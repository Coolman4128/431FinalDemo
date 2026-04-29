using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BrowserTesting.Desktop.Models;
using BrowserTesting.Desktop.Classes;

namespace BrowserTesting.Desktop.Services;

public sealed class ChatOrchestrator(
    SqliteChatRepository repository,
    LmStudioLlmClient llmClient,
    BrowserSessionManager browserSessionManager,
    AppSettings settings)
{
    private readonly ToolCatalog tools = new(repository, browserSessionManager);

    public Task InitializeAsync(CancellationToken cancellationToken) =>
        repository.InitializeAsync(cancellationToken);

    public Task<IReadOnlyList<ChatSessionSummary>> ListChatsAsync(CancellationToken cancellationToken) =>
        repository.ListChatsAsync(cancellationToken);

    public Task<ChatSession> CreateChatAsync(string? title, CancellationToken cancellationToken) =>
        repository.CreateChatAsync(title, cancellationToken);

    public async Task<ChatSession?> LoadChatAsync(
        Guid chatId,
        Action<OrchestratorUpdate>? onUpdate,
        CancellationToken cancellationToken)
    {
        var chat = await repository.GetChatAsync(chatId, cancellationToken);
        if (chat is null)
        {
            return null;
        }

        onUpdate?.Invoke(new ChatLoaded(chat));
        return chat;
    }

    public async Task<BrowserSessionSnapshot?> CloseBrowserAsync(
        Guid runId,
        Action<OrchestratorUpdate>? onUpdate,
        CancellationToken cancellationToken)
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

        var chat = await repository.GetChatAsync(chatId, cancellationToken)
            ?? throw new InvalidOperationException($"Chat {chatId} was not found.");

        await AddUserMessageAsync(chat, prompt, onUpdate, cancellationToken);
        var run = await repository.CreateRunAsync(chatId, prompt, cancellationToken);
        onUpdate(new RunUpdated(run));
        var connection = settings.CreateConnectionSettings();

        if (chat.Title is "New Chat" or "Untitled Chat")
        {
            chat.Title = BuildChatTitle(prompt);
            chat.UpdatedAtUtc = DateTime.UtcNow;
            await repository.UpdateChatAsync(chat, cancellationToken);
        }

        try
        {
            var repeatedFailures = new RepeatedFailureTracker();
            var passiveLoop = new PassiveToolLoopTracker();
            var stalledTurns = 0;
            var taskEnded = false;
            for (var iteration = 0; !taskEnded; iteration++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                chat = await repository.GetChatAsync(chatId, cancellationToken)
                    ?? throw new InvalidOperationException($"Chat {chatId} was not found.");
                run = chat.Runs.Single(candidate => candidate.Id == run.Id);

                run.Status = TestRunStatus.Running;
                run.UpdatedAtUtc = DateTime.UtcNow;
                await repository.UpdateRunAsync(run, cancellationToken);
                onUpdate(new RunUpdated(run));

                var assistantEntry = await repository.AddTimelineEntryAsync(
                    new TimelineEntry
                    {
                        Id = Guid.NewGuid(),
                        ChatSessionId = chatId,
                        TestRunId = run.Id,
                        Kind = TimelineItemKind.AssistantMessage,
                        Role = "assistant",
                        Content = string.Empty,
                        CreatedAtUtc = DateTime.UtcNow,
                    },
                    cancellationToken);

                onUpdate(new TimelineEntryUpserted(assistantEntry));

                var toolBuilders = new Dictionary<int, ToolCallAccumulator>();
                await foreach (var streamEvent in llmClient.StreamCompletionAsync(BuildRequest(chat, run, Math.Max(settings.MaxToolIterations - iteration, 0), connection), cancellationToken))
                {
                    switch (streamEvent)
                    {
                        case LlmTextDelta textDelta:
                            assistantEntry.Content += textDelta.Content;
                            await repository.UpdateTimelineEntryAsync(assistantEntry, cancellationToken);
                            onUpdate(new TimelineEntryUpserted(assistantEntry));
                            break;

                        case LlmToolCallDelta toolDelta:
                            if (!toolBuilders.TryGetValue(toolDelta.Index, out var builder))
                            {
                                builder = new ToolCallAccumulator(toolDelta.Index);
                                toolBuilders[toolDelta.Index] = builder;
                            }

                            builder.Append(toolDelta);
                            break;

                        case LlmStreamCompleted:
                            break;

                        case LlmStreamFaulted faulted:
                            throw new InvalidOperationException(faulted.Message);
                    }
                }

                if (toolBuilders.Count == 0)
                {
                    stalledTurns++;
                    if (string.IsNullOrWhiteSpace(assistantEntry.Content))
                    {
                        await AddSystemNoticeAsync(
                            chatId,
                            run.Id,
                            BuildEmptyTurnNotice(stalledTurns),
                            onUpdate,
                            cancellationToken);
                    }

                    run = await RefreshRunStateAsync(chatId, run.Id, onUpdate, cancellationToken);
                    await AddSystemNoticeAsync(
                        chatId,
                        run.Id,
                        BuildNoToolNotice(run, stalledTurns),
                        onUpdate,
                        cancellationToken);

                    run.Status = TestRunStatus.Running;
                    run.FailureReason = null;
                    run.CompletedAtUtc = null;
                    run.UpdatedAtUtc = DateTime.UtcNow;
                    await repository.UpdateRunAsync(run, cancellationToken);
                    onUpdate(new RunUpdated(run));
                    continue;
                }

                stalledTurns = 0;

                run.Status = TestRunStatus.WaitingForTool;
                run.UpdatedAtUtc = DateTime.UtcNow;
                await repository.UpdateRunAsync(run, cancellationToken);
                onUpdate(new RunUpdated(run));

                var restartAfterToolFailure = false;
                foreach (var toolCall in toolBuilders.Values.OrderBy(candidate => candidate.Index).Select(candidate => candidate.Build()))
                {
                    var arguments = ParseArguments(toolCall);
                    var startedEntry = await repository.AddTimelineEntryAsync(
                        new TimelineEntry
                        {
                            Id = Guid.NewGuid(),
                            ChatSessionId = chatId,
                            TestRunId = run.Id,
                            Kind = TimelineItemKind.ToolCallStarted,
                            Role = "assistant",
                            Content = $"Calling `{toolCall.Name}`...",
                            ToolCallId = toolCall.Id,
                            ToolName = toolCall.Name,
                            MetadataJson = arguments.ToJsonString(),
                            CreatedAtUtc = DateTime.UtcNow,
                        },
                        cancellationToken);

                    onUpdate(new TimelineEntryUpserted(startedEntry));

                    ToolExecutionResult result;
                    try
                    {
                        result = await tools.ExecuteAsync(
                            new ToolInvocationContext
                            {
                                ChatSessionId = chatId,
                                TestRunId = run.Id,
                                LaunchHeadless = settings.LaunchHeadless,
                                BrowserSnapshot = run.BrowserSnapshot,
                            },
                            toolCall.Name,
                            arguments,
                            cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        result = ToolExecutionResult.Failed($"Tool `{toolCall.Name}` failed.", ex.Message);
                    }

                    run = await RefreshRunStateAsync(chatId, run.Id, onUpdate, cancellationToken);
                    var repeatedAttemptCount = 0;
                    var passiveAttemptCount = 0;
                    if (result.Success)
                    {
                        repeatedFailures.Reset();
                        passiveAttemptCount = passiveLoop.Register(toolCall.Name);
                    }
                    else
                    {
                        passiveLoop.Reset();
                        repeatedAttemptCount = repeatedFailures.RegisterFailure(
                            BuildFailureSignature(
                                toolCall.Name,
                                result.NormalizedArguments ?? arguments,
                                result.Error ?? result.Summary));
                    }

                    var finishedMetadata = BuildToolResultMetadata(result, repeatedAttemptCount);
                    var finishedEntry = await repository.AddTimelineEntryAsync(
                        new TimelineEntry
                        {
                            Id = Guid.NewGuid(),
                            ChatSessionId = chatId,
                            TestRunId = run.Id,
                            Kind = TimelineItemKind.ToolCallFinished,
                            Role = "tool",
                            Content = result.Summary,
                            ToolCallId = toolCall.Id,
                            ToolName = toolCall.Name,
                            MetadataJson = finishedMetadata.ToJsonString(),
                            CreatedAtUtc = DateTime.UtcNow,
                        },
                        cancellationToken);

                    onUpdate(new TimelineEntryUpserted(finishedEntry));
                    run = await RefreshRunStateAsync(chatId, run.Id, onUpdate, cancellationToken);

                    if (passiveAttemptCount >= 3)
                    {
                        await AddSystemNoticeAsync(
                            chatId,
                            run.Id,
                            BuildPassiveToolLoopNotice(toolCall.Name, passiveAttemptCount),
                            onUpdate,
                            cancellationToken);
                    }

                    if (result.Success && string.Equals(toolCall.Name, "end_task", StringComparison.Ordinal))
                    {
                        run.Status = run.Goals.Any(goal => goal.Status == GoalStatus.Failed)
                            ? TestRunStatus.Failed
                            : TestRunStatus.Completed;
                        run.FailureReason = run.Status == TestRunStatus.Failed
                            ? ExtractEndTaskSummary(result) ?? "One or more goals failed."
                            : null;
                        run.CompletedAtUtc = DateTime.UtcNow;
                        run.UpdatedAtUtc = DateTime.UtcNow;
                        await repository.UpdateRunAsync(run, cancellationToken);
                        onUpdate(new RunUpdated(run));
                        taskEnded = true;
                        break;
                    }

                    if (!result.Success)
                    {
                        await AddSystemNoticeAsync(
                            chatId,
                            run.Id,
                            BuildToolFailureNotice(toolCall.Name, result, repeatedAttemptCount),
                            onUpdate,
                            cancellationToken);

                        run.Status = TestRunStatus.Running;
                        run.FailureReason = null;
                        run.CompletedAtUtc = null;
                        run.UpdatedAtUtc = DateTime.UtcNow;
                        await repository.UpdateRunAsync(run, cancellationToken);
                        onUpdate(new RunUpdated(run));
                        restartAfterToolFailure = true;
                        break;
                    }

                }

                if (restartAfterToolFailure)
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
        }

        return run;
    }

    private async Task AddUserMessageAsync(ChatSession chat, string prompt, Action<OrchestratorUpdate> onUpdate, CancellationToken cancellationToken)
    {
        chat.UpdatedAtUtc = DateTime.UtcNow;
        await repository.UpdateChatAsync(chat, cancellationToken);

        var entry = await repository.AddTimelineEntryAsync(
            new TimelineEntry
            {
                Id = Guid.NewGuid(),
                ChatSessionId = chat.Id,
                Kind = TimelineItemKind.UserMessage,
                Role = "user",
                Content = prompt.Trim(),
                CreatedAtUtc = DateTime.UtcNow,
            },
            cancellationToken);

        onUpdate(new TimelineEntryUpserted(entry));
    }

    private LlmRequest BuildRequest(ChatSession chat, TestRun run, int turnsRemaining, LlmConnectionSettings connection)
    {
        var forceEndTask = CanCallEndTask(run);
        return new LlmRequest
        {
            Connection = connection,
            Tools = tools.Definitions,
            Messages = BuildConversation(chat, run, turnsRemaining, connection),
            ToolChoiceMode = forceEndTask ? LlmToolChoiceMode.ForceFunction : LlmToolChoiceMode.Required,
            ForcedToolName = forceEndTask ? "end_task" : null,
            ParallelToolCalls = false,
        };
    }

    private IReadOnlyList<LlmConversationMessage> BuildConversation(
        ChatSession chat,
        TestRun activeRun,
        int turnsRemaining,
        LlmConnectionSettings connection)
    {
        var messages = new List<LlmConversationMessage>
        {
            new()
            {
                Role = connection.Provider == LlmProvider.OpenAi ? "developer" : "system",
                Content = BuildSystemPrompt(chat, activeRun, turnsRemaining),
            },
            new()
            {
                Role = "user",
                Content = activeRun.UserPrompt,
            },
        };

        var recentNotices = BuildRecentActiveRunNotices(chat, activeRun.Id);
        if (!string.IsNullOrWhiteSpace(recentNotices))
        {
            messages.Add(new LlmConversationMessage
            {
                Role = "system",
                Content = recentNotices,
            });
        }

        return messages;
    }

    private static string? BuildRecentActiveRunNotices(ChatSession chat, Guid activeRunId)
    {
        var notices = chat.Timeline
            .Where(entry => entry.TestRunId == activeRunId && entry.Kind == TimelineItemKind.SystemNotice)
            .OrderByDescending(entry => entry.Sequence)
            .Take(3)
            .Reverse()
            .Select(entry => Truncate(entry.Content, 700))
            .Where(content => !string.IsNullOrWhiteSpace(content))
            .ToArray();

        return notices.Length == 0
            ? null
            : $"recent_active_run_notices: {string.Join(" | ", notices)}";
    }

    private string BuildSystemPrompt(ChatSession chat, TestRun activeRun, int turnsRemaining)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are a browser-testing agent. Drive the browser with tools and maintain the active-run goal ledger.");
        builder.AppendLine("Rules:");
        builder.AppendLine("- Use tools every turn. Do not narrate progress instead of calling a tool.");
        builder.AppendLine("- If the user listed goals, create each distinct requested goal once. Do not recreate semantically equivalent goals.");
        builder.AppendLine("- active_run.goals is already current. Do not call list_goals just to rediscover the same IDs.");
        builder.AppendLine("- Only use goal IDs from active_run.goals. Previous-run summaries are not actionable.");
        builder.AppendLine("- Do not call open_browser when active_run.browser.state is Active; continue from the current page.");
        builder.AppendLine("- After inspect_page returns usable refs, act on those refs. Do not inspect the same unchanged page repeatedly.");
        builder.AppendLine("- Refs are page-local. If the current URL differs from the inspection URL, inspect the current page before click_ref/type_ref.");
        builder.AppendLine("- When observed evidence satisfies a pending goal, mark that goal passed immediately before moving to later dependent work.");
        builder.AppendLine("- Use active_run.last_page_inspection and active_run.recent_page_inspections as valid evidence from this run. Do not fail a goal only because the browser later advanced to another page.");
        builder.AppendLine("- Do not mark an already Passed or Failed goal again. Use a Pending or Running goal ID, or call end_task when all goals are terminal.");
        builder.AppendLine("- Resolve every active-run goal as passed or failed with observed evidence.");
        builder.AppendLine("- When every active-run goal is passed or failed, call end_task next.");
        builder.AppendLine("- If end_task fails, resolve the returned unresolved goals, then call end_task again.");
        builder.AppendLine("- Prefer inspect_page, then click_ref/type_ref. Use selector tools only when refs are insufficient.");
        builder.AppendLine("- inspect_page lists actionable elements and visible_text. For cost, totals, confirmation text, and other non-control content, inspect_page visible_text or get_text/get_html must show the expected text before passing the goal.");
        builder.AppendLine("- On tool failure, change strategy. Do not repeat identical failing calls.");
        builder.AppendLine();
        builder.AppendLine("Critical shapes:");
        builder.AppendLine("- inspect_page: {\"max_elements\":40,\"include_hidden\":false}");
        builder.AppendLine("- click_ref/type_ref use refs returned by the latest inspect_page call.");
        builder.AppendLine("- Locator tools: {\"locator\":{\"strategy\":\"css\",\"value\":\"input[name='q']\"}}");
        builder.AppendLine("- mark_goal_pass: {\"goal_id\":\"<goal-id>\",\"evidence\":\"Observed expected result on the page.\"}");
        builder.AppendLine("- mark_goal_fail: {\"goal_id\":\"<goal-id>\",\"reason\":\"Why the goal failed.\",\"evidence\":\"Observed blocking evidence.\"}");
        builder.AppendLine("- end_task: {\"outcome\":\"completed|failed\",\"summary\":\"One or two paragraphs, at least 120 characters, summarizing what you did.\",\"test_results\":\"One or two paragraphs, at least 120 characters, summarizing pass/fail results.\",\"evidence\":\"...\",\"remaining_work\":\"none|...\"}");
        builder.AppendLine();
        builder.AppendLine("active_run:");
        builder.AppendLine(BuildActiveRunContext(chat, activeRun, turnsRemaining).ToJsonString());
        return builder.ToString();
    }

    private JsonObject BuildActiveRunContext(ChatSession chat, TestRun activeRun, int turnsRemaining)
    {
        var secrets = new JsonArray();
        foreach (var name in repository.ListSecretNamesAsync(chat.Id, CancellationToken.None).GetAwaiter().GetResult())
        {
            secrets.Add(name);
        }

        return new JsonObject
        {
            ["run_id"] = activeRun.Id.ToString(),
            ["status"] = activeRun.Status.ToString(),
            ["user_prompt"] = Truncate(activeRun.UserPrompt, 2000),
            ["expected_goal_count"] = GetExpectedGoalCount(activeRun.UserPrompt),
            ["active_goal_count"] = activeRun.Goals.Count,
            ["completion_gate"] = CanCallEndTask(activeRun)
                ? "All active-run goals are terminal. The next tool call must be end_task."
                : BuildCompletionGateMessage(activeRun),
            ["soft_turn_budget_remaining"] = turnsRemaining,
            ["goals"] = BuildGoalLedger(activeRun.Goals, includeIds: true),
            ["browser"] = BuildBrowserSnapshotNode(activeRun.BrowserSnapshot),
            ["saved_secret_names"] = secrets,
            ["recent_tool_outcomes"] = GetRecentToolOutcomes(chat, activeRun.Id),
            ["last_page_inspection"] = GetLastPageInspection(chat, activeRun.Id),
            ["recent_page_inspections"] = GetRecentPageInspections(chat, activeRun.Id),
        };
    }

    private static bool CanCallEndTask(TestRun run)
    {
        var expectedGoalCount = GetExpectedGoalCount(run.UserPrompt);
        return run.Goals.Count > 0 &&
               (expectedGoalCount is null || run.Goals.Count >= expectedGoalCount.Value) &&
               run.Goals.All(goal => goal.Status is GoalStatus.Passed or GoalStatus.Failed);
    }

    private static string BuildCompletionGateMessage(TestRun run)
    {
        var expectedGoalCount = GetExpectedGoalCount(run.UserPrompt);
        if (expectedGoalCount is { } expected && run.Goals.Count < expected)
        {
            return $"The user requested {expected} goals, but only {run.Goals.Count} active-run goals exist. Create the missing distinct goals before end_task.";
        }

        if (run.Goals.Count == 0)
        {
            return "No active-run goals exist. Create one or more goals before browser work.";
        }

        var unresolved = run.Goals
            .Where(goal => goal.Status is GoalStatus.Pending or GoalStatus.Running)
            .Select(goal => goal.Id.ToString())
            .ToArray();

        return unresolved.Length == 0
            ? "Every active-run goal must be terminal before end_task."
            : $"Resolve these active-run goal IDs before end_task: {string.Join(", ", unresolved)}";
    }

    private static int? GetExpectedGoalCount(string prompt)
    {
        var match = Regex.Match(
            prompt,
            @"\b(?:make|create|have|want)\s+(?<count>\d+|one|two|three|four|five|six|seven|eight|nine|ten)\s+goals?\b",
            RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            match = Regex.Match(
                prompt,
                @"\b(?<count>\d+|one|two|three|four|five|six|seven|eight|nine|ten)\s+goals?\b",
                RegexOptions.IgnoreCase);
        }

        if (!match.Success)
        {
            return null;
        }

        var rawCount = match.Groups["count"].Value;
        if (int.TryParse(rawCount, out var numeric))
        {
            return Math.Clamp(numeric, 1, 25);
        }

        return rawCount.ToLowerInvariant() switch
        {
            "one" => 1,
            "two" => 2,
            "three" => 3,
            "four" => 4,
            "five" => 5,
            "six" => 6,
            "seven" => 7,
            "eight" => 8,
            "nine" => 9,
            "ten" => 10,
            _ => null,
        };
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

    private static JsonObject BuildBrowserSnapshotNode(BrowserSessionSnapshot snapshot)
    {
        var tabs = new JsonArray();
        foreach (var tab in snapshot.Tabs.Take(5))
        {
            tabs.Add(new JsonObject
            {
                ["title"] = Truncate(tab.Title, 200),
                ["url"] = Truncate(tab.Url, 500),
                ["is_selected"] = tab.IsSelected,
            });
        }

        return new JsonObject
        {
            ["current_url"] = Truncate(snapshot.CurrentUrl, 500),
            ["page_title"] = Truncate(snapshot.PageTitle, 300),
            ["state"] = snapshot.State.ToString(),
            ["tab_count"] = snapshot.Tabs.Count,
            ["tabs"] = tabs,
        };
    }

    private static string? ExtractEndTaskSummary(ToolExecutionResult result)
    {
        if (result.Data is JsonObject data &&
            data["summary"] is JsonValue summaryValue &&
            summaryValue.TryGetValue<string>(out var summary) &&
            !string.IsNullOrWhiteSpace(summary))
        {
            return summary.Trim();
        }

        return null;
    }

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
            return new JsonObject
            {
                ["raw"] = toolCall.ArgumentsJson,
            };
        }
    }

    private static string BuildEmptyTurnNotice(int stalledTurns) =>
        stalledTurns switch
        {
            1 => "Your last reply was empty while goals are still unresolved. Call exactly one tool on the next turn. Do not return an empty response.",
            2 => "You have produced multiple empty or no-tool turns. Stop narrating and emit exactly one structured tool call now.",
            _ => "Repeated empty or no-tool turns detected. If you cannot proceed, use goal evidence to fail the blocked goal instead of returning another empty reply.",
        };

    private static string BuildNoToolNotice(TestRun run, int stalledTurns)
    {
        var prefix = stalledTurns > 1
            ? $"No structured tool call was emitted for {stalledTurns} consecutive turns. "
            : "No structured tool call was emitted. ";

        if (CanCallEndTask(run))
        {
            return prefix + "All active-run goals are passed or failed. Call end_task now with paragraph-length summary, paragraph-length test_results, evidence, and remaining_work.";
        }

        if (run.Goals.Count == 0)
        {
            return prefix + "No active-run goals exist. Call create_goal before browser work.";
        }

        var unresolved = run.Goals
            .Where(goal => goal.Status is GoalStatus.Pending or GoalStatus.Running)
            .Select(goal => goal.Id.ToString())
            .ToArray();
        return prefix + $"Goals are unresolved ({string.Join(", ", unresolved)}). Use tools to inspect evidence, then mark each goal passed or failed.";
    }

    private static string BuildPassiveToolLoopNotice(string toolName, int attemptCount) =>
        $"Passive tool loop detected after {attemptCount} consecutive passive calls ending with `{toolName}`. Use active_run.goals, active_run.browser, active_run.last_page_inspection, and active_run.recent_page_inspections from context instead of calling list_goals/open_browser/inspect_page again. The next tool should change page or goal state: create a missing distinct goal, type_ref, click_ref, mark_goal_pass, mark_goal_fail, or end_task when all goals are terminal.";

    private static JsonObject BuildToolResultMetadata(ToolExecutionResult result, int repeatedAttemptCount)
    {
        var metadata = new JsonObject
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

        return metadata;
    }

    private static string BuildToolFailureNotice(string toolName, ToolExecutionResult result, int repeatedAttemptCount)
    {
        var builder = new StringBuilder();
        if (repeatedAttemptCount >= 2)
        {
            builder.Append($"Repeated identical tool failure detected for `{toolName}` (attempt {repeatedAttemptCount}). ");
            builder.Append("Do not repeat the same call with the same arguments. ");
        }
        else
        {
            builder.Append($"The tool `{toolName}` failed. ");
        }

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

        if (toolName is "mark_goal_pass" or "mark_goal_fail" or "update_goal_status")
        {
            builder.Append("Next-step options: use a Pending or Running goal ID from active_run.goals, inspect prior active-run evidence if needed, or call end_task if every goal is already terminal.");
        }
        else
        {
            builder.Append("Next-step options: inspect page state, change the argument shape, try a different locator strategy, use a less brittle inspection tool, or fail the goal with evidence if the page blocks further progress.");
        }

        return builder.ToString();
    }

    private static string BuildFailureSignature(string toolName, JsonNode? arguments, string? error) =>
        $"{toolName}|{CanonicalizeJson(arguments)}|{error ?? string.Empty}";

    private static string CanonicalizeJson(JsonNode? node) =>
        node switch
        {
            null => "null",
            JsonObject obj => $"{{{string.Join(",", obj.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"\"{pair.Key}\":{CanonicalizeJson(pair.Value)}"))}}}",
            JsonArray array => $"[{string.Join(",", array.Select(CanonicalizeJson))}]",
            _ => node.ToJsonString(),
        };

    private static string? Truncate(string? value, int maxLength)
    {
        if (value is null || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength];
    }

    private static JsonNode? CompactJsonNode(
        JsonNode? node,
        int maxStringLength = 1000,
        int maxArrayItems = 40,
        int maxObjectProperties = 80)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonValue value when value.TryGetValue<string>(out var text):
                return JsonValue.Create(Truncate(text, maxStringLength));
            case JsonValue:
                return node.DeepClone();
            case JsonArray array:
            {
                var clone = new JsonArray();
                foreach (var item in array.Take(maxArrayItems))
                {
                    clone.Add(CompactJsonNode(item, maxStringLength, maxArrayItems, maxObjectProperties));
                }

                if (array.Count > maxArrayItems)
                {
                    clone.Add(new JsonObject
                    {
                        ["_truncated"] = true,
                        ["omitted_count"] = array.Count - maxArrayItems,
                    });
                }

                return clone;
            }
            case JsonObject obj:
            {
                var clone = new JsonObject();
                var copied = 0;
                foreach (var property in obj)
                {
                    if (copied >= maxObjectProperties)
                    {
                        clone["_truncated"] = true;
                        clone["omitted_property_count"] = obj.Count - copied;
                        break;
                    }

                    clone[property.Key] = CompactJsonNode(property.Value, maxStringLength, maxArrayItems, maxObjectProperties);
                    copied++;
                }

                return clone;
            }
            default:
                return node.DeepClone();
        }
    }

    private static JsonArray GetRecentToolOutcomes(ChatSession chat, Guid runId)
    {
        var outcomes = new JsonArray();
        foreach (var entry in chat.Timeline
                     .Where(candidate => candidate.TestRunId == runId && candidate.Kind == TimelineItemKind.ToolCallFinished)
                     .OrderByDescending(candidate => candidate.Sequence)
                     .Take(6)
                     .Reverse())
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
                ["data"] = ShouldIncludeRecentToolData(entry.ToolName)
                    ? CompactJsonNode(metadata?["data"], 700, 20, 60)
                    : null,
            });
        }

        return outcomes;
    }

    private static JsonNode? GetLastPageInspection(ChatSession chat, Guid runId)
    {
        var entry = chat.Timeline
            .Where(candidate =>
                candidate.TestRunId == runId &&
                candidate.Kind == TimelineItemKind.ToolCallFinished &&
                string.Equals(candidate.ToolName, "inspect_page", StringComparison.Ordinal))
            .OrderByDescending(candidate => candidate.Sequence)
            .FirstOrDefault(candidate => ParseMetadata(candidate.MetadataJson)?["success"]?.GetValue<bool>() == true);

        if (entry is null)
        {
            return null;
        }

        var metadata = ParseMetadata(entry.MetadataJson);
        return CompactJsonNode(metadata?["data"], 700, 30, 80);
    }

    private static JsonArray GetRecentPageInspections(ChatSession chat, Guid runId)
    {
        const int maxInspections = 5;
        var inspections = new List<JsonNode>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in chat.Timeline
                     .Where(candidate =>
                         candidate.TestRunId == runId &&
                         candidate.Kind == TimelineItemKind.ToolCallFinished &&
                         string.Equals(candidate.ToolName, "inspect_page", StringComparison.Ordinal))
                     .OrderByDescending(candidate => candidate.Sequence))
        {
            var metadata = ParseMetadata(entry.MetadataJson);
            if (metadata?["success"]?.GetValue<bool>() != true ||
                metadata["data"] is not JsonObject data)
            {
                continue;
            }

            var url = data["url"]?.GetValue<string>();
            var urlKey = NormalizePageEvidenceUrl(url) ?? entry.Sequence.ToString();
            if (!seenUrls.Add(urlKey))
            {
                continue;
            }

            if (CompactJsonNode(data, 900, 30, 80) is { } compact)
            {
                inspections.Add(compact);
            }

            if (inspections.Count >= maxInspections)
            {
                break;
            }
        }

        inspections.Reverse();
        return new JsonArray(inspections.ToArray());
    }

    private static string? NormalizePageEvidenceUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var hashIndex = url.IndexOf('#', StringComparison.Ordinal);
            return (hashIndex >= 0 ? url[..hashIndex] : url).TrimEnd('/');
        }

        var builder = new UriBuilder(uri)
        {
            Fragment = string.Empty,
        };
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }

    private static bool ShouldIncludeRecentToolData(string? toolName) =>
        toolName is "inspect_page"
            or "find_element"
            or "find_elements"
            or "get_page_state"
            or "get_text"
            or "get_attribute"
            or "execute_javascript"
            or "wait_for_text"
            or "wait_for_navigation"
            or "click"
            or "click_ref"
            or "type_ref"
            or "open_browser"
            or "create_goal"
            or "update_goal_status"
            or "mark_goal_pass"
            or "mark_goal_fail";

    private static JsonObject? ParseMetadata(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(metadataJson)?.AsObject();
        }
        catch
        {
            return null;
        }
    }

    private async Task<TestRun> RefreshRunStateAsync(Guid chatId, Guid runId, Action<OrchestratorUpdate> onUpdate, CancellationToken cancellationToken)
    {
        var chat = await repository.GetChatAsync(chatId, cancellationToken)
            ?? throw new InvalidOperationException($"Chat {chatId} was not found.");
        var run = chat.Runs.Single(candidate => candidate.Id == runId);
        onUpdate(new GoalsUpdated(run.Id, run.Goals));
        onUpdate(new BrowserSnapshotUpdated(run.Id, run.BrowserSnapshot));
        onUpdate(new RunUpdated(run));
        return run;
    }

    private async Task AddSystemNoticeAsync(
        Guid chatId,
        Guid runId,
        string content,
        Action<OrchestratorUpdate> onUpdate,
        CancellationToken cancellationToken)
    {
        var entry = await repository.AddTimelineEntryAsync(
            new TimelineEntry
            {
                Id = Guid.NewGuid(),
                ChatSessionId = chatId,
                TestRunId = runId,
                Kind = TimelineItemKind.SystemNotice,
                Role = "system",
                Content = content,
                CreatedAtUtc = DateTime.UtcNow,
            },
            cancellationToken);

        onUpdate(new TimelineEntryUpserted(entry));
    }

    private static string BuildChatTitle(string prompt)
    {
        var words = prompt.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.Length <= 6
            ? string.Join(' ', words)
            : $"{string.Join(' ', words.Take(6))}...";
    }

    private sealed class ToolCallAccumulator(int index)
    {
        private readonly StringBuilder arguments = new();
        private readonly StringBuilder identifier = new();
        private readonly StringBuilder name = new();

        public int Index => index;

        public void Append(LlmToolCallDelta delta)
        {
            if (!string.IsNullOrEmpty(delta.IdPart))
            {
                identifier.Append(delta.IdPart);
            }

            if (!string.IsNullOrEmpty(delta.NamePart))
            {
                name.Append(delta.NamePart);
            }

            if (!string.IsNullOrEmpty(delta.ArgumentsPart))
            {
                arguments.Append(delta.ArgumentsPart);
            }
        }

        public LlmToolCall Build() =>
            new()
            {
                Index = index,
                Id = identifier.Length == 0 ? Guid.NewGuid().ToString("N") : identifier.ToString(),
                Name = name.Length == 0 ? "unknown_tool" : name.ToString(),
                ArgumentsJson = arguments.Length == 0 ? "{}" : arguments.ToString(),
            };

    }

    private sealed class RepeatedFailureTracker
    {
        private string? lastSignature;
        private int streak;

        public int RegisterFailure(string signature)
        {
            if (string.Equals(lastSignature, signature, StringComparison.Ordinal))
            {
                streak++;
            }
            else
            {
                lastSignature = signature;
                streak = 1;
            }

            return streak;
        }

        public void Reset()
        {
            lastSignature = null;
            streak = 0;
        }
    }

    private sealed class PassiveToolLoopTracker
    {
        private static readonly HashSet<string> PassiveToolNames =
        [
            "list_goals",
            "inspect_page",
            "get_page_state",
            "list_tabs",
            "open_browser",
        ];

        private int streak;

        public int Register(string toolName)
        {
            if (PassiveToolNames.Contains(toolName))
            {
                streak++;
            }
            else
            {
                streak = 0;
            }

            return streak;
        }

        public void Reset() => streak = 0;
    }
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

    private static readonly HashSet<string> LocatorToolNames =
    [
        "find_element",
        "find_elements",
        "click",
        "double_click",
        "type_text",
        "clear",
        "send_keys",
        "submit",
        "select_option",
        "hover",
        "scroll_into_view",
        "get_text",
        "get_attribute",
        "get_html",
        "wait_for_element",
    ];

    private readonly BrowserSessionManager browserSessionManager;
    private readonly SqliteChatRepository repository;
    private readonly IReadOnlyList<LlmToolDefinition> definitions =
    [
        Define("open_browser", "Open a Chrome browser for the active run.", Object(Property("url", "string"), Property("profile_name", "string"))),
        Define("close_browser", "Close the browser for the active run.", Object()),
        Define("list_tabs", "List open tabs for the active browser.", Object()),
        Define("switch_tab", "Switch to a tab by index or handle.", Object(Property("index", "integer"), Property("handle", "string"))),
        Define("goto_url", "Navigate the browser to a URL.", Object(Property("url", "string", true))),
        Define("back", "Navigate backward in browser history.", Object()),
        Define("forward", "Navigate forward in browser history.", Object()),
        Define("refresh", "Refresh the current page.", Object()),
        Define("get_page_state", "Return the current URL, title, and tab summary.", Object()),
        Define("find_element", LocatorDescription("Find the first matching element"), LocatorSchema("locator")),
        Define("find_elements", LocatorDescription("Find all matching elements"), LocatorSchema("locator")),
        Define("inspect_page", "Return visible page text plus a compact list of visible actionable elements with page-local refs for click_ref/type_ref. Use before guessing selectors.", Object(Property("max_elements", "integer"), Property("include_hidden", "boolean"))),
        Define("click_ref", "Click an element ref returned by the latest inspect_page for the current page URL.", Object(Property("ref", "string", true))),
        Define("type_ref", "Type text into an element ref returned by the latest inspect_page for the current page URL.", Object(Property("ref", "string", true), Property("text", "string", true), Property("clear_first", "boolean"))),
        Define("click", LocatorDescription("Click an element"), LocatorSchema("locator")),
        Define("double_click", LocatorDescription("Double click an element"), LocatorSchema("locator")),
        Define("type_text", "Type text into an element. Use {\"locator\":{\"strategy\":\"...\",\"value\":\"...\"},\"text\":\"...\"}.", LocatorSchema("locator", Property("text", "string", true), Property("clear_first", "boolean"))),
        Define("clear", LocatorDescription("Clear the value of an input element"), LocatorSchema("locator")),
        Define("send_keys", "Send raw keys to an element. Use {\"locator\":{\"strategy\":\"...\",\"value\":\"...\"},\"keys\":\"...\"}.", LocatorSchema("locator", Property("keys", "string", true))),
        Define("submit", LocatorDescription("Submit a form element"), LocatorSchema("locator")),
        Define("select_option", "Select an option in a select element by text or value. Use {\"locator\":{\"strategy\":\"...\",\"value\":\"...\"},\"text\":\"...\"} or \"value\".", LocatorSchema("locator", Property("text", "string"), Property("value", "string"))),
        Define("hover", LocatorDescription("Move the mouse over an element"), LocatorSchema("locator")),
        Define("scroll_into_view", LocatorDescription("Scroll until the element is visible"), LocatorSchema("locator")),
        Define("get_text", LocatorDescription("Read text from an element"), LocatorSchema("locator")),
        Define("get_attribute", "Read an attribute from an element. Use {\"locator\":{\"strategy\":\"...\",\"value\":\"...\"},\"attribute\":\"...\"}.", LocatorSchema("locator", Property("attribute", "string", true))),
        Define("get_html", "Return bounded outer HTML from an optional element locator, or bounded page HTML when no locator is supplied. Prefer inspect_page for compact actionable refs.", OptionalLocatorSchema("locator")),
        Define("take_screenshot", "Capture a screenshot to disk.", Object(Property("name", "string"))),
        Define("execute_javascript", "Run JavaScript in the current page. Optional arguments must be JSON primitive values passed positionally.", Object(
            Property("script", "string", true),
            ArrayProperty("arguments", PrimitiveValueSchema()))),
        Define("wait_for_element", "Wait until an element exists. Use {\"locator\":{\"strategy\":\"...\",\"value\":\"...\"},\"timeout_ms\":5000}.", LocatorSchema("locator", Property("timeout_ms", "integer"))),
        Define("wait_for_text", "Wait until page source contains text.", Object(Property("text", "string", true), Property("timeout_ms", "integer"))),
        Define("wait_for_navigation", "Wait until the URL contains expected text.", Object(Property("url_contains", "string", true), Property("timeout_ms", "integer"))),
        Define("sleep", "Pause execution briefly.", Object(Property("milliseconds", "integer", true))),
        Define("get_cookies", "Return all cookies from the current page.", Object()),
        Define("set_cookie", "Set a cookie in the current browser.", Object(Property("name", "string", true), Property("value", "string", true), Property("domain", "string"), Property("path", "string"))),
        Define("read_local_storage", "Read local storage value by key.", Object(Property("key", "string", true))),
        Define("write_local_storage", "Write local storage value by key.", Object(Property("key", "string", true), Property("value", "string", true))),
        Define("create_goal", "Create a new test goal for the active run.", Object(Property("title", "string", true), Property("success_criteria", "string", true))),
        Define("update_goal_status", "Update a goal status to pending, running, passed, or failed.", Object(Property("goal_id", "string", true), Property("status", "string", true), Property("note", "string"), Property("evidence", "string"))),
        Define("mark_goal_pass", "Mark a goal as passed with evidence.", Object(Property("goal_id", "string", true), Property("evidence", "string", true))),
        Define("mark_goal_fail", "Mark a goal as failed with reason and evidence.", Object(Property("goal_id", "string", true), Property("reason", "string", true), Property("evidence", "string"))),
        Define("list_goals", "List all goals for the active run.", Object()),
        Define("end_task", "Finish the active run after every goal is passed or failed. Include final text summarizing what was done and the test results. Use only when all active-run goals are terminal.", Object(
            EnumProperty("outcome", ["completed", "failed"], true),
            Property("summary", "string", true, "One or two paragraphs, at least 120 characters, summarizing what you did during the run. Do not use a terse phrase."),
            Property("test_results", "string", true, "One or two paragraphs, at least 120 characters, summarizing which tests or goals passed or failed and why. Do not use a terse phrase."),
            Property("evidence", "string", true),
            Property("remaining_work", "string", true))),
        Define("save_secret", "Save a named secret for this chat.", Object(Property("name", "string", true), Property("value", "string", true))),
        Define("get_secret", "Retrieve a named secret for this chat.", Object(Property("name", "string", true))),
        Define("list_secrets", "List saved secret names for this chat.", Object()),
    ];
    private readonly IReadOnlyDictionary<string, LlmToolDefinition> definitionsByName;

    public ToolCatalog(SqliteChatRepository repository, BrowserSessionManager browserSessionManager)
    {
        this.repository = repository;
        this.browserSessionManager = browserSessionManager;
        definitionsByName = definitions.ToDictionary(definition => definition.Name, StringComparer.Ordinal);
    }

    public IReadOnlyList<LlmToolDefinition> Definitions => definitions;

    public async Task<ToolExecutionResult> ExecuteAsync(ToolInvocationContext context, string toolName, JsonObject arguments, CancellationToken cancellationToken)
    {
        definitionsByName.TryGetValue(toolName, out var definition);
        var normalizedArguments = NormalizeArguments(toolName, definition, arguments);
        if (definition is not null && Validate(definition, normalizedArguments) is { } validation)
        {
            return ToolExecutionResult.Failed(
                validation.Summary,
                validation.Error,
                hint: validation.Hint?.Message,
                normalizedArguments: validation.Hint?.NormalizedArguments,
                expectedArguments: validation.ExpectedArguments,
                exampleArguments: validation.ExampleArguments);
        }

        var result = toolName switch
        {
            "create_goal" => await CreateGoalAsync(context, normalizedArguments, cancellationToken),
            "update_goal_status" => await UpdateGoalStatusAsync(context, normalizedArguments, cancellationToken),
            "mark_goal_pass" => await UpdateGoalStatusAsync(context, new JsonObject
            {
                ["goal_id"] = normalizedArguments["goal_id"]?.DeepClone(),
                ["status"] = "passed",
                ["evidence"] = normalizedArguments["evidence"]?.DeepClone(),
            }, cancellationToken),
            "mark_goal_fail" => await UpdateGoalStatusAsync(context, new JsonObject
            {
                ["goal_id"] = normalizedArguments["goal_id"]?.DeepClone(),
                ["status"] = "failed",
                ["note"] = normalizedArguments["reason"]?.DeepClone(),
                ["evidence"] = normalizedArguments["evidence"]?.DeepClone(),
            }, cancellationToken),
            "list_goals" => await ListGoalsAsync(context, cancellationToken),
            "end_task" => await EndTaskAsync(context, normalizedArguments, cancellationToken),
            "save_secret" => await SaveSecretAsync(context, normalizedArguments, cancellationToken),
            "get_secret" => await GetSecretAsync(context, normalizedArguments, cancellationToken),
            "list_secrets" => await ListSecretsAsync(context, cancellationToken),
            _ => await browserSessionManager.ExecuteBrowserToolAsync(
                context.TestRunId,
                toolName,
                normalizedArguments,
                context.BrowserSnapshot,
                context.LaunchHeadless,
                cancellationToken),
        };

        if (result.NormalizedArguments is null && !JsonNode.DeepEquals(arguments, normalizedArguments))
        {
            result.NormalizedArguments = normalizedArguments.DeepClone();
        }

        if (result.Hint is null && result.NormalizedArguments is not null)
        {
            result.Hint = $"Arguments were normalized before executing `{toolName}`.";
        }

        var snapshot = await browserSessionManager.GetSnapshotAsync(context.TestRunId, cancellationToken);
        if (snapshot is not null)
        {
            await repository.SaveBrowserSnapshotAsync(context.TestRunId, snapshot, cancellationToken);
        }

        return result;
    }

    private async Task<ToolExecutionResult> CreateGoalAsync(ToolInvocationContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        var title = GetString(arguments, "title");
        var successCriteria = GetString(arguments, "success_criteria");
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(successCriteria))
        {
            return ToolExecutionResult.Failed("Goal title and success criteria are required.");
        }

        var existingGoals = await repository.ListGoalsAsync(context.TestRunId, cancellationToken);
        var existingGoal = existingGoals.FirstOrDefault(goal => IsDuplicateGoal(goal, title, successCriteria));
        if (existingGoal is not null)
        {
            return ToolExecutionResult.Successful(
                "Goal already exists.",
                GoalNode(existingGoal),
                "Use the existing active-run goal ID. Do not create a duplicate goal.");
        }

        var goal = await CreateGoalAsync(context.ChatSessionId, context.TestRunId, title, successCriteria, cancellationToken);
        return ToolExecutionResult.Successful("Goal created.", GoalNode(goal));
    }

    private async Task<ToolExecutionResult> UpdateGoalStatusAsync(ToolInvocationContext context, JsonObject arguments, CancellationToken cancellationToken)
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
        var existingGoal = goals.SingleOrDefault(candidate => candidate.Id == goalId);
        if (existingGoal is null)
        {
            return ToolExecutionResult.Failed("Goal not found.");
        }

        if (existingGoal.Status is GoalStatus.Passed or GoalStatus.Failed)
        {
            var unresolved = goals
                .Where(goal => goal.Status is GoalStatus.Pending or GoalStatus.Running)
                .ToArray();

            return ToolExecutionResult.Failed(
                $"Goal is already {existingGoal.Status}; it was not changed.",
                data: new JsonObject
                {
                    ["goal"] = GoalNode(existingGoal),
                    ["unresolved_goals"] = new JsonArray(unresolved.Select(GoalNode).ToArray()),
                },
                hint: unresolved.Length == 0
                    ? "All goals are already terminal. Call end_task instead of marking the same goal again."
                    : "Use a Pending or Running goal_id from active_run.goals. Do not mark an already terminal goal again.");
        }

        var goal = await UpdateGoalStatusAsync(
            context.ChatSessionId,
            context.TestRunId,
            goalId,
            status,
            GetString(arguments, "note"),
            GetString(arguments, "evidence"),
            cancellationToken);

        return goal is null
            ? ToolExecutionResult.Failed("Goal not found.")
            : ToolExecutionResult.Successful($"Goal marked {status}.", GoalNode(goal));
    }

    private async Task<ToolExecutionResult> ListGoalsAsync(ToolInvocationContext context, CancellationToken cancellationToken)
    {
        var goals = await repository.ListGoalsAsync(context.TestRunId, cancellationToken);
        return ToolExecutionResult.Successful("Goals listed.", new JsonArray(goals.Select(GoalNode).ToArray()));
    }

    private async Task<ToolExecutionResult> EndTaskAsync(ToolInvocationContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        var outcome = GetString(arguments, "outcome");
        var summary = GetString(arguments, "summary")?.Trim();
        var testResults = GetString(arguments, "test_results")?.Trim();
        var evidence = GetString(arguments, "evidence")?.Trim();
        var remainingWork = GetString(arguments, "remaining_work")?.Trim();

        if (outcome is not ("completed" or "failed"))
        {
            return ToolExecutionResult.Failed("End task outcome must be completed or failed.");
        }

        if (string.IsNullOrWhiteSpace(summary) ||
            string.IsNullOrWhiteSpace(testResults) ||
            string.IsNullOrWhiteSpace(evidence) ||
            string.IsNullOrWhiteSpace(remainingWork))
        {
            return ToolExecutionResult.Failed("End task summary, test_results, evidence, and remaining_work are required.");
        }

        if (summary.Length < MinimumEndTaskNarrativeLength ||
            testResults.Length < MinimumEndTaskNarrativeLength)
        {
            return ToolExecutionResult.Failed(
                $"End task summary and test_results must each be paragraph-length text of at least {MinimumEndTaskNarrativeLength} characters.");
        }

        var goals = await repository.ListGoalsAsync(context.TestRunId, cancellationToken);
        var unresolved = goals
            .Where(goal => goal.Status is GoalStatus.Pending or GoalStatus.Running)
            .ToArray();

        if (goals.Count == 0 || unresolved.Length > 0)
        {
            return ToolExecutionResult.Failed(
                "Cannot end task until every active-run goal is passed or failed.",
                goals.Count == 0
                    ? "No goals exist for the active run."
                    : "Some active-run goals are still pending or running.",
                data: new JsonObject
                {
                    ["unresolved_goals"] = new JsonArray(unresolved.Select(GoalNode).ToArray()),
                },
                hint: "Use list_goals, inspect current browser evidence, then mark each active-run goal passed or failed before calling end_task again.");
        }

        var hasFailedGoal = goals.Any(goal => goal.Status == GoalStatus.Failed);
        return ToolExecutionResult.Successful(
            "Task ended.",
            new JsonObject
            {
                ["outcome"] = hasFailedGoal ? "failed" : outcome,
                ["summary"] = summary,
                ["test_results"] = testResults,
                ["evidence"] = evidence,
                ["remaining_work"] = remainingWork,
                ["goals"] = new JsonArray(goals.Select(GoalNode).ToArray()),
            });
    }

    private async Task<ToolExecutionResult> SaveSecretAsync(ToolInvocationContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        var name = GetString(arguments, "name");
        var value = GetString(arguments, "value");
        if (string.IsNullOrWhiteSpace(name) || value is null)
        {
            return ToolExecutionResult.Failed("Secret name and value are required.");
        }

        await repository.SaveSecretAsync(context.ChatSessionId, name, value, cancellationToken);
        return ToolExecutionResult.Successful("Secret saved.", new JsonObject { ["name"] = name });
    }

    private async Task<ToolExecutionResult> GetSecretAsync(ToolInvocationContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        var name = GetString(arguments, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return ToolExecutionResult.Failed("Secret name is required.");
        }

        var value = await repository.GetSecretAsync(context.ChatSessionId, name, cancellationToken);
        return value is null
            ? ToolExecutionResult.Failed($"Secret `{name}` was not found.")
            : ToolExecutionResult.Successful("Secret retrieved.", new JsonObject
            {
                ["name"] = name,
                ["value"] = value,
            });
    }

    private async Task<ToolExecutionResult> ListSecretsAsync(ToolInvocationContext context, CancellationToken cancellationToken)
    {
        var names = await repository.ListSecretNamesAsync(context.ChatSessionId, cancellationToken);
        return ToolExecutionResult.Successful("Secrets listed.", new JsonArray(names.Select(name => JsonValue.Create(name)).ToArray()));
    }

    private async Task<GoalItem> CreateGoalAsync(Guid chatId, Guid runId, string title, string successCriteria, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var goal = new GoalItem
        {
            Id = Guid.NewGuid(),
            TestRunId = runId,
            Title = title.Trim(),
            SuccessCriteria = successCriteria.Trim(),
            Status = GoalStatus.Pending,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        await repository.AddGoalAsync(goal, cancellationToken);
        await AddGoalTimelineEntryAsync(chatId, runId, goal, "Goal created.", cancellationToken);
        return goal;
    }

    private async Task<GoalItem?> UpdateGoalStatusAsync(
        Guid chatId,
        Guid runId,
        Guid goalId,
        GoalStatus status,
        string? note,
        string? evidence,
        CancellationToken cancellationToken)
    {
        var goals = await repository.ListGoalsAsync(runId, cancellationToken);
        var goal = goals.SingleOrDefault(candidate => candidate.Id == goalId);
        if (goal is null)
        {
            return null;
        }

        goal.Status = status;
        goal.Note = string.IsNullOrWhiteSpace(note) ? goal.Note : note.Trim();
        goal.Evidence = string.IsNullOrWhiteSpace(evidence) ? goal.Evidence : evidence.Trim();
        goal.UpdatedAtUtc = DateTime.UtcNow;
        goal.CompletedAtUtc = status is GoalStatus.Passed or GoalStatus.Failed
            ? DateTime.UtcNow
            : null;

        await repository.UpdateGoalAsync(goal, cancellationToken);
        await AddGoalTimelineEntryAsync(chatId, runId, goal, $"Goal marked as {status}.", cancellationToken);
        return goal;
    }

    private async Task AddGoalTimelineEntryAsync(Guid chatId, Guid runId, GoalItem goal, string summary, CancellationToken cancellationToken)
    {
        await repository.AddTimelineEntryAsync(
            new TimelineEntry
            {
                Id = Guid.NewGuid(),
                ChatSessionId = chatId,
                TestRunId = runId,
                Kind = TimelineItemKind.GoalChanged,
                Role = "system",
                Content = $"{summary} {goal.Title}",
                MetadataJson = JsonSerializer.Serialize(goal),
                CreatedAtUtc = DateTime.UtcNow,
            },
            cancellationToken);
    }

    private static LlmToolDefinition Define(string name, string description, JsonObject parameters) =>
        new()
        {
            Name = name,
            Description = description,
            Parameters = parameters,
        };

    private static JsonObject LocatorSchema(string locatorName, params JsonObject[] extraProperties) =>
        LocatorSchema(locatorName, locatorRequired: true, extraProperties);

    private static JsonObject OptionalLocatorSchema(string locatorName, params JsonObject[] extraProperties) =>
        LocatorSchema(locatorName, locatorRequired: false, extraProperties);

    private static JsonObject LocatorSchema(string locatorName, bool locatorRequired, params JsonObject[] extraProperties)
    {
        var allProperties = new List<JsonObject>
        {
            new()
            {
                ["name"] = locatorName,
                ["required"] = locatorRequired,
                ["schema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["strategy"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JsonArray("css", "xpath", "id", "name", "class", "tag", "link_text", "partial_link_text"),
                        },
                        ["value"] = new JsonObject
                        {
                            ["type"] = "string",
                        },
                    },
                    ["required"] = new JsonArray("strategy", "value"),
                },
            },
        };

        allProperties.AddRange(extraProperties);
        return Object(allProperties.ToArray());
    }

    private static string LocatorDescription(string action) =>
        $"{action} using a required locator argument shaped like {{\"locator\":{{\"strategy\":\"css\",\"value\":\"selector\"}}}}.";

    private static JsonObject Object(params JsonObject[] properties)
    {
        var objectProperties = new JsonObject();
        var required = new JsonArray();

        foreach (var property in properties)
        {
            var name = property["name"]!.GetValue<string>();
            objectProperties[name] = property["schema"]!.DeepClone();
            if (property["required"]?.GetValue<bool>() == true)
            {
                required.Add(name);
            }
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = objectProperties,
        };

        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        return schema;
    }

    private static JsonObject Property(string name, string type, bool required = false, string? description = null)
    {
        var schema = new JsonObject
        {
            ["type"] = type,
        };

        if (!string.IsNullOrWhiteSpace(description))
        {
            schema["description"] = description;
        }

        return new JsonObject
        {
            ["name"] = name,
            ["required"] = required,
            ["schema"] = schema,
        };
    }

    private static JsonObject EnumProperty(string name, string[] choices, bool required = false) =>
        new()
        {
            ["name"] = name,
            ["required"] = required,
            ["schema"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(choices.Select(choice => JsonValue.Create(choice)).ToArray()),
            },
        };

    private static JsonObject ArrayProperty(string name, JsonObject itemSchema, bool required = false) =>
        new()
        {
            ["name"] = name,
            ["required"] = required,
            ["schema"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = itemSchema.DeepClone(),
            },
        };

    private static JsonObject PrimitiveValueSchema() =>
        new()
        {
            ["anyOf"] = new JsonArray
            {
                new JsonObject { ["type"] = "string" },
                new JsonObject { ["type"] = "number" },
                new JsonObject { ["type"] = "integer" },
                new JsonObject { ["type"] = "boolean" },
                new JsonObject { ["type"] = "null" },
            },
        };

    private static JsonObject GoalNode(GoalItem goal) =>
        new()
        {
            ["id"] = goal.Id.ToString(),
            ["title"] = goal.Title,
            ["success_criteria"] = goal.SuccessCriteria,
            ["status"] = goal.Status.ToString(),
            ["note"] = goal.Note,
            ["evidence"] = goal.Evidence,
        };

    private static string? GetString(JsonObject arguments, string name) =>
        arguments[name] is JsonValue value && value.TryGetValue<string>(out var result)
            ? result
            : null;

    private static string NormalizeGoalText(string value)
    {
        var normalized = new string(value
            .Where(character => !char.IsPunctuation(character))
            .Select(char.ToLowerInvariant)
            .ToArray());
        return string.Join(' ', normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static bool IsDuplicateGoal(GoalItem goal, string title, string successCriteria)
    {
        var existingTitle = NormalizeGoalText(goal.Title);
        var requestedTitle = NormalizeGoalText(title);
        var existingCriteria = NormalizeGoalText(goal.SuccessCriteria);
        var requestedCriteria = NormalizeGoalText(successCriteria);

        return string.Equals(existingTitle, requestedTitle, StringComparison.Ordinal) ||
               string.Equals(existingCriteria, requestedCriteria, StringComparison.Ordinal) ||
               TokenSimilarity(existingCriteria, requestedCriteria) >= 0.75d;
    }

    private static double TokenSimilarity(string left, string right)
    {
        var leftTokens = left.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var rightTokens = right.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        if (leftTokens.Count == 0 || rightTokens.Count == 0)
        {
            return 0d;
        }

        var intersection = leftTokens.Intersect(rightTokens, StringComparer.Ordinal).Count();
        var union = leftTokens.Union(rightTokens, StringComparer.Ordinal).Count();
        return union == 0 ? 0d : (double)intersection / union;
    }

    private static JsonObject NormalizeArguments(string toolName, LlmToolDefinition? definition, JsonObject arguments)
    {
        var normalized = (JsonObject)arguments.DeepClone();
        if (definition is not null)
        {
            var normalizedFromSchema = NormalizeNodeFromSchema(normalized, definition.Parameters) as JsonObject;
            if (normalizedFromSchema is not null)
            {
                normalized = normalizedFromSchema;
            }
        }

        if (!LocatorToolNames.Contains(toolName) || normalized["locator"] is not null)
        {
            return normalized;
        }

        if (GetString(normalized, "strategy") is not { } strategy ||
            GetString(normalized, "value") is not { } value)
        {
            return normalized;
        }

        normalized["locator"] = new JsonObject
        {
            ["strategy"] = strategy,
            ["value"] = value,
        };
        normalized.Remove("strategy");
        normalized.Remove("value");
        return normalized;
    }

    private static JsonNode? NormalizeNodeFromSchema(JsonNode? value, JsonObject? schema)
    {
        if (value is null || schema is null)
        {
            return value;
        }

        var schemaType = schema["type"]?.GetValue<string>();
        if (value is JsonValue jsonValue &&
            jsonValue.TryGetValue<string>(out var stringValue) &&
            (string.Equals(schemaType, "object", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(schemaType, "array", StringComparison.OrdinalIgnoreCase)))
        {
            var parsed = TryParseEmbeddedJson(stringValue, schemaType);
            if (parsed is not null)
            {
                value = parsed;
            }
        }

        if (string.Equals(schemaType, "object", StringComparison.OrdinalIgnoreCase) &&
            value is JsonObject objectValue)
        {
            var properties = schema["properties"]?.AsObject();
            if (properties is null)
            {
                return objectValue;
            }

            foreach (var property in properties)
            {
                if (property.Value is not JsonObject propertySchema || objectValue[property.Key] is null)
                {
                    continue;
                }

                objectValue[property.Key] = NormalizeNodeFromSchema(objectValue[property.Key], propertySchema);
            }

            return objectValue;
        }

        if (string.Equals(schemaType, "array", StringComparison.OrdinalIgnoreCase) &&
            value is JsonArray arrayValue &&
            schema["items"] is JsonObject itemSchema)
        {
            for (var index = 0; index < arrayValue.Count; index++)
            {
                arrayValue[index] = NormalizeNodeFromSchema(arrayValue[index], itemSchema);
            }
        }

        return value;
    }

    private static JsonNode? TryParseEmbeddedJson(string rawValue, string? expectedType)
    {
        var trimmed = rawValue.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        var looksLikeObject = trimmed.StartsWith('{') && trimmed.EndsWith('}');
        var looksLikeArray = trimmed.StartsWith('[') && trimmed.EndsWith(']');
        if (!looksLikeObject && !looksLikeArray)
        {
            return null;
        }

        try
        {
            var parsed = JsonNode.Parse(trimmed);
            return expectedType switch
            {
                "object" when parsed is JsonObject => parsed,
                "array" when parsed is JsonArray => parsed,
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    private static ToolArgumentValidationResult? Validate(LlmToolDefinition definition, JsonObject arguments)
    {
        var schema = definition.Parameters;
        if (!string.Equals(schema["type"]?.GetValue<string>(), "object", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var properties = schema["properties"]?.AsObject();
        var required = schema["required"]?.AsArray();
        if (properties is null)
        {
            return null;
        }

        var issues = new List<string>();
        ValidateObject(arguments, properties, required, string.Empty, issues);
        if (issues.Count == 0)
        {
            return null;
        }

        var hint = BuildHint(arguments, properties);
        return new ToolArgumentValidationResult(
            $"Tool `{definition.Name}` received invalid arguments.",
            string.Join(" ", issues),
            schema.DeepClone(),
            BuildExampleArguments(properties, required),
            hint);
    }

    private static void ValidateObject(JsonObject value, JsonObject properties, JsonArray? required, string path, List<string> issues)
    {
        if (required is not null)
        {
            foreach (var item in required)
            {
                var propertyName = item?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(propertyName) && value[propertyName] is null)
                {
                    issues.Add($"Missing required argument `{BuildPath(path, propertyName)}`.");
                }
            }
        }

        foreach (var property in properties)
        {
            if (property.Value is JsonObject propertySchema && value[property.Key] is { } propertyValue)
            {
                ValidateNode(propertyValue, propertySchema, BuildPath(path, property.Key), issues);
            }
        }
    }

    private static void ValidateNode(JsonNode? value, JsonObject schema, string path, List<string> issues)
    {
        if (schema["anyOf"] is JsonArray anyOfSchemas && anyOfSchemas.Count > 0)
        {
            foreach (var candidate in anyOfSchemas.OfType<JsonObject>())
            {
                var candidateIssues = new List<string>();
                ValidateNode(value, candidate, path, candidateIssues);
                if (candidateIssues.Count == 0)
                {
                    return;
                }
            }

            issues.Add($"Argument `{path}` does not match any allowed type.");
            return;
        }

        var type = schema["type"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(type))
        {
            return;
        }

        if (schema["enum"] is JsonArray choices && choices.Count > 0 && value is JsonValue enumValue)
        {
            var allowed = choices
                .Select(choice => choice?.GetValue<string>())
                .Where(choice => !string.IsNullOrWhiteSpace(choice))
                .ToArray();
            if (enumValue.TryGetValue<string>(out var text) &&
                allowed.Any(choice => string.Equals(choice, text, StringComparison.Ordinal)))
            {
                return;
            }

            issues.Add($"Argument `{path}` must be one of: {string.Join(", ", allowed)}.");
            return;
        }

        if (value is null)
        {
            if (!string.Equals(type, "null", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add($"Argument `{path}` must not be null.");
            }

            return;
        }

        switch (type)
        {
            case "object":
                if (value is not JsonObject objectValue)
                {
                    issues.Add($"Argument `{path}` must be an object.");
                    return;
                }

                ValidateObject(objectValue, schema["properties"]?.AsObject() ?? new JsonObject(), schema["required"]?.AsArray(), path, issues);
                break;

            case "array":
                if (value is not JsonArray arrayValue)
                {
                    issues.Add($"Argument `{path}` must be an array.");
                    return;
                }

                if (schema["items"] is JsonObject itemSchema)
                {
                    for (var index = 0; index < arrayValue.Count; index++)
                    {
                        ValidateNode(arrayValue[index], itemSchema, $"{path}[{index}]", issues);
                    }
                }

                break;

            case "string":
                if (!IsString(value))
                {
                    issues.Add($"Argument `{path}` must be a string.");
                }

                break;

            case "integer":
                if (!IsInteger(value))
                {
                    issues.Add($"Argument `{path}` must be an integer.");
                }

                break;

            case "number":
                if (!IsNumber(value))
                {
                    issues.Add($"Argument `{path}` must be a number.");
                }

                break;

            case "boolean":
                if (!IsBoolean(value))
                {
                    issues.Add($"Argument `{path}` must be a boolean.");
                }

                break;

            case "null":
                if (value is not null)
                {
                    issues.Add($"Argument `{path}` must be null.");
                }

                break;
        }
    }

    private static ToolArgumentHint? BuildHint(JsonObject arguments, JsonObject properties)
    {
        if (properties.ContainsKey("locator") &&
            arguments["locator"] is null &&
            IsString(arguments["strategy"]) &&
            IsString(arguments["value"]))
        {
            var strategy = arguments["strategy"]!.GetValue<string>();
            var value = arguments["value"]!.GetValue<string>();

            return new ToolArgumentHint(
                "Wrap `strategy` and `value` inside a top-level `locator` object.",
                new JsonObject
                {
                    ["locator"] = new JsonObject
                    {
                        ["strategy"] = strategy,
                        ["value"] = value,
                    },
                });
        }

        return null;
    }

    private static JsonObject BuildExampleArguments(JsonObject properties, JsonArray? required)
    {
        var example = new JsonObject();
        var requiredNames = required?
            .Select(item => item?.GetValue<string>())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal)
            ?? [];

        foreach (var property in properties)
        {
            if (property.Value is JsonObject propertySchema &&
                (requiredNames.Count == 0 || requiredNames.Contains(property.Key)))
            {
                example[property.Key] = BuildExampleValue(property.Key, propertySchema);
            }
        }

        return example;
    }

    private static JsonNode? BuildExampleValue(string propertyName, JsonObject schema)
    {
        if (schema["anyOf"] is JsonArray anyOfSchemas)
        {
            foreach (var candidate in anyOfSchemas.OfType<JsonObject>())
            {
                if (string.Equals(candidate["type"]?.GetValue<string>(), "null", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var example = BuildExampleValue(propertyName, candidate);
                if (example is not null)
                {
                    return example;
                }
            }
        }

        if (schema["enum"] is JsonArray choices && choices.Count > 0)
        {
            return choices[0]?.DeepClone();
        }

        return schema["type"]?.GetValue<string>() switch
        {
            "object" => BuildExampleArguments(schema["properties"]?.AsObject() ?? new JsonObject(), schema["required"]?.AsArray()),
            "array" => new JsonArray(),
            "integer" => JsonValue.Create(5000),
            "boolean" => JsonValue.Create(true),
            "string" => JsonValue.Create(GetExampleString(propertyName)),
            _ => null,
        };
    }

    private static string GetExampleString(string propertyName) =>
        propertyName switch
        {
            "url" => "https://www.google.com",
            "goal_id" => "<goal-id>",
            "success_criteria" => "Observed expected page evidence",
            "title" => "Verify page behavior",
            "text" => "Example text",
            "value" => "selector-or-value",
            "strategy" => "css",
            "reason" => "Observed behavior did not meet the goal",
            "summary" => "I created the active-run goals, drove the browser through the requested workflow, inspected the resulting page state, and recorded evidence against each goal before ending the run.",
            "evidence" => "Captured page evidence",
            "test_results" => "All requested test goals passed. Each active-run goal was marked with observed browser evidence, and no unresolved pending or running goals remained when the run was ended.",
            "attribute" => "aria-label",
            _ => $"<{propertyName}>",
        };

    private static string BuildPath(string path, string propertyName) =>
        string.IsNullOrWhiteSpace(path) ? propertyName : $"{path}.{propertyName}";

    private static bool IsString(JsonNode? value) =>
        value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out _);

    private static bool IsBoolean(JsonNode? value) =>
        value is JsonValue jsonValue && jsonValue.TryGetValue<bool>(out _);

    private static bool IsInteger(JsonNode? value) =>
        value is JsonValue jsonValue && (jsonValue.TryGetValue<int>(out _) || jsonValue.TryGetValue<long>(out _));

    private static bool IsNumber(JsonNode? value) =>
        value is JsonValue jsonValue &&
        (jsonValue.TryGetValue<double>(out _) ||
         jsonValue.TryGetValue<decimal>(out _) ||
         jsonValue.TryGetValue<int>(out _) ||
         jsonValue.TryGetValue<long>(out _));

    private sealed record ToolArgumentValidationResult(
        string Summary,
        string Error,
        JsonNode? ExpectedArguments,
        JsonNode? ExampleArguments,
        ToolArgumentHint? Hint);

    private sealed record ToolArgumentHint(
        string Message,
        JsonNode? NormalizedArguments);
}
