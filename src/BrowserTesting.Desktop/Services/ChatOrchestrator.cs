using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BrowserTesting.Desktop.Classes;
using BrowserTesting.Desktop.Models;

namespace BrowserTesting.Desktop.Services;

public sealed class ChatOrchestrator(
    SqliteChatRepository repository,
    LmStudioLlmClient llmClient,
    BrowserSessionManager browserSessionManager,
    AppSettings settings)
{
    private readonly ToolCatalog tools = new(repository, browserSessionManager);

    public Task InitializeAsync(CancellationToken cancellationToken) => repository.InitializeAsync(cancellationToken);
    public Task<IReadOnlyList<ChatSessionSummary>> ListChatsAsync(CancellationToken cancellationToken) => repository.ListChatsAsync(cancellationToken);
    public Task<ChatSession> CreateChatAsync(string? title, CancellationToken cancellationToken) => repository.CreateChatAsync(title, cancellationToken);

    public async Task<ChatSession?> LoadChatAsync(Guid chatId, Action<OrchestratorUpdate>? onUpdate, CancellationToken cancellationToken)
    {
        var chat = await repository.GetChatAsync(chatId, cancellationToken);
        if (chat is not null)
        {
            onUpdate?.Invoke(new ChatLoaded(chat));
        }

        return chat;
    }

    public async Task<BrowserSessionSnapshot?> CloseBrowserAsync(Guid runId, Action<OrchestratorUpdate>? onUpdate, CancellationToken cancellationToken)
    {
        await browserSessionManager.CloseBrowserAsync(runId, cancellationToken);
        var snapshot = await browserSessionManager.GetSnapshotAsync(runId, cancellationToken);
        if (snapshot is null)
        {
            return null;
        }

        await repository.SaveBrowserSnapshotAsync(runId, snapshot, cancellationToken);
        onUpdate?.Invoke(new BrowserSnapshotUpdated(runId, snapshot));
        return snapshot;
    }

    public async Task<TestRun> RunPromptAsync(Guid chatId, string prompt, Action<OrchestratorUpdate> onUpdate, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("Prompt cannot be empty.", nameof(prompt));
        }

        var chat = await LoadRequiredChatAsync(chatId, cancellationToken);
        await AddUserMessageAsync(chat, prompt, onUpdate, cancellationToken);
        var run = await repository.CreateRunAsync(chatId, prompt, cancellationToken);
        onUpdate(new RunUpdated(run));
        await UpdateDefaultTitleAsync(chat, prompt, cancellationToken);

        try
        {
            var failures = new RepeatedFailureTracker();
            var passiveLoop = new PassiveToolLoopTracker();
            var stalledTurns = 0;
            for (var iteration = 0; ; iteration++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (chat, run) = await LoadRunStateAsync(chatId, run.Id, cancellationToken);
                await SaveRunStatusAsync(run, TestRunStatus.Running, onUpdate, cancellationToken);

                var assistantEntry = await AddTimelineAsync(chatId, run.Id, TimelineItemKind.AssistantMessage, "assistant", string.Empty, onUpdate, cancellationToken);
                var toolCalls = await StreamAssistantTurnAsync(chat, run, assistantEntry, Math.Max(settings.MaxToolIterations - iteration, 0), onUpdate, cancellationToken);
                if (toolCalls.Count == 0)
                {
                    stalledTurns = await HandleNoToolTurnAsync(chatId, run.Id, assistantEntry, stalledTurns, onUpdate, cancellationToken);
                    continue;
                }

                stalledTurns = 0;
                await SaveRunStatusAsync(run, TestRunStatus.WaitingForTool, onUpdate, cancellationToken);
                var restart = false;
                foreach (var toolCall in toolCalls.OrderBy(call => call.Index))
                {
                    var outcome = await ExecuteAndRecordToolAsync(chatId, run.Id, toolCall, failures, passiveLoop, onUpdate, cancellationToken);
                    run = outcome.Run;
                    if (outcome.Ended)
                    {
                        return run;
                    }

                    if (outcome.Restart)
                    {
                        restart = true;
                        break;
                    }
                }

                if (!restart)
                {
                    continue;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run.Status = TestRunStatus.Failed;
            run.FailureReason = ex.Message;
            run.CompletedAtUtc = DateTime.UtcNow;
            run.UpdatedAtUtc = DateTime.UtcNow;
            await repository.UpdateRunAsync(run, cancellationToken);
            onUpdate(new RunUpdated(run));
            onUpdate(new OrchestrationError(ex.Message));
            return run;
        }
    }

    private async Task<IReadOnlyList<LlmToolCall>> StreamAssistantTurnAsync(
        ChatSession chat,
        TestRun run,
        TimelineEntry assistantEntry,
        int turnsRemaining,
        Action<OrchestratorUpdate> onUpdate,
        CancellationToken cancellationToken)
    {
        var builders = new Dictionary<int, ToolCallAccumulator>();
        var request = BuildRequest(chat, run, turnsRemaining, settings.CreateConnectionSettings());
        await foreach (var streamEvent in llmClient.StreamCompletionAsync(request, cancellationToken))
        {
            switch (streamEvent)
            {
                case LlmTextDelta textDelta:
                    assistantEntry.Content += textDelta.Content;
                    await repository.UpdateTimelineEntryAsync(assistantEntry, cancellationToken);
                    onUpdate(new TimelineEntryUpserted(assistantEntry));
                    break;
                case LlmToolCallDelta toolDelta:
                    if (!builders.TryGetValue(toolDelta.Index, out var builder)) builders[toolDelta.Index] = builder = new(toolDelta.Index);
                    builder.Append(toolDelta);
                    break;
                case LlmStreamFaulted faulted:
                    throw new InvalidOperationException(faulted.Message);
            }
        }

        return builders.Values.Select(builder => builder.Build()).ToArray();
    }

    private async Task<ToolOutcome> ExecuteAndRecordToolAsync(
        Guid chatId,
        Guid runId,
        LlmToolCall toolCall,
        RepeatedFailureTracker failures,
        PassiveToolLoopTracker passiveLoop,
        Action<OrchestratorUpdate> onUpdate,
        CancellationToken cancellationToken)
    {
        var arguments = ParseArguments(toolCall);
        await AddTimelineAsync(chatId, runId, TimelineItemKind.ToolCallStarted, "assistant", $"Calling `{toolCall.Name}`...", onUpdate, cancellationToken, toolCall.Id, toolCall.Name, arguments.ToJsonString());
        var result = await ExecuteToolSafelyAsync(chatId, runId, toolCall.Name, arguments, cancellationToken);
        var run = await RefreshRunStateAsync(chatId, runId, onUpdate, cancellationToken);
        var repeatedAttempt = result.Success ? 0 : failures.RegisterFailure(BuildFailureSignature(toolCall.Name, result.NormalizedArguments ?? arguments, result.Error ?? result.Summary));
        var passiveAttempt = result.Success ? passiveLoop.Register(toolCall.Name) : 0;
        if (result.Success)
        {
            failures.Reset();
        }
        else
        {
            passiveLoop.Reset();
        }

        await AddTimelineAsync(chatId, runId, TimelineItemKind.ToolCallFinished, "tool", result.Summary, onUpdate, cancellationToken, toolCall.Id, toolCall.Name, BuildToolResultMetadata(result, repeatedAttempt).ToJsonString());
        run = await RefreshRunStateAsync(chatId, runId, onUpdate, cancellationToken);
        if (passiveAttempt >= 3)
        {
            await AddSystemNoticeAsync(chatId, runId, BuildPassiveToolLoopNotice(toolCall.Name, passiveAttempt), onUpdate, cancellationToken);
        }

        if (result.Success && toolCall.Name == "end_task")
        {
            run.Status = run.Goals.Any(goal => goal.Status == GoalStatus.Failed) ? TestRunStatus.Failed : TestRunStatus.Completed;
            run.FailureReason = run.Status == TestRunStatus.Failed ? ExtractEndTaskSummary(result) ?? "One or more goals failed." : null;
            run.CompletedAtUtc = DateTime.UtcNow;
            run.UpdatedAtUtc = DateTime.UtcNow;
            await repository.UpdateRunAsync(run, cancellationToken);
            onUpdate(new RunUpdated(run));
            return new(run, Ended: true, Restart: false);
        }

        if (result.Success)
        {
            return new(run, Ended: false, Restart: false);
        }

        await AddSystemNoticeAsync(chatId, runId, BuildToolFailureNotice(toolCall.Name, result, repeatedAttempt), onUpdate, cancellationToken);
        await SaveRunStatusAsync(run, TestRunStatus.Running, onUpdate, cancellationToken, clearCompletion: true);
        return new(run, Ended: false, Restart: true);
    }

    private async Task<ToolExecutionResult> ExecuteToolSafelyAsync(Guid chatId, Guid runId, string name, JsonObject arguments, CancellationToken cancellationToken)
    {
        try
        {
            return await tools.ExecuteAsync(new ToolInvocationContext
            {
                ChatSessionId = chatId,
                TestRunId = runId,
                LaunchHeadless = settings.LaunchHeadless,
                BrowserSnapshot = (await repository.GetChatAsync(chatId, cancellationToken))?.Runs.SingleOrDefault(run => run.Id == runId)?.BrowserSnapshot,
            }, name, arguments, cancellationToken);
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Failed($"Tool `{name}` failed.", ex.Message);
        }
    }

    private async Task<int> HandleNoToolTurnAsync(Guid chatId, Guid runId, TimelineEntry assistantEntry, int stalledTurns, Action<OrchestratorUpdate> onUpdate, CancellationToken cancellationToken)
    {
        stalledTurns++;
        if (string.IsNullOrWhiteSpace(assistantEntry.Content))
        {
            await AddSystemNoticeAsync(chatId, runId, BuildEmptyTurnNotice(stalledTurns), onUpdate, cancellationToken);
        }

        var run = await RefreshRunStateAsync(chatId, runId, onUpdate, cancellationToken);
        await AddSystemNoticeAsync(chatId, runId, BuildNoToolNotice(run, stalledTurns), onUpdate, cancellationToken);
        await SaveRunStatusAsync(run, TestRunStatus.Running, onUpdate, cancellationToken, clearCompletion: true);
        return stalledTurns;
    }

    private async Task AddUserMessageAsync(ChatSession chat, string prompt, Action<OrchestratorUpdate> onUpdate, CancellationToken cancellationToken)
    {
        chat.UpdatedAtUtc = DateTime.UtcNow;
        await repository.UpdateChatAsync(chat, cancellationToken);
        await AddTimelineAsync(chat.Id, null, TimelineItemKind.UserMessage, "user", prompt.Trim(), onUpdate, cancellationToken);
    }

    private async Task<TimelineEntry> AddTimelineAsync(
        Guid chatId,
        Guid? runId,
        TimelineItemKind kind,
        string role,
        string content,
        Action<OrchestratorUpdate> onUpdate,
        CancellationToken cancellationToken,
        string? toolCallId = null,
        string? toolName = null,
        string? metadataJson = null)
    {
        var entry = await repository.AddTimelineEntryAsync(new TimelineEntry
        {
            Id = Guid.NewGuid(),
            ChatSessionId = chatId,
            TestRunId = runId,
            Kind = kind,
            Role = role,
            Content = content,
            ToolCallId = toolCallId,
            ToolName = toolName,
            MetadataJson = metadataJson,
            CreatedAtUtc = DateTime.UtcNow,
        }, cancellationToken);
        onUpdate(new TimelineEntryUpserted(entry));
        return entry;
    }

    private async Task SaveRunStatusAsync(TestRun run, TestRunStatus status, Action<OrchestratorUpdate> onUpdate, CancellationToken cancellationToken, bool clearCompletion = false)
    {
        run.Status = status;
        run.UpdatedAtUtc = DateTime.UtcNow;
        if (clearCompletion)
        {
            run.FailureReason = null;
            run.CompletedAtUtc = null;
        }

        await repository.UpdateRunAsync(run, cancellationToken);
        onUpdate(new RunUpdated(run));
    }

    private LlmRequest BuildRequest(ChatSession chat, TestRun run, int turnsRemaining, LlmConnectionSettings connection)
    {
        var forceEndTask = CanCallEndTask(run);
        return new()
        {
            Connection = connection,
            Tools = tools.Definitions,
            Messages = BuildConversation(chat, run, turnsRemaining, connection),
            ToolChoiceMode = forceEndTask ? LlmToolChoiceMode.ForceFunction : LlmToolChoiceMode.Required,
            ForcedToolName = forceEndTask ? "end_task" : null,
        };
    }

    private IReadOnlyList<LlmConversationMessage> BuildConversation(ChatSession chat, TestRun activeRun, int turnsRemaining, LlmConnectionSettings connection)
    {
        var messages = new List<LlmConversationMessage>
        {
            new() { Role = connection.Provider == LlmProvider.OpenAi ? "developer" : "system", Content = BuildSystemPrompt(chat, activeRun, turnsRemaining) },
            new() { Role = "user", Content = activeRun.UserPrompt },
        };
        return messages;
    }

    private string BuildSystemPrompt(ChatSession chat, TestRun activeRun, int turnsRemaining) =>
        string.Join(Environment.NewLine,
        [
            "You are a browser-testing agent. Drive the browser with tools and maintain the active-run goal ledger.",
            "Rules:",
            "- Use tools every turn. Do not narrate progress instead of calling a tool.",
            "- If the user listed goals, create each distinct requested goal once. Do not recreate semantically equivalent goals.",
            "- Do not call open_browser when active_run.browser.state is Active; continue from the current page.",
            "- After inspect_page returns usable refs, act on those refs. Do not inspect the same unchanged page repeatedly.",
            "- Refs are page-local. If the current URL differs from the inspection URL, inspect the current page before click_ref/type_ref.",
            "- When observed evidence satisfies a pending goal, mark that goal passed immediately before moving to later dependent work.",
            "- Use active_run.last_page_inspection and active_run.recent_page_inspections as valid evidence from this run.",
            "- Resolve every active-run goal as passed or failed with observed evidence.",
            "- When every active-run goal is passed or failed, call end_task next.",
            "- Prefer inspect_page, then click_ref/type_ref. Use selector tools only when refs are insufficient.",
            "- inspect_page visible_text or get_text/get_html must show expected text before passing verification goals.",
            "- On tool failure, change strategy. Do not repeat identical failing calls.",
            "active_run:",
            BuildActiveRunContext(chat, activeRun, turnsRemaining).ToJsonString(),
        ]);

    private JsonObject BuildActiveRunContext(ChatSession chat, TestRun activeRun, int turnsRemaining) => new()
    {
        ["run_id"] = activeRun.Id.ToString(),
        ["status"] = activeRun.Status.ToString(),
        ["user_prompt"] = Truncate(activeRun.UserPrompt, 2000),
        ["expected_goal_count"] = GetExpectedGoalCount(activeRun.UserPrompt),
        ["active_goal_count"] = activeRun.Goals.Count,
        ["completion_gate"] = CanCallEndTask(activeRun) ? "All active-run goals are terminal. The next tool call must be end_task." : BuildCompletionGateMessage(activeRun),
        ["soft_turn_budget_remaining"] = turnsRemaining,
        ["goals"] = BuildGoalLedger(activeRun.Goals, includeIds: true),
        ["browser"] = BuildBrowserSnapshotNode(activeRun.BrowserSnapshot),
        ["recent_tool_outcomes"] = GetRecentToolOutcomes(chat, activeRun.Id),
        ["last_page_inspection"] = GetLastPageInspection(chat, activeRun.Id),
        ["recent_page_inspections"] = GetRecentPageInspections(chat, activeRun.Id),
    };

    private async Task<ChatSession> LoadRequiredChatAsync(Guid chatId, CancellationToken cancellationToken) =>
        await repository.GetChatAsync(chatId, cancellationToken) ?? throw new InvalidOperationException($"Chat {chatId} was not found.");

    private async Task<(ChatSession Chat, TestRun Run)> LoadRunStateAsync(Guid chatId, Guid runId, CancellationToken cancellationToken)
    {
        var chat = await LoadRequiredChatAsync(chatId, cancellationToken);
        return (chat, chat.Runs.Single(run => run.Id == runId));
    }

    private async Task UpdateDefaultTitleAsync(ChatSession chat, string prompt, CancellationToken cancellationToken)
    {
        if (chat.Title is not ("New Chat" or "Untitled Chat"))
        {
            return;
        }

        chat.Title = BuildChatTitle(prompt);
        chat.UpdatedAtUtc = DateTime.UtcNow;
        await repository.UpdateChatAsync(chat, cancellationToken);
    }

    private async Task<TestRun> RefreshRunStateAsync(Guid chatId, Guid runId, Action<OrchestratorUpdate> onUpdate, CancellationToken cancellationToken)
    {
        var (_, run) = await LoadRunStateAsync(chatId, runId, cancellationToken);
        onUpdate(new GoalsUpdated(run.Id, run.Goals));
        onUpdate(new BrowserSnapshotUpdated(run.Id, run.BrowserSnapshot));
        onUpdate(new RunUpdated(run));
        return run;
    }

    private Task AddSystemNoticeAsync(Guid chatId, Guid runId, string content, Action<OrchestratorUpdate> onUpdate, CancellationToken cancellationToken) =>
        AddTimelineAsync(chatId, runId, TimelineItemKind.SystemNotice, "system", content, onUpdate, cancellationToken);

    private static bool CanCallEndTask(TestRun run)
    {
        var expected = GetExpectedGoalCount(run.UserPrompt);
        return run.Goals.Count > 0 && (expected is null || run.Goals.Count >= expected) && run.Goals.All(goal => goal.Status is GoalStatus.Passed or GoalStatus.Failed);
    }

    private static string BuildCompletionGateMessage(TestRun run)
    {
        var expected = GetExpectedGoalCount(run.UserPrompt);
        if (expected is not null && run.Goals.Count < expected)
        {
            return $"The user requested {expected} goals, but only {run.Goals.Count} active-run goals exist. Create the missing distinct goals before end_task.";
        }

        if (run.Goals.Count == 0)
        {
            return "No active-run goals exist. Create one or more goals before browser work.";
        }

        var unresolved = run.Goals.Where(goal => goal.Status is GoalStatus.Pending or GoalStatus.Running).Select(goal => goal.Id.ToString()).ToArray();
        return unresolved.Length == 0 ? "Every active-run goal must be terminal before end_task." : $"Resolve these active-run goal IDs before end_task: {string.Join(", ", unresolved)}";
    }

    private static int? GetExpectedGoalCount(string prompt)
    {
        var match = Regex.Match(prompt, @"\b(?:make|create|have|want)\s+(?<count>\d+|one|two|three|four|five|six|seven|eight|nine|ten)\s+goals?\b", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            match = Regex.Match(prompt, @"\b(?<count>\d+|one|two|three|four|five|six|seven|eight|nine|ten)\s+goals?\b", RegexOptions.IgnoreCase);
        }

        if (!match.Success)
        {
            return null;
        }

        return int.TryParse(match.Groups["count"].Value, out var numeric)
            ? Math.Clamp(numeric, 1, 25)
            : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10 }.GetValueOrDefault(match.Groups["count"].Value);
    }

    private static JsonArray BuildGoalLedger(IEnumerable<GoalItem> goals, bool includeIds)
    {
        var ledger = new JsonArray();
        foreach (var goal in goals)
        {
            var item = new JsonObject
            {
                ["title"] = Truncate(goal.Title, 300),
                ["success_criteria"] = Truncate(goal.SuccessCriteria, 500),
                ["status"] = goal.Status.ToString(),
                ["note"] = Truncate(goal.Note, 500),
                ["evidence"] = Truncate(goal.Evidence, 800),
            };
            if (includeIds)
            {
                item["id"] = goal.Id.ToString();
            }

            ledger.Add(item);
        }

        return ledger;
    }

    private static JsonObject BuildBrowserSnapshotNode(BrowserSessionSnapshot snapshot) => new()
    {
        ["current_url"] = Truncate(snapshot.CurrentUrl, 500),
        ["page_title"] = Truncate(snapshot.PageTitle, 300),
        ["state"] = snapshot.State.ToString(),
        ["tab_count"] = snapshot.Tabs.Count,
        ["tabs"] = new JsonArray(snapshot.Tabs.Take(5).Select(tab => new JsonObject
        {
            ["title"] = Truncate(tab.Title, 200),
            ["url"] = Truncate(tab.Url, 500),
            ["is_selected"] = tab.IsSelected,
        }).ToArray()),
    };

    private static string? ExtractEndTaskSummary(ToolExecutionResult result) =>
        result.Data is JsonObject data && data["summary"] is JsonValue value && value.TryGetValue<string>(out var summary) && !string.IsNullOrWhiteSpace(summary)
            ? summary.Trim()
            : null;

    private static JsonObject ParseArguments(LlmToolCall toolCall)
    {
        if (string.IsNullOrWhiteSpace(toolCall.ArgumentsJson))
        {
            return [];
        }

        try
        {
            return JsonNode.Parse(toolCall.ArgumentsJson)?.AsObject() ?? [];
        }
        catch
        {
            return new JsonObject { ["raw"] = toolCall.ArgumentsJson };
        }
    }

    private static string BuildEmptyTurnNotice(int stalledTurns) => stalledTurns switch
    {
        1 => "Your last reply was empty while goals are still unresolved. Call exactly one tool on the next turn. Do not return an empty response.",
        2 => "You have produced multiple empty or no-tool turns. Stop narrating and emit exactly one structured tool call now.",
        _ => "Repeated empty or no-tool turns detected. If you cannot proceed, use goal evidence to fail the blocked goal instead of returning another empty reply.",
    };

    private static string BuildNoToolNotice(TestRun run, int stalledTurns)
    {
        var prefix = stalledTurns > 1 ? $"No structured tool call was emitted for {stalledTurns} consecutive turns. " : "No structured tool call was emitted. ";
        if (CanCallEndTask(run))
        {
            return prefix + "All active-run goals are passed or failed. Call end_task now with paragraph-length summary, paragraph-length test_results, evidence, and remaining_work.";
        }

        if (run.Goals.Count == 0)
        {
            return prefix + "No active-run goals exist. Call create_goal before browser work.";
        }

        var unresolved = run.Goals.Where(goal => goal.Status is GoalStatus.Pending or GoalStatus.Running).Select(goal => goal.Id);
        return prefix + $"Goals are unresolved ({string.Join(", ", unresolved)}). Use tools to inspect evidence, then mark each goal passed or failed.";
    }

    private static string BuildPassiveToolLoopNotice(string toolName, int attemptCount) =>
        $"Passive tool loop detected after {attemptCount} consecutive passive calls ending with `{toolName}`. Use active_run context instead of calling list_goals/open_browser/inspect_page again. The next tool should change page or goal state.";

    private static JsonObject BuildToolResultMetadata(ToolExecutionResult result, int repeatedAttemptCount) => new()
    {
        ["success"] = result.Success,
        ["summary"] = Truncate(result.Summary, 500),
        ["error"] = Truncate(result.Error, 1000),
        ["hint"] = Truncate(result.Hint, 1000),
        ["repeated_attempt_count"] = repeatedAttemptCount,
        ["data"] = CompactJsonNode(result.Data),
        ["normalized_arguments"] = CompactJsonNode(result.NormalizedArguments),
        ["expected_arguments"] = CompactJsonNode(result.ExpectedArguments),
        ["example_arguments"] = CompactJsonNode(result.ExampleArguments),
    };

    private static string BuildToolFailureNotice(string toolName, ToolExecutionResult result, int repeatedAttemptCount)
    {
        var builder = new StringBuilder(repeatedAttemptCount >= 2
            ? $"Repeated identical tool failure detected for `{toolName}` (attempt {repeatedAttemptCount}). Do not repeat the same call with the same arguments. "
            : $"The tool `{toolName}` failed. ");
        builder.Append($"Error: {Truncate(result.Error ?? result.Summary, 1000)}. ");
        if (!string.IsNullOrWhiteSpace(result.Hint))
        {
            builder.Append($"Hint: {Truncate(result.Hint, 1000)}. ");
        }

        if (result.NormalizedArguments is not null)
        {
            builder.Append($"Normalized arguments: {CompactJsonNode(result.NormalizedArguments, 1000)?.ToJsonString()}. ");
        }

        if (result.ExampleArguments is not null)
        {
            builder.Append($"Example arguments: {CompactJsonNode(result.ExampleArguments, 1000)?.ToJsonString()}. ");
        }

        builder.Append(toolName is "mark_goal_pass" or "mark_goal_fail" or "update_goal_status"
            ? "Next-step options: use a Pending or Running goal ID from active_run.goals, inspect prior active-run evidence if needed, or call end_task if every goal is already terminal."
            : "Next-step options: inspect page state, change the argument shape, try a different locator strategy, use a less brittle inspection tool, or fail the goal with evidence if the page blocks further progress.");
        return builder.ToString();
    }

    private static string BuildFailureSignature(string toolName, JsonNode? arguments, string? error) => $"{toolName}|{CanonicalizeJson(arguments)}|{error ?? string.Empty}";
    private static string CanonicalizeJson(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject obj => $"{{{string.Join(",", obj.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"\"{pair.Key}\":{CanonicalizeJson(pair.Value)}"))}}}",
        JsonArray array => $"[{string.Join(",", array.Select(CanonicalizeJson))}]",
        _ => node.ToJsonString(),
    };

    private static JsonNode? CompactJsonNode(JsonNode? node, int maxStringLength = 1000, int maxArrayItems = 40, int maxObjectProperties = 80) => node switch
    {
        null => null,
        JsonValue value when value.TryGetValue<string>(out var text) => JsonValue.Create(Truncate(text, maxStringLength)),
        JsonValue => node.DeepClone(),
        JsonArray array => CompactArray(array, maxStringLength, maxArrayItems, maxObjectProperties),
        JsonObject obj => CompactObject(obj, maxStringLength, maxArrayItems, maxObjectProperties),
        _ => node.DeepClone(),
    };

    private static JsonArray CompactArray(JsonArray array, int maxStringLength, int maxArrayItems, int maxObjectProperties)
    {
        var clone = new JsonArray(array.Take(maxArrayItems).Select(item => CompactJsonNode(item, maxStringLength, maxArrayItems, maxObjectProperties)).ToArray());
        if (array.Count > maxArrayItems)
        {
            clone.Add(new JsonObject { ["_truncated"] = true, ["omitted_count"] = array.Count - maxArrayItems });
        }

        return clone;
    }

    private static JsonObject CompactObject(JsonObject obj, int maxStringLength, int maxArrayItems, int maxObjectProperties)
    {
        var clone = new JsonObject();
        foreach (var property in obj.Take(maxObjectProperties))
        {
            clone[property.Key] = CompactJsonNode(property.Value, maxStringLength, maxArrayItems, maxObjectProperties);
        }

        if (obj.Count > maxObjectProperties)
        {
            clone["_truncated"] = true;
            clone["omitted_property_count"] = obj.Count - maxObjectProperties;
        }

        return clone;
    }

    private static JsonArray GetRecentToolOutcomes(ChatSession chat, Guid runId)
    {
        var outcomes = new JsonArray();
        foreach (var entry in chat.Timeline.Where(candidate => candidate.TestRunId == runId && candidate.Kind == TimelineItemKind.ToolCallFinished).OrderByDescending(candidate => candidate.Sequence).Take(6).Reverse())
        {
            var metadata = ParseMetadata(entry.MetadataJson);
            outcomes.Add(new JsonObject
            {
                ["tool_name"] = entry.ToolName,
                ["summary"] = Truncate(entry.Content, 500),
                ["success"] = metadata?["success"]?.DeepClone(),
                ["error"] = CompactJsonNode(metadata?["error"], 1000),
                ["hint"] = CompactJsonNode(metadata?["hint"], 1000),
                ["repeated_attempt_count"] = metadata?["repeated_attempt_count"]?.DeepClone(),
                ["normalized_arguments"] = CompactJsonNode(metadata?["normalized_arguments"], 1000),
                ["data"] = ShouldIncludeRecentToolData(entry.ToolName) ? CompactJsonNode(metadata?["data"], 700, 20, 60) : null,
            });
        }

        return outcomes;
    }

    private static JsonNode? GetLastPageInspection(ChatSession chat, Guid runId)
    {
        var entry = chat.Timeline.Where(candidate => candidate.TestRunId == runId && candidate.Kind == TimelineItemKind.ToolCallFinished && candidate.ToolName == "inspect_page")
            .OrderByDescending(candidate => candidate.Sequence).FirstOrDefault(candidate => ParseMetadata(candidate.MetadataJson)?["success"]?.GetValue<bool>() == true);
        return entry is null ? null : CompactJsonNode(ParseMetadata(entry.MetadataJson)?["data"], 700, 30, 80);
    }

    private static JsonArray GetRecentPageInspections(ChatSession chat, Guid runId)
    {
        var inspections = new List<JsonNode>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in chat.Timeline.Where(candidate => candidate.TestRunId == runId && candidate.Kind == TimelineItemKind.ToolCallFinished && candidate.ToolName == "inspect_page").OrderByDescending(candidate => candidate.Sequence))
        {
            var metadata = ParseMetadata(entry.MetadataJson);
            if (metadata?["success"]?.GetValue<bool>() != true || metadata["data"] is not JsonObject data)
            {
                continue;
            }

            var key = NormalizePageEvidenceUrl(data["url"]?.GetValue<string>()) ?? entry.Sequence.ToString();
            if (seenUrls.Add(key) && CompactJsonNode(data, 900, 30, 80) is { } compact)
            {
                inspections.Add(compact);
            }

            if (inspections.Count >= 5)
            {
                break;
            }
        }

        inspections.Reverse();
        return new JsonArray(inspections.ToArray());
    }

    private static bool ShouldIncludeRecentToolData(string? toolName) =>
        toolName is "inspect_page" or "find_element" or "find_elements" or "get_page_state" or "get_text" or "get_attribute" or "execute_javascript" or "wait_for_text" or "wait_for_navigation" or "click" or "click_ref" or "type_ref" or "open_browser" or "create_goal" or "update_goal_status" or "mark_goal_pass" or "mark_goal_fail";

    private static JsonObject? ParseMetadata(string? metadataJson)
    {
        try
        {
            return string.IsNullOrWhiteSpace(metadataJson) ? null : JsonNode.Parse(metadataJson)?.AsObject();
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizePageEvidenceUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var hash = url.IndexOf('#', StringComparison.Ordinal);
            return (hash >= 0 ? url[..hash] : url).TrimEnd('/');
        }

        return new UriBuilder(uri) { Fragment = string.Empty }.Uri.AbsoluteUri.TrimEnd('/');
    }

    private static string? Truncate(string? value, int maxLength) => value is null || value.Length <= maxLength ? value : value[..maxLength];
    private static string BuildChatTitle(string prompt) => string.Join(' ', prompt.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(6)) + (prompt.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length > 6 ? "..." : string.Empty);

    private sealed class ToolCallAccumulator(int index)
    {
        private readonly StringBuilder arguments = new();
        private readonly StringBuilder identifier = new();
        private readonly StringBuilder name = new();
        public int Index => index;
        public void Append(LlmToolCallDelta delta)
        {
            identifier.Append(delta.IdPart);
            name.Append(delta.NamePart);
            arguments.Append(delta.ArgumentsPart);
        }
        public LlmToolCall Build() => new() { Index = index, Id = identifier.Length == 0 ? Guid.NewGuid().ToString("N") : identifier.ToString(), Name = name.Length == 0 ? "unknown_tool" : name.ToString(), ArgumentsJson = arguments.Length == 0 ? "{}" : arguments.ToString() };
    }

    private sealed class RepeatedFailureTracker
    {
        private string? lastSignature;
        private int streak;
        public int RegisterFailure(string signature) => string.Equals(lastSignature, signature, StringComparison.Ordinal) ? ++streak : (lastSignature = signature) is null ? streak = 1 : streak = 1;
        public void Reset() => (lastSignature, streak) = (null, 0);
    }

    private sealed class PassiveToolLoopTracker
    {
        private static readonly HashSet<string> PassiveToolNames = ["list_goals", "inspect_page", "get_page_state", "open_browser"];
        private int streak;
        public int Register(string toolName) => PassiveToolNames.Contains(toolName) ? ++streak : streak = 0;
        public void Reset() => streak = 0;
    }

    private sealed record ToolOutcome(TestRun Run, bool Ended, bool Restart);
}

public abstract record OrchestratorUpdate;
public sealed record ChatLoaded(ChatSession Chat) : OrchestratorUpdate;
public sealed record TimelineEntryUpserted(TimelineEntry Entry) : OrchestratorUpdate;
public sealed record RunUpdated(TestRun Run) : OrchestratorUpdate;
public sealed record BrowserSnapshotUpdated(Guid RunId, BrowserSessionSnapshot Snapshot) : OrchestratorUpdate;
public sealed record GoalsUpdated(Guid RunId, IReadOnlyList<GoalItem> Goals) : OrchestratorUpdate;
public sealed record OrchestrationError(string Message) : OrchestratorUpdate;

internal sealed class ToolCatalog
{
    private const int MinimumEndTaskNarrativeLength = 120;
    private static readonly HashSet<string> LocatorToolNames = ["find_element", "find_elements", "click", "double_click", "type_text", "clear", "send_keys", "submit", "select_option", "hover", "scroll_into_view", "get_text", "get_attribute", "get_html", "wait_for_element"];
    private readonly BrowserSessionManager browserSessionManager;
    private readonly SqliteChatRepository repository;
    private readonly IReadOnlyList<LlmToolDefinition> definitions;

    public ToolCatalog(SqliteChatRepository repository, BrowserSessionManager browserSessionManager)
    {
        this.repository = repository;
        this.browserSessionManager = browserSessionManager;
        definitions =
        [
            Define("open_browser", "Open a Chrome browser for the active run.", Obj(P("url", "string"), P("profile_name", "string"))),
            Define("close_browser", "Close the browser for the active run.", Obj()),
            Define("goto_url", "Navigate the browser to a URL.", Obj(P("url", "string", true))),
            Define("back", "Navigate backward in browser history.", Obj()),
            Define("forward", "Navigate forward in browser history.", Obj()),
            Define("refresh", "Refresh the current page.", Obj()),
            Define("get_page_state", "Return the current URL, title, and tab summary.", Obj()),
            Define("find_element", LocatorDescription("Find the first matching element"), LocatorSchema()),
            Define("find_elements", LocatorDescription("Find all matching elements"), LocatorSchema()),
            Define("inspect_page", "Return visible page text plus a compact list of visible actionable elements with page-local refs for click_ref/type_ref. Use before guessing selectors.", Obj(P("max_elements", "integer"), P("include_hidden", "boolean"))),
            Define("click_ref", "Click an element ref returned by the latest inspect_page for the current page URL.", Obj(P("ref", "string", true))),
            Define("type_ref", "Type text into an element ref returned by the latest inspect_page for the current page URL.", Obj(P("ref", "string", true), P("text", "string", true), P("clear_first", "boolean"))),
            Define("click", LocatorDescription("Click an element"), LocatorSchema()),
            Define("double_click", LocatorDescription("Double click an element"), LocatorSchema()),
            Define("type_text", "Type text into an element. Use {\"locator\":{\"strategy\":\"...\",\"value\":\"...\"},\"text\":\"...\"}.", LocatorSchema(P("text", "string", true), P("clear_first", "boolean"))),
            Define("clear", LocatorDescription("Clear the value of an input element"), LocatorSchema()),
            Define("send_keys", "Send raw keys to an element. Use {\"locator\":{\"strategy\":\"...\",\"value\":\"...\"},\"keys\":\"...\"}.", LocatorSchema(P("keys", "string", true))),
            Define("submit", LocatorDescription("Submit a form element"), LocatorSchema()),
            Define("select_option", "Select an option in a select element by text or value. Use {\"locator\":{\"strategy\":\"...\",\"value\":\"...\"},\"text\":\"...\"} or \"value\".", LocatorSchema(P("text", "string"), P("value", "string"))),
            Define("hover", LocatorDescription("Move the mouse over an element"), LocatorSchema()),
            Define("scroll_into_view", LocatorDescription("Scroll until the element is visible"), LocatorSchema()),
            Define("get_text", LocatorDescription("Read text from an element"), LocatorSchema()),
            Define("get_attribute", "Read an attribute from an element. Use {\"locator\":{\"strategy\":\"...\",\"value\":\"...\"},\"attribute\":\"...\"}.", LocatorSchema(P("attribute", "string", true))),
            Define("get_html", "Return bounded outer HTML from an optional element locator, or bounded page HTML when no locator is supplied. Prefer inspect_page for compact actionable refs.", LocatorSchema(required: false)),
            Define("execute_javascript", "Run JavaScript in the current page. Optional arguments must be JSON primitive values passed positionally.", Obj(P("script", "string", true), A("arguments", PrimitiveValueSchema()))),
            Define("wait_for_element", "Wait until an element exists. Use {\"locator\":{\"strategy\":\"...\",\"value\":\"...\"},\"timeout_ms\":5000}.", LocatorSchema(P("timeout_ms", "integer"))),
            Define("wait_for_text", "Wait until page source contains text.", Obj(P("text", "string", true), P("timeout_ms", "integer"))),
            Define("wait_for_navigation", "Wait until the URL contains expected text.", Obj(P("url_contains", "string", true), P("timeout_ms", "integer"))),
            Define("sleep", "Pause execution briefly.", Obj(P("milliseconds", "integer", true))),
            Define("create_goal", "Create a new test goal for the active run.", Obj(P("title", "string", true), P("success_criteria", "string", true))),
            Define("update_goal_status", "Update a goal status to pending, running, passed, or failed.", Obj(P("goal_id", "string", true), P("status", "string", true), P("note", "string"), P("evidence", "string"))),
            Define("mark_goal_pass", "Mark a goal as passed with evidence.", Obj(P("goal_id", "string", true), P("evidence", "string", true))),
            Define("mark_goal_fail", "Mark a goal as failed with reason and evidence.", Obj(P("goal_id", "string", true), P("reason", "string", true), P("evidence", "string"))),
            Define("list_goals", "List all goals for the active run.", Obj()),
            Define("end_task", "Finish the active run after every goal is passed or failed. Include final text summarizing what was done and the test results. Use only when all active-run goals are terminal.", Obj(
                E("outcome", ["completed", "failed"], true),
                P("summary", "string", true, "One or two paragraphs, at least 120 characters, summarizing what you did during the run. Do not use a terse phrase."),
                P("test_results", "string", true, "One or two paragraphs, at least 120 characters, summarizing which tests or goals passed or failed and why. Do not use a terse phrase."),
                P("evidence", "string", true),
                P("remaining_work", "string", true))),
        ];
    }

    public IReadOnlyList<LlmToolDefinition> Definitions => definitions;

    public async Task<ToolExecutionResult> ExecuteAsync(ToolInvocationContext context, string toolName, JsonObject arguments, CancellationToken cancellationToken)
    {
        var normalized = NormalizeArguments(toolName, arguments);
        var result = toolName switch
        {
            "create_goal" => await CreateGoalAsync(context, normalized, cancellationToken),
            "update_goal_status" => await UpdateGoalStatusAsync(context, normalized, cancellationToken),
            "mark_goal_pass" => await UpdateGoalStatusAsync(context, CopyWith(normalized, ("status", "passed")), cancellationToken),
            "mark_goal_fail" => await UpdateGoalStatusAsync(context, CopyWith(normalized, ("status", "failed"), ("note", GetString(normalized, "reason"))), cancellationToken),
            "list_goals" => await ListGoalsAsync(context, cancellationToken),
            "end_task" => await EndTaskAsync(context, normalized, cancellationToken),
            _ => await browserSessionManager.ExecuteBrowserToolAsync(context.TestRunId, toolName, normalized, context.BrowserSnapshot, context.LaunchHeadless, cancellationToken),
        };

        if (result.NormalizedArguments is null && !JsonNode.DeepEquals(arguments, normalized))
        {
            result.NormalizedArguments = normalized.DeepClone();
            result.Hint ??= $"Arguments were normalized before executing `{toolName}`.";
        }

        if (await browserSessionManager.GetSnapshotAsync(context.TestRunId, cancellationToken) is { } snapshot)
        {
            await repository.SaveBrowserSnapshotAsync(context.TestRunId, snapshot, cancellationToken);
        }

        return result;
    } private async Task<ToolExecutionResult> CreateGoalAsync(ToolInvocationContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        var title = GetString(arguments, "title");
        var criteria = GetString(arguments, "success_criteria");
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(criteria))
        {
            return ToolExecutionResult.Failed("Goal title and success criteria are required.");
        }

        var existing = (await repository.ListGoalsAsync(context.TestRunId, cancellationToken)).FirstOrDefault(goal => IsDuplicateGoal(goal, title, criteria));
        if (existing is not null)
        {
            return ToolExecutionResult.Successful("Goal already exists.", GoalNode(existing), "Use the existing active-run goal ID. Do not create a duplicate goal.");
        }

        var now = DateTime.UtcNow;
        var goal = new GoalItem { Id = Guid.NewGuid(), TestRunId = context.TestRunId, Title = title.Trim(), SuccessCriteria = criteria.Trim(), Status = GoalStatus.Pending, CreatedAtUtc = now, UpdatedAtUtc = now };
        await repository.AddGoalAsync(goal, cancellationToken);
        await AddGoalTimelineEntryAsync(context.ChatSessionId, context.TestRunId, goal, "Goal created.", cancellationToken);
        return ToolExecutionResult.Successful("Goal created.", GoalNode(goal));
    } private async Task<ToolExecutionResult> UpdateGoalStatusAsync(ToolInvocationContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(GetString(arguments, "goal_id"), out var goalId))
        {
            return ToolExecutionResult.Failed("A valid goal_id is required.");
        }

        if (!Enum.TryParse<GoalStatus>(GetString(arguments, "status"), true, out var status))
        {
            return ToolExecutionResult.Failed("Status must be pending, running, passed, or failed.");
        }

        var goals = await repository.ListGoalsAsync(context.TestRunId, cancellationToken);
        var goal = goals.SingleOrDefault(candidate => candidate.Id == goalId);
        if (goal is null)
        {
            return ToolExecutionResult.Failed("Goal not found.");
        }

        if (goal.Status is GoalStatus.Passed or GoalStatus.Failed)
        {
            var unresolved = goals.Where(item => item.Status is GoalStatus.Pending or GoalStatus.Running).ToArray();
            return ToolExecutionResult.Failed($"Goal is already {goal.Status}; it was not changed.", data: new JsonObject
            {
                ["goal"] = GoalNode(goal),
                ["unresolved_goals"] = new JsonArray(unresolved.Select(GoalNode).ToArray()),
            }, hint: unresolved.Length == 0 ? "All goals are already terminal. Call end_task instead of marking the same goal again." : "Use a Pending or Running goal_id from active_run.goals. Do not mark an already terminal goal again.");
        }

        goal.Status = status;
        goal.Note = string.IsNullOrWhiteSpace(GetString(arguments, "note")) ? goal.Note : GetString(arguments, "note")!.Trim();
        goal.Evidence = string.IsNullOrWhiteSpace(GetString(arguments, "evidence")) ? goal.Evidence : GetString(arguments, "evidence")!.Trim();
        goal.UpdatedAtUtc = DateTime.UtcNow;
        goal.CompletedAtUtc = status is GoalStatus.Passed or GoalStatus.Failed ? DateTime.UtcNow : null;
        await repository.UpdateGoalAsync(goal, cancellationToken);
        await AddGoalTimelineEntryAsync(context.ChatSessionId, context.TestRunId, goal, $"Goal marked as {status}.", cancellationToken);
        return ToolExecutionResult.Successful($"Goal marked {status}.", GoalNode(goal));
    }
    private async Task<ToolExecutionResult> ListGoalsAsync(ToolInvocationContext context, CancellationToken cancellationToken) =>
        ToolExecutionResult.Successful("Goals listed.", new JsonArray((await repository.ListGoalsAsync(context.TestRunId, cancellationToken)).Select(GoalNode).ToArray()));

    private async Task<ToolExecutionResult> EndTaskAsync(ToolInvocationContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        var outcome = GetString(arguments, "outcome");
        var summary = GetString(arguments, "summary")?.Trim();
        var results = GetString(arguments, "test_results")?.Trim();
        var evidence = GetString(arguments, "evidence")?.Trim();
        var remaining = GetString(arguments, "remaining_work")?.Trim();
        if (outcome is not ("completed" or "failed"))
        {
            return ToolExecutionResult.Failed("End task outcome must be completed or failed.");
        }

        if (new[] { summary, results, evidence, remaining }.Any(string.IsNullOrWhiteSpace))
        {
            return ToolExecutionResult.Failed("End task summary, test_results, evidence, and remaining_work are required.");
        }

        if (summary!.Length < MinimumEndTaskNarrativeLength || results!.Length < MinimumEndTaskNarrativeLength)
        {
            return ToolExecutionResult.Failed($"End task summary and test_results must each be paragraph-length text of at least {MinimumEndTaskNarrativeLength} characters.");
        }

        var goals = await repository.ListGoalsAsync(context.TestRunId, cancellationToken);
        var unresolved = goals.Where(goal => goal.Status is GoalStatus.Pending or GoalStatus.Running).ToArray();
        if (goals.Count == 0 || unresolved.Length > 0)
        {
            return ToolExecutionResult.Failed("Cannot end task until every active-run goal is passed or failed.", goals.Count == 0 ? "No goals exist for the active run." : "Some active-run goals are still pending or running.", data: new JsonObject { ["unresolved_goals"] = new JsonArray(unresolved.Select(GoalNode).ToArray()) }, hint: "Use list_goals, inspect current browser evidence, then mark each active-run goal passed or failed before calling end_task again.");
        }

        return ToolExecutionResult.Successful("Task ended.", new JsonObject
        {
            ["outcome"] = goals.Any(goal => goal.Status == GoalStatus.Failed) ? "failed" : outcome,
            ["summary"] = summary,
            ["test_results"] = results,
            ["evidence"] = evidence,
            ["remaining_work"] = remaining,
            ["goals"] = new JsonArray(goals.Select(GoalNode).ToArray()),
        });
    }

    private async Task AddGoalTimelineEntryAsync(Guid chatId, Guid runId, GoalItem goal, string summary, CancellationToken cancellationToken) =>
        await repository.AddTimelineEntryAsync(new TimelineEntry { Id = Guid.NewGuid(), ChatSessionId = chatId, TestRunId = runId, Kind = TimelineItemKind.GoalChanged, Role = "system", Content = $"{summary} {goal.Title}", MetadataJson = JsonSerializer.Serialize(goal), CreatedAtUtc = DateTime.UtcNow }, cancellationToken);

    private static LlmToolDefinition Define(string name, string description, JsonObject parameters) => new() { Name = name, Description = description, Parameters = parameters };
    private static JsonObject LocatorSchema(params Arg[] extra) => LocatorSchema(required: true, extra);
    private static JsonObject LocatorSchema(bool required, params Arg[] extra) => Obj([new Arg("locator", required, new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["strategy"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("css", "xpath", "id", "name", "class", "tag", "link_text", "partial_link_text") }, ["value"] = new JsonObject { ["type"] = "string" } }, ["required"] = new JsonArray("strategy", "value") }), .. extra]);
    private static string LocatorDescription(string action) => $"{action} using a required locator argument shaped like {{\"locator\":{{\"strategy\":\"css\",\"value\":\"selector\"}}}}.";

    private static JsonObject Obj(params Arg[] args)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var arg in args)
        {
            properties[arg.Name] = arg.Schema.DeepClone();
            if (arg.Required)
            {
                required.Add(arg.Name);
            }
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        return schema;
    }

    private static Arg P(string name, string type, bool required = false, string? description = null)
    {
        var schema = new JsonObject { ["type"] = type };
        if (!string.IsNullOrWhiteSpace(description))
        {
            schema["description"] = description;
        }

        return new(name, required, schema);
    }

    private static Arg E(string name, string[] choices, bool required = false) => new(name, required, new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(choices.Select(choice => JsonValue.Create(choice)).ToArray()) });
    private static Arg A(string name, JsonObject itemSchema, bool required = false) => new(name, required, new JsonObject { ["type"] = "array", ["items"] = itemSchema.DeepClone() });
    private static JsonObject PrimitiveValueSchema() => new() { ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "number" }, new JsonObject { ["type"] = "integer" }, new JsonObject { ["type"] = "boolean" }, new JsonObject { ["type"] = "null" }) };

    private static JsonObject NormalizeArguments(string toolName, JsonObject arguments)
    {
        var normalized = (JsonObject)arguments.DeepClone();
        if (LocatorToolNames.Contains(toolName) && normalized["locator"] is null && GetString(normalized, "strategy") is { } strategy && GetString(normalized, "value") is { } value)
        {
            normalized["locator"] = new JsonObject { ["strategy"] = strategy, ["value"] = value };
            normalized.Remove("strategy");
            normalized.Remove("value");
        }

        return normalized;
    }

    private static JsonObject CopyWith(JsonObject source, params (string Key, string? Value)[] updates)
    {
        var copy = (JsonObject)source.DeepClone();
        foreach (var (key, value) in updates)
        {
            copy[key] = value;
        }

        return copy;
    }

    private static JsonObject GoalNode(GoalItem goal) => new() { ["id"] = goal.Id.ToString(), ["title"] = goal.Title, ["success_criteria"] = goal.SuccessCriteria, ["status"] = goal.Status.ToString(), ["note"] = goal.Note, ["evidence"] = goal.Evidence };
    private static string? GetString(JsonObject arguments, string name) => arguments[name] is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;
    private static bool IsDuplicateGoal(GoalItem goal, string title, string successCriteria)
    {
        var existingTitle = NormalizeGoalText(goal.Title);
        var requestedTitle = NormalizeGoalText(title);
        var existingCriteria = NormalizeGoalText(goal.SuccessCriteria);
        var requestedCriteria = NormalizeGoalText(successCriteria);
        return existingTitle == requestedTitle || existingCriteria == requestedCriteria || TokenSimilarity(existingCriteria, requestedCriteria) >= 0.75d;
    }

    private static string NormalizeGoalText(string value) => string.Join(' ', new string(value.Where(character => !char.IsPunctuation(character)).Select(char.ToLowerInvariant).ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    private static double TokenSimilarity(string left, string right)
    {
        var leftTokens = left.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var rightTokens = right.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var union = leftTokens.Union(rightTokens, StringComparer.Ordinal).Count();
        return leftTokens.Count == 0 || rightTokens.Count == 0 || union == 0 ? 0d : (double)leftTokens.Intersect(rightTokens, StringComparer.Ordinal).Count() / union;
    }

    private sealed record Arg(string Name, bool Required, JsonObject Schema);
}
