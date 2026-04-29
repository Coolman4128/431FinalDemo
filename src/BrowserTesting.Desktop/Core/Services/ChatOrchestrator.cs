using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BrowserTesting.Core.Llm;
using BrowserTesting.Core.Models;
using BrowserTesting.Core.Orchestration;
using BrowserTesting.Infrastructure.Browser;
using BrowserTesting.Infrastructure.Llm;
using BrowserTesting.Infrastructure.Persistence;
using BrowserTesting.Infrastructure.Secrets;
using BrowserTesting.Infrastructure.Tools;

namespace BrowserTesting.Core.Services;

public sealed class ChatOrchestrator(
    SqliteChatRepository repository,
    LmStudioLlmClient llmClient,
    ToolRegistry toolRegistry,
    ToolExecutor toolExecutor,
    BrowserSessionManager browserSessionManager,
    DpapiSecretStore secretStore,
    AppSettings settings)
{
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
                        result = await toolExecutor.ExecuteAsync(
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
            Tools = toolRegistry.GetToolDefinitions(),
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
        foreach (var name in secretStore.ListSecretNamesAsync(chat.Id, CancellationToken.None).GetAwaiter().GetResult())
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

    private static int GetConsecutiveFailureCount(ChatSession chat, Guid runId)
    {
        var count = 0;
        foreach (var entry in chat.Timeline
                     .Where(candidate => candidate.TestRunId == runId && candidate.Kind == TimelineItemKind.ToolCallFinished)
                     .OrderByDescending(candidate => candidate.Sequence))
        {
            var metadata = ParseMetadata(entry.MetadataJson);
            var success = metadata?["success"]?.GetValue<bool>();
            if (success is true)
            {
                break;
            }

            count++;
        }

        return count;
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

        public static ToolCallAccumulator FromToolCall(LlmToolCall toolCall)
        {
            var accumulator = new ToolCallAccumulator(toolCall.Index);
            accumulator.identifier.Append(toolCall.Id);
            accumulator.name.Append(toolCall.Name);
            accumulator.arguments.Append(toolCall.ArgumentsJson);
            return accumulator;
        }
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
