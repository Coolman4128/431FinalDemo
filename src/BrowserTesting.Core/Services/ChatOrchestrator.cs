using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BrowserTesting.Core.Abstractions;
using BrowserTesting.Core.Llm;
using BrowserTesting.Core.Models;
using BrowserTesting.Core.Orchestration;

namespace BrowserTesting.Core.Services;

public sealed class ChatOrchestrator(
    IChatRepository repository,
    ILlmClient llmClient,
    IToolRegistry toolRegistry,
    IToolExecutor toolExecutor,
    IBrowserSessionManager browserSessionManager,
    ISecretStore secretStore,
    AppSettings settings) : IChatOrchestrator
{
    public Task InitializeAsync(CancellationToken cancellationToken) =>
        repository.InitializeAsync(cancellationToken);

    public Task<IReadOnlyList<ChatSessionSummary>> ListChatsAsync(CancellationToken cancellationToken) =>
        repository.ListChatsAsync(cancellationToken);

    public Task<ChatSession> CreateChatAsync(string? title, CancellationToken cancellationToken) =>
        repository.CreateChatAsync(title, cancellationToken);

    public async Task<BrowserSessionSnapshot?> RefreshBrowserSnapshotAsync(
        Guid runId,
        Action<OrchestratorUpdate>? onUpdate,
        CancellationToken cancellationToken)
    {
        var snapshot = await browserSessionManager.GetSnapshotAsync(runId, cancellationToken);
        if (snapshot is null)
        {
            return null;
        }

        onUpdate?.Invoke(new BrowserSnapshotUpdated(runId, snapshot));
        return snapshot;
    }

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

        if (onUpdate is not null)
        {
            var run = chat.Runs
                .OrderByDescending(candidate => candidate.UpdatedAtUtc)
                .FirstOrDefault();

            if (run is not null && RequiresResumeNormalization(run.BrowserSnapshot))
            {
                run.BrowserSnapshot = CloseSnapshot(run.BrowserSnapshot);
                await repository.SaveBrowserSnapshotAsync(run.Id, run.BrowserSnapshot, cancellationToken);
            }
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
            var stalledTurns = 0;
            for (var iteration = 0; iteration < settings.MaxToolIterations; iteration++)
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

                var toolDefinitions = toolRegistry.GetToolDefinitions();
                var toolNames = toolDefinitions.Select(definition => definition.Name).ToArray();
                var toolBuilders = new Dictionary<int, ToolCallAccumulator>();
                await foreach (var streamEvent in llmClient.StreamCompletionAsync(BuildRequest(chat, run, settings.MaxToolIterations - iteration, connection), cancellationToken))
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

                if (toolBuilders.Count == 0 &&
                    TryRecoverToolCalls(
                        assistantEntry.Content,
                        toolNames,
                        out var recoveredContent,
                        out var recoveredToolCalls))
                {
                    assistantEntry.Content = recoveredContent;
                    await repository.UpdateTimelineEntryAsync(assistantEntry, cancellationToken);
                    onUpdate(new TimelineEntryUpserted(assistantEntry));

                    foreach (var recoveredToolCall in recoveredToolCalls)
                    {
                        toolBuilders[recoveredToolCall.Index] = ToolCallAccumulator.FromToolCall(recoveredToolCall);
                    }
                }

                if (toolBuilders.Count == 0 &&
                    TryInferToolCallsFromIntent(
                        assistantEntry.Content,
                        run,
                        toolNames,
                        out var inferredContent,
                        out var inferredToolCalls))
                {
                    assistantEntry.Content = inferredContent;
                    await repository.UpdateTimelineEntryAsync(assistantEntry, cancellationToken);
                    onUpdate(new TimelineEntryUpserted(assistantEntry));

                    foreach (var inferredToolCall in inferredToolCalls)
                    {
                        toolBuilders[inferredToolCall.Index] = ToolCallAccumulator.FromToolCall(inferredToolCall);
                    }

                    await AddSystemNoticeAsync(
                        chatId,
                        run.Id,
                        "Recovered an implied tool action from the assistant text. Emit the tool call directly next time instead of only describing the next step.",
                        onUpdate,
                        cancellationToken);
                }

                if (toolBuilders.Count == 0)
                {
                    if (TryDetectNarratedToolIntent(
                            assistantEntry.Content,
                            toolNames,
                            out var mentionedToolName))
                    {
                        await AddSystemNoticeAsync(
                            chatId,
                            run.Id,
                            $"Your last reply mentioned the tool `{mentionedToolName}` but did not emit a structured tool call. On the next turn, call the tool directly instead of narrating it in plain text.",
                            onUpdate,
                            cancellationToken);
                    }

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
                    run.Status = InferRunStatusFromGoals(run);
                    if (run.Status is TestRunStatus.Completed or TestRunStatus.Failed)
                    {
                        run.CompletedAtUtc = DateTime.UtcNow;
                        run.UpdatedAtUtc = DateTime.UtcNow;
                        await repository.UpdateRunAsync(run, cancellationToken);
                        onUpdate(new RunUpdated(run));
                        break;
                    }

                    if (stalledTurns >= 3)
                    {
                        run.Status = TestRunStatus.Failed;
                        run.FailureReason = "The model produced repeated empty or no-tool turns while goals were still unresolved.";
                        run.CompletedAtUtc = DateTime.UtcNow;
                        run.UpdatedAtUtc = DateTime.UtcNow;
                        await repository.UpdateRunAsync(run, cancellationToken);
                        onUpdate(new RunUpdated(run));
                        break;
                    }

                    await AddSystemNoticeAsync(
                        chatId,
                        run.Id,
                        "Goals are still unresolved. Continue using tools until you have enough evidence to pass or fail them.",
                        onUpdate,
                        cancellationToken);

                    run.Status = TestRunStatus.Running;
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
                var stopForRepeatedFailureLoop = false;
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
                    if (result.Success)
                    {
                        repeatedFailures.Reset();
                    }
                    else
                    {
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

                    if (!result.Success)
                    {
                        await AddSystemNoticeAsync(
                            chatId,
                            run.Id,
                            BuildToolFailureNotice(toolCall.Name, result, repeatedAttemptCount),
                            onUpdate,
                            cancellationToken);

                        if (repeatedAttemptCount >= 3)
                        {
                            run.Status = TestRunStatus.Failed;
                            run.FailureReason = $"Stopped after {repeatedAttemptCount} identical `{toolCall.Name}` failures. Change strategy instead of repeating the same tool call.";
                            run.CompletedAtUtc = DateTime.UtcNow;
                            run.UpdatedAtUtc = DateTime.UtcNow;
                            await repository.UpdateRunAsync(run, cancellationToken);
                            onUpdate(new RunUpdated(run));
                            stopForRepeatedFailureLoop = true;
                            break;
                        }

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

                if (stopForRepeatedFailureLoop)
                {
                    break;
                }
            }

            run = await RefreshRunStateAsync(chatId, run.Id, onUpdate, cancellationToken);
            if (run.Status is not TestRunStatus.Completed and not TestRunStatus.Failed and not TestRunStatus.Cancelled)
            {
                run.Status = InferRunStatusFromGoals(run);
                if (run.Status is not TestRunStatus.Completed and not TestRunStatus.Failed)
                {
                    run.Status = TestRunStatus.Failed;
                    run.FailureReason = $"The run did not finish within {settings.MaxToolIterations} LLM turns.";
                }

                run.CompletedAtUtc = DateTime.UtcNow;
                run.UpdatedAtUtc = DateTime.UtcNow;
                await repository.UpdateRunAsync(run, cancellationToken);
                onUpdate(new RunUpdated(run));
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

    private LlmRequest BuildRequest(ChatSession chat, TestRun run, int turnsRemaining, LlmConnectionSettings connection) =>
        new()
        {
            Connection = connection,
            Tools = toolRegistry.GetToolDefinitions(),
            Messages = BuildConversation(chat, run, turnsRemaining, connection),
        };

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
        };

        var pendingAssistantContent = string.Empty;
        var pendingToolCalls = new List<LlmToolCall>();
        var pendingToolCallIds = new HashSet<string>(StringComparer.Ordinal);
        var bufferedMessages = new List<LlmConversationMessage>();

        foreach (var entry in chat.Timeline.OrderBy(candidate => candidate.Sequence))
        {
            switch (entry.Kind)
            {
                case TimelineItemKind.UserMessage:
                    FlushBufferedMessages(messages, bufferedMessages);
                    FlushAssistantMessage(messages, ref pendingAssistantContent, pendingToolCalls);
                    messages.Add(new LlmConversationMessage { Role = "user", Content = entry.Content });
                    break;
                case TimelineItemKind.AssistantMessage:
                    FlushBufferedMessages(messages, bufferedMessages);
                    FlushAssistantMessage(messages, ref pendingAssistantContent, pendingToolCalls);
                    pendingAssistantContent = entry.Content;
                    break;
                case TimelineItemKind.ToolCallStarted:
                    var toolCall = new LlmToolCall
                    {
                        Index = pendingToolCalls.Count,
                        Id = entry.ToolCallId ?? Guid.NewGuid().ToString("N"),
                        Name = entry.ToolName ?? "unknown_tool",
                        ArgumentsJson = entry.MetadataJson ?? "{}",
                    };
                    pendingToolCalls.Add(toolCall);
                    pendingToolCallIds.Add(toolCall.Id);
                    break;
                case TimelineItemKind.ToolCallFinished:
                    FlushAssistantMessage(messages, ref pendingAssistantContent, pendingToolCalls);
                    messages.Add(new LlmConversationMessage
                    {
                        Role = "tool",
                        ToolCallId = entry.ToolCallId,
                        Name = entry.ToolName,
                        Content = entry.MetadataJson ?? entry.Content,
                    });
                    if (!string.IsNullOrWhiteSpace(entry.ToolCallId))
                    {
                        pendingToolCallIds.Remove(entry.ToolCallId);
                    }

                    FlushBufferedMessagesIfToolBlockCompleted(messages, bufferedMessages, pendingToolCallIds);
                    break;
                case TimelineItemKind.SystemNotice:
                    AddSystemOrBufferedMessage(
                        messages,
                        bufferedMessages,
                        ref pendingAssistantContent,
                        pendingToolCalls,
                        pendingToolCallIds,
                        $"System notice: {entry.Content}");
                    break;
                case TimelineItemKind.GoalChanged:
                    AddSystemOrBufferedMessage(
                        messages,
                        bufferedMessages,
                        ref pendingAssistantContent,
                        pendingToolCalls,
                        pendingToolCallIds,
                        FormatGoalChangedMessage(entry));
                    break;
            }
        }

        FlushAssistantMessage(messages, ref pendingAssistantContent, pendingToolCalls);
        FlushBufferedMessages(messages, bufferedMessages);
        return messages;
    }

    private static void AddSystemOrBufferedMessage(
        List<LlmConversationMessage> messages,
        List<LlmConversationMessage> bufferedMessages,
        ref string pendingAssistantContent,
        List<LlmToolCall> pendingToolCalls,
        HashSet<string> pendingToolCallIds,
        string content)
    {
        var message = new LlmConversationMessage
        {
            Role = "system",
            Content = content,
        };

        if (pendingToolCallIds.Count > 0 || pendingToolCalls.Count > 0)
        {
            bufferedMessages.Add(message);
            return;
        }

        FlushBufferedMessages(messages, bufferedMessages);
        FlushAssistantMessage(messages, ref pendingAssistantContent, pendingToolCalls);
        messages.Add(message);
    }

    private static void FlushBufferedMessagesIfToolBlockCompleted(
        List<LlmConversationMessage> messages,
        List<LlmConversationMessage> bufferedMessages,
        HashSet<string> pendingToolCallIds)
    {
        if (pendingToolCallIds.Count == 0)
        {
            FlushBufferedMessages(messages, bufferedMessages);
        }
    }

    private static void FlushBufferedMessages(
        List<LlmConversationMessage> messages,
        List<LlmConversationMessage> bufferedMessages)
    {
        if (bufferedMessages.Count == 0)
        {
            return;
        }

        messages.AddRange(bufferedMessages);
        bufferedMessages.Clear();
    }

    private string BuildSystemPrompt(ChatSession chat, TestRun activeRun, int turnsRemaining)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are a browser-testing agent controlling Selenium tools.");
        builder.AppendLine("Operating playbook:");
        builder.AppendLine("- Always create explicit goals before significant browser work.");
        builder.AppendLine("- Update goals as work progresses and only mark them passed or failed from observed page evidence.");
        builder.AppendLine("- If any goal is still pending or running, continue using tools until it is resolved.");
        builder.AppendLine("- Inspect browser/page state before guessing selectors or outcomes.");
        builder.AppendLine("- Use exact tool argument names and shapes from the tool schema.");
        builder.AppendLine("- If a tool fails, read the tool result JSON and change strategy. Do not repeat the same tool name with the same arguments.");
        builder.AppendLine("- After repeated failures, switch approach, inspect state, or fail the relevant goal with evidence if blocked.");
        builder.AppendLine("- Keep assistant text brief and useful. Tool activity is rendered separately in the UI.");
        builder.AppendLine();
        builder.AppendLine("Critical tool shapes:");
        builder.AppendLine("- Locator tools such as find_element, click, get_text, type_text, wait_for_element: {\"locator\":{\"strategy\":\"css\",\"value\":\"input[name='q']\"}}");
        builder.AppendLine("- mark_goal_pass: {\"goal_id\":\"<goal-id>\",\"evidence\":\"Observed expected result on the page.\"}");
        builder.AppendLine("- mark_goal_fail: {\"goal_id\":\"<goal-id>\",\"reason\":\"Why the goal failed.\",\"evidence\":\"Observed blocking evidence.\"}");
        builder.AppendLine("- update_goal_status: {\"goal_id\":\"<goal-id>\",\"status\":\"running|passed|failed\",\"note\":\"Optional note\",\"evidence\":\"Optional evidence\"}");
        builder.AppendLine();
        builder.AppendLine("Current run state (treat this as the source of truth for the active run):");
        builder.AppendLine(JsonSerializer.Serialize(new
        {
            activeRun.Id,
            Status = activeRun.Status.ToString(),
            activeRun.UserPrompt,
            Browser = activeRun.BrowserSnapshot,
            Goals = activeRun.Goals,
            SavedSecrets = secretStore.ListSecretNamesAsync(chat.Id, CancellationToken.None).GetAwaiter().GetResult(),
        }));
        builder.AppendLine();
        builder.AppendLine("Recent execution context:");
        builder.AppendLine(JsonSerializer.Serialize(new
        {
            TurnsRemaining = turnsRemaining,
            Browser = activeRun.BrowserSnapshot,
            ConsecutiveFailureCount = GetConsecutiveFailureCount(chat, activeRun.Id),
            RecentToolOutcomes = GetRecentToolOutcomes(chat, activeRun.Id),
        }));
        return builder.ToString();
    }

    private static void FlushAssistantMessage(List<LlmConversationMessage> messages, ref string pendingAssistantContent, List<LlmToolCall> pendingToolCalls)
    {
        if (string.IsNullOrWhiteSpace(pendingAssistantContent) && pendingToolCalls.Count == 0)
        {
            return;
        }

        messages.Add(new LlmConversationMessage
        {
            Role = "assistant",
            Content = pendingToolCalls.Count > 0
                ? null
                : string.IsNullOrWhiteSpace(pendingAssistantContent) ? null : pendingAssistantContent,
            ToolCalls = pendingToolCalls.Count == 0 ? null : pendingToolCalls.ToArray(),
        });

        pendingAssistantContent = string.Empty;
        pendingToolCalls.Clear();
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

    private static bool TryRecoverToolCalls(
        string content,
        IReadOnlyCollection<string> toolNames,
        out string cleanedContent,
        out IReadOnlyList<LlmToolCall> toolCalls)
    {
        if (TryExtractTaggedToolCalls(content, out cleanedContent, out toolCalls))
        {
            return true;
        }

        if (TryExtractJsonEnvelopeToolCalls(content, toolNames, out cleanedContent, out toolCalls))
        {
            return true;
        }

        if (TryExtractNarratedToolCalls(content, toolNames, out cleanedContent, out toolCalls))
        {
            return true;
        }

        cleanedContent = content;
        toolCalls = [];
        return false;
    }

    private static bool TryExtractTaggedToolCalls(
        string content,
        out string cleanedContent,
        out IReadOnlyList<LlmToolCall> toolCalls)
    {
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.Singleline;
        var toolCallMatches = Regex.Matches(
            content,
            @"<tool_call>\s*<function=(?<name>[^>\s]+)>\s*(?<body>.*?)\s*</function>\s*</tool_call>",
            options);

        if (toolCallMatches.Count == 0)
        {
            cleanedContent = content;
            toolCalls = [];
            return false;
        }

        var extracted = new List<LlmToolCall>();
        var index = 0;
        foreach (Match match in toolCallMatches)
        {
            var functionName = match.Groups["name"].Value.Trim();
            var body = match.Groups["body"].Value;
            var arguments = new JsonObject();

            var parameterMatches = Regex.Matches(
                body,
                @"<parameter=(?<name>[^>\s]+)>\s*(?<value>.*?)\s*</parameter>",
                options);

            foreach (Match parameterMatch in parameterMatches)
            {
                var parameterName = parameterMatch.Groups["name"].Value.Trim();
                var rawValue = parameterMatch.Groups["value"].Value.Trim();
                arguments[parameterName] = CoerceTaggedValue(rawValue);
            }

            extracted.Add(new LlmToolCall
            {
                Index = index++,
                Id = Guid.NewGuid().ToString("N"),
                Name = functionName,
                ArgumentsJson = arguments.ToJsonString(),
            });
        }

        cleanedContent = Regex.Replace(
            content,
            @"<tool_call>\s*<function=[^>\s]+>\s*.*?\s*</function>\s*</tool_call>",
            string.Empty,
            options).Trim();

        toolCalls = extracted;
        return true;
    }

    private static bool TryExtractJsonEnvelopeToolCalls(
        string content,
        IReadOnlyCollection<string> toolNames,
        out string cleanedContent,
        out IReadOnlyList<LlmToolCall> toolCalls)
    {
        var extracted = new List<LlmToolCall>();
        var rewritten = content;

        var codeBlockMatches = Regex.Matches(
            content,
            @"```(?:json)?\s*(?<body>\{.*?\})\s*```",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match match in codeBlockMatches)
        {
            if (!TryParseJsonEnvelopeToolCall(match.Groups["body"].Value, toolNames, extracted.Count, out var toolCall))
            {
                continue;
            }

            extracted.Add(toolCall);
            rewritten = rewritten.Replace(match.Value, string.Empty, StringComparison.Ordinal);
        }

        if (extracted.Count == 0)
        {
            cleanedContent = content;
            toolCalls = [];
            return false;
        }

        cleanedContent = rewritten.Trim();
        toolCalls = extracted;
        return true;
    }

    private static bool TryParseJsonEnvelopeToolCall(
        string rawJson,
        IReadOnlyCollection<string> toolNames,
        int index,
        out LlmToolCall toolCall)
    {
        toolCall = default!;

        try
        {
            var envelope = JsonNode.Parse(rawJson)?.AsObject();
            if (envelope is null)
            {
                return false;
            }

            var candidateName = envelope["tool"]?.GetValue<string>()
                ?? envelope["name"]?.GetValue<string>()
                ?? envelope["function"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(candidateName) || !toolNames.Contains(candidateName))
            {
                return false;
            }

            var arguments = envelope["arguments"]?.AsObject()
                ?? envelope["params"]?.AsObject()
                ?? envelope["parameters"]?.AsObject();
            if (arguments is null)
            {
                return false;
            }

            toolCall = new LlmToolCall
            {
                Index = index,
                Id = Guid.NewGuid().ToString("N"),
                Name = candidateName,
                ArgumentsJson = arguments.ToJsonString(),
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryExtractNarratedToolCalls(
        string content,
        IReadOnlyCollection<string> toolNames,
        out string cleanedContent,
        out IReadOnlyList<LlmToolCall> toolCalls)
    {
        var extracted = new List<LlmToolCall>();
        var remaining = content;

        foreach (var toolName in toolNames)
        {
            while (TryExtractNarratedToolCall(remaining, toolName, extracted.Count, out var toolCall, out remaining))
            {
                extracted.Add(toolCall);
            }
        }

        if (extracted.Count == 0)
        {
            cleanedContent = content;
            toolCalls = [];
            return false;
        }

        cleanedContent = remaining.Trim();
        toolCalls = extracted;
        return true;
    }

    private static bool TryExtractNarratedToolCall(
        string content,
        string toolName,
        int index,
        out LlmToolCall toolCall,
        out string updatedContent)
    {
        toolCall = default!;
        updatedContent = content;

        var toolMatch = Regex.Match(
            content,
            $@"(?<prefix>(?:calling|call|use|using|invoke|invoking)\s+)?`?{Regex.Escape(toolName)}`?",
            RegexOptions.IgnoreCase);
        if (!toolMatch.Success)
        {
            return false;
        }

        var searchStart = toolMatch.Index + toolMatch.Length;
        var jsonStart = content.IndexOf('{', searchStart);
        if (jsonStart < 0)
        {
            return false;
        }

        if (!TryExtractBalancedJsonObject(content, jsonStart, out var rawArguments, out var jsonEndExclusive))
        {
            return false;
        }

        try
        {
            var arguments = JsonNode.Parse(rawArguments)?.AsObject();
            if (arguments is null)
            {
                return false;
            }

            toolCall = new LlmToolCall
            {
                Index = index,
                Id = Guid.NewGuid().ToString("N"),
                Name = toolName,
                ArgumentsJson = arguments.ToJsonString(),
            };

            updatedContent = (content[..toolMatch.Index] + content[jsonEndExclusive..]).Trim();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryExtractBalancedJsonObject(
        string content,
        int startIndex,
        out string rawJson,
        out int endExclusive)
    {
        rawJson = string.Empty;
        endExclusive = startIndex;

        var depth = 0;
        var inString = false;
        var escaping = false;

        for (var index = startIndex; index < content.Length; index++)
        {
            var current = content[index];
            if (escaping)
            {
                escaping = false;
                continue;
            }

            if (current == '\\' && inString)
            {
                escaping = true;
                continue;
            }

            if (current == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString)
            {
                continue;
            }

            if (current == '{')
            {
                depth++;
            }
            else if (current == '}')
            {
                depth--;
                if (depth == 0)
                {
                    endExclusive = index + 1;
                    rawJson = content[startIndex..endExclusive];
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryDetectNarratedToolIntent(
        string content,
        IEnumerable<string> toolNames,
        out string? toolName)
    {
        foreach (var candidate in toolNames)
        {
            if (Regex.IsMatch(
                    content,
                    $@"(?:call|calling|use|using|invoke|invoking).{{0,40}}`?{Regex.Escape(candidate)}`?",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                toolName = candidate;
                return true;
            }
        }

        toolName = null;
        return false;
    }

    private static bool TryInferToolCallsFromIntent(
        string content,
        TestRun run,
        IReadOnlyCollection<string> toolNames,
        out string cleanedContent,
        out IReadOnlyList<LlmToolCall> toolCalls)
    {
        var extracted = new List<LlmToolCall>();
        cleanedContent = content;

        if (TryInferOpenBrowserToolCall(content, run, toolNames, out var openBrowserCall))
        {
            extracted.Add(openBrowserCall);
            cleanedContent = RemoveSentenceContaining(content, "open");
        }
        else if (TryInferNoArgumentToolCall(content, toolNames, "list_goals", "list goals", out var listGoalsCall))
        {
            extracted.Add(listGoalsCall);
            cleanedContent = RemoveSentenceContaining(content, "list goals");
        }
        else if (TryInferNoArgumentToolCall(content, toolNames, "get_page_state", "page state", out var pageStateCall))
        {
            extracted.Add(pageStateCall);
            cleanedContent = RemoveSentenceContaining(content, "page state");
        }

        toolCalls = extracted;
        return extracted.Count > 0;
    }

    private static bool TryInferOpenBrowserToolCall(
        string content,
        TestRun run,
        IReadOnlyCollection<string> toolNames,
        out LlmToolCall toolCall)
    {
        toolCall = default!;

        if (!toolNames.Contains("open_browser"))
        {
            return false;
        }

        if (!Regex.IsMatch(content, @"\b(open|opening|launch|launching)\b.*\b(browser|chrome)\b", RegexOptions.IgnoreCase | RegexOptions.Singleline) &&
            !Regex.IsMatch(content, @"\bnavigat(e|ing)\b.*\bgoogle\b", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(run.BrowserSnapshot.CurrentUrl) ||
            !string.IsNullOrWhiteSpace(run.BrowserSnapshot.DriverSessionId))
        {
            return false;
        }

        var url = ExtractUrl(content) ?? ExtractUrl(run.UserPrompt) ?? ExtractKnownSiteUrl(content) ?? ExtractKnownSiteUrl(run.UserPrompt);
        if (url is null)
        {
            return false;
        }

        toolCall = new LlmToolCall
        {
            Index = 0,
            Id = Guid.NewGuid().ToString("N"),
            Name = "open_browser",
            ArgumentsJson = new JsonObject
            {
                ["url"] = url,
            }.ToJsonString(),
        };

        return true;
    }

    private static bool RequiresResumeNormalization(BrowserSessionSnapshot snapshot) =>
        snapshot.RestoreStatus is not (RestoreStatus.NotStarted or RestoreStatus.Closed)
        || !string.IsNullOrWhiteSpace(snapshot.CurrentUrl)
        || !string.IsNullOrWhiteSpace(snapshot.PageTitle)
        || !string.IsNullOrWhiteSpace(snapshot.DriverSessionId)
        || !string.IsNullOrWhiteSpace(snapshot.DriverServiceUrl)
        || snapshot.BrowserProcessId is not null
        || snapshot.Tabs.Count > 0;

    private static BrowserSessionSnapshot CloseSnapshot(BrowserSessionSnapshot snapshot) =>
        new()
        {
            TestRunId = snapshot.TestRunId,
            ProfilePath = snapshot.ProfilePath,
            CurrentUrl = null,
            PageTitle = null,
            DriverSessionId = null,
            DriverServiceUrl = null,
            BrowserProcessId = null,
            RestoreStatus = RestoreStatus.Closed,
            LastCapturedAtUtc = DateTime.UtcNow,
            Tabs = [],
        };

    private static bool TryInferNoArgumentToolCall(
        string content,
        IReadOnlyCollection<string> toolNames,
        string toolName,
        string phrase,
        out LlmToolCall toolCall)
    {
        toolCall = default!;
        if (!toolNames.Contains(toolName) || !content.Contains(phrase, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        toolCall = new LlmToolCall
        {
            Index = 0,
            Id = Guid.NewGuid().ToString("N"),
            Name = toolName,
            ArgumentsJson = "{}",
        };
        return true;
    }

    private static string? ExtractUrl(string content)
    {
        var match = Regex.Match(content, @"https?://[^\s""')]+", RegexOptions.IgnoreCase);
        return match.Success ? match.Value.TrimEnd('.', ',', ';') : null;
    }

    private static string? ExtractKnownSiteUrl(string content)
    {
        if (Regex.IsMatch(content, @"\bgoogle(?:\.com)?\b", RegexOptions.IgnoreCase))
        {
            return "https://www.google.com";
        }

        return null;
    }

    private static string RemoveSentenceContaining(string content, string token)
    {
        var sentences = Regex.Split(content, @"(?<=[.!?])\s+");
        var filtered = sentences
            .Where(sentence => !sentence.Contains(token, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return string.Join(' ', filtered).Trim();
    }

    private static string BuildEmptyTurnNotice(int stalledTurns) =>
        stalledTurns switch
        {
            1 => "Your last reply was empty while goals are still unresolved. Call exactly one tool on the next turn. Do not return an empty response.",
            2 => "You have produced multiple empty or no-tool turns. Stop narrating and emit exactly one structured tool call now.",
            _ => "Repeated empty or no-tool turns detected. If you cannot proceed, use goal evidence to fail the blocked goal instead of returning another empty reply.",
        };

    private static JsonNode CoerceTaggedValue(string rawValue)
    {
        if (bool.TryParse(rawValue, out var boolean))
        {
            return JsonValue.Create(boolean);
        }

        if (int.TryParse(rawValue, out var integer))
        {
            return JsonValue.Create(integer);
        }

        if (double.TryParse(rawValue, out var number))
        {
            return JsonValue.Create(number);
        }

        return JsonValue.Create(rawValue);
    }

    private static string FormatGoalChangedMessage(TimelineEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.MetadataJson))
        {
            return $"Goal update: {entry.Content}";
        }

        return $"Goal update: {entry.Content}\nMetadata: {entry.MetadataJson}";
    }

    private static JsonObject BuildToolResultMetadata(ToolExecutionResult result, int repeatedAttemptCount)
    {
        var metadata = new JsonObject
        {
            ["success"] = result.Success,
            ["summary"] = result.Summary,
            ["error"] = result.Error,
            ["hint"] = result.Hint,
            ["repeated_attempt_count"] = repeatedAttemptCount,
            ["data"] = result.Data?.DeepClone(),
            ["normalized_arguments"] = result.NormalizedArguments?.DeepClone(),
            ["expected_arguments"] = result.ExpectedArguments?.DeepClone(),
            ["example_arguments"] = result.ExampleArguments?.DeepClone(),
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

        builder.Append($"Error: {result.Error ?? result.Summary}. ");
        if (!string.IsNullOrWhiteSpace(result.Hint))
        {
            builder.Append($"Hint: {result.Hint}. ");
        }

        if (result.NormalizedArguments is not null)
        {
            builder.Append($"Normalized arguments: {result.NormalizedArguments.ToJsonString()}. ");
        }

        if (result.ExampleArguments is not null)
        {
            builder.Append($"Example arguments: {result.ExampleArguments.ToJsonString()}. ");
        }

        builder.Append("Next-step options: inspect page state, change the argument shape, try a different locator strategy, use a less brittle inspection tool, or fail the goal with evidence if the page blocks further progress.");
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
                     .Take(3)
                     .Reverse())
        {
            var metadata = ParseMetadata(entry.MetadataJson);
            outcomes.Add(new JsonObject
            {
                ["tool_name"] = entry.ToolName,
                ["summary"] = entry.Content,
                ["success"] = metadata?["success"]?.DeepClone(),
                ["error"] = metadata?["error"]?.DeepClone(),
                ["hint"] = metadata?["hint"]?.DeepClone(),
                ["repeated_attempt_count"] = metadata?["repeated_attempt_count"]?.DeepClone(),
                ["normalized_arguments"] = metadata?["normalized_arguments"]?.DeepClone(),
            });
        }

        return outcomes;
    }

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

    private static TestRunStatus InferRunStatusFromGoals(TestRun run)
    {
        if (run.Goals.Count == 0)
        {
            return TestRunStatus.Completed;
        }

        if (run.Goals.Any(goal => goal.Status == GoalStatus.Failed))
        {
            return TestRunStatus.Failed;
        }

        return run.Goals.All(goal => goal.Status == GoalStatus.Passed)
            ? TestRunStatus.Completed
            : TestRunStatus.Running;
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
}
