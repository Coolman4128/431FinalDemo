using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using BrowserTesting.Core.Abstractions;
using BrowserTesting.Core.Llm;
using BrowserTesting.Core.Models;

namespace BrowserTesting.Tests;

internal sealed class NullBrowserSessionManager : IBrowserSessionManager
{
    public Task<BrowserSessionSnapshot> OpenBrowserAsync(Guid testRunId, string? startUrl, string profilePath, bool headless, CancellationToken cancellationToken) =>
        Task.FromResult(new BrowserSessionSnapshot { TestRunId = testRunId, CurrentUrl = startUrl });

    public Task<BrowserSessionSnapshot?> GetSnapshotAsync(Guid testRunId, CancellationToken cancellationToken) =>
        Task.FromResult<BrowserSessionSnapshot?>(null);

    public Task<ToolExecutionResult> ExecuteBrowserToolAsync(Guid testRunId, string toolName, JsonObject arguments, BrowserSessionSnapshot? persistedSnapshot, bool headless, CancellationToken cancellationToken) =>
        Task.FromResult(ToolExecutionResult.Successful($"{toolName} executed."));

    public Task CloseBrowserAsync(Guid testRunId, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

internal sealed class FixedGoalService(IReadOnlyList<GoalItem> goals) : IGoalService
{
    public Task<GoalItem> CreateGoalAsync(Guid chatId, Guid runId, string title, string successCriteria, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<GoalItem?> UpdateGoalStatusAsync(Guid chatId, Guid runId, Guid goalId, GoalStatus status, string? note, string? evidence, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<GoalItem>> ListGoalsAsync(Guid runId, CancellationToken cancellationToken) =>
        Task.FromResult(goals);
}

internal sealed class NoOpSecretStore : ISecretStore
{
    public Task SaveSecretAsync(Guid chatId, string name, string value, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<string?> GetSecretAsync(Guid chatId, string name, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    public Task<IReadOnlyList<string>> ListSecretNamesAsync(Guid chatId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}

internal class NoOpChatRepository : IChatRepository
{
    public virtual Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public virtual Task<IReadOnlyList<ChatSessionSummary>> ListChatsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ChatSessionSummary>>([]);
    public virtual Task<ChatSession> CreateChatAsync(string? title, CancellationToken cancellationToken) => throw new NotSupportedException();
    public virtual Task<ChatSession?> GetChatAsync(Guid chatId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public virtual Task UpdateChatAsync(ChatSession chat, CancellationToken cancellationToken) => Task.CompletedTask;
    public virtual Task<TestRun> CreateRunAsync(Guid chatId, string userPrompt, CancellationToken cancellationToken) => throw new NotSupportedException();
    public virtual Task UpdateRunAsync(TestRun run, CancellationToken cancellationToken) => Task.CompletedTask;
    public virtual Task<IReadOnlyList<GoalItem>> ListGoalsAsync(Guid runId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GoalItem>>([]);
    public virtual Task<GoalItem> AddGoalAsync(GoalItem goal, CancellationToken cancellationToken) => Task.FromResult(goal);
    public virtual Task UpdateGoalAsync(GoalItem goal, CancellationToken cancellationToken) => Task.CompletedTask;
    public virtual Task<TimelineEntry> AddTimelineEntryAsync(TimelineEntry entry, CancellationToken cancellationToken) => Task.FromResult(entry);
    public virtual Task UpdateTimelineEntryAsync(TimelineEntry entry, CancellationToken cancellationToken) => Task.CompletedTask;
    public virtual Task<long> GetNextSequenceAsync(Guid chatId, CancellationToken cancellationToken) => Task.FromResult(0L);
    public virtual Task SaveBrowserSnapshotAsync(Guid runId, BrowserSessionSnapshot snapshot, CancellationToken cancellationToken) => Task.CompletedTask;
    public virtual Task SaveSecretAsync(Guid chatId, string name, string encryptedValue, CancellationToken cancellationToken) => Task.CompletedTask;
    public virtual Task<string?> GetSecretAsync(Guid chatId, string name, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    public virtual Task<IReadOnlyList<string>> ListSecretNamesAsync(Guid chatId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);
}

internal sealed class InMemoryChatRepository : NoOpChatRepository
{
    private readonly ConcurrentDictionary<Guid, ChatSession> chats = [];
    private long sequence;

    public Action<TestRun>? ConfigureCreatedRun { get; set; }
    public Action<ChatSession, TestRun>? AfterCreateRun { get; set; }

    public ChatSession AddChat(ChatSession chat)
    {
        chats[chat.Id] = chat;
        return chat;
    }

    public override Task<ChatSession?> GetChatAsync(Guid chatId, CancellationToken cancellationToken) =>
        Task.FromResult(chats.TryGetValue(chatId, out var chat) ? Clone(chat) : null);

    public override Task UpdateChatAsync(ChatSession chat, CancellationToken cancellationToken)
    {
        var existing = chats[chat.Id];
        existing.Title = chat.Title;
        existing.UpdatedAtUtc = chat.UpdatedAtUtc;
        return Task.CompletedTask;
    }

    public override Task<TestRun> CreateRunAsync(Guid chatId, string userPrompt, CancellationToken cancellationToken)
    {
        var run = new TestRun
        {
            Id = Guid.NewGuid(),
            ChatSessionId = chatId,
            UserPrompt = userPrompt,
            Status = TestRunStatus.Pending,
        };
        ConfigureCreatedRun?.Invoke(run);
        chats[chatId].Runs.Add(run);
        AfterCreateRun?.Invoke(chats[chatId], run);
        return Task.FromResult(Clone(run));
    }

    public override Task UpdateRunAsync(TestRun run, CancellationToken cancellationToken)
    {
        var chat = chats[run.ChatSessionId];
        var index = chat.Runs.FindIndex(candidate => candidate.Id == run.Id);
        if (index >= 0)
        {
            chat.Runs[index] = Clone(run);
        }

        return Task.CompletedTask;
    }

    public override Task<TimelineEntry> AddTimelineEntryAsync(TimelineEntry entry, CancellationToken cancellationToken)
    {
        var copy = Clone(entry);
        copy.Sequence = Interlocked.Increment(ref sequence);
        chats[entry.ChatSessionId].Timeline.Add(copy);
        return Task.FromResult(Clone(copy));
    }

    public override Task UpdateTimelineEntryAsync(TimelineEntry entry, CancellationToken cancellationToken)
    {
        var timeline = chats[entry.ChatSessionId].Timeline;
        var index = timeline.FindIndex(candidate => candidate.Id == entry.Id);
        if (index >= 0)
        {
            timeline[index] = Clone(entry);
        }

        return Task.CompletedTask;
    }

    private static ChatSession Clone(ChatSession chat) =>
        new()
        {
            Id = chat.Id,
            Title = chat.Title,
            CreatedAtUtc = chat.CreatedAtUtc,
            UpdatedAtUtc = chat.UpdatedAtUtc,
            Runs = chat.Runs.Select(Clone).ToList(),
            Timeline = chat.Timeline.Select(Clone).ToList(),
        };

    private static TestRun Clone(TestRun run) =>
        new()
        {
            Id = run.Id,
            ChatSessionId = run.ChatSessionId,
            UserPrompt = run.UserPrompt,
            Status = run.Status,
            FailureReason = run.FailureReason,
            CreatedAtUtc = run.CreatedAtUtc,
            UpdatedAtUtc = run.UpdatedAtUtc,
            CompletedAtUtc = run.CompletedAtUtc,
            BrowserSnapshot = run.BrowserSnapshot,
            Goals = run.Goals.Select(Clone).ToList(),
        };

    private static GoalItem Clone(GoalItem goal) =>
        new()
        {
            Id = goal.Id,
            TestRunId = goal.TestRunId,
            Title = goal.Title,
            SuccessCriteria = goal.SuccessCriteria,
            Status = goal.Status,
            Note = goal.Note,
            Evidence = goal.Evidence,
            CreatedAtUtc = goal.CreatedAtUtc,
            UpdatedAtUtc = goal.UpdatedAtUtc,
            CompletedAtUtc = goal.CompletedAtUtc,
        };

    private static TimelineEntry Clone(TimelineEntry entry) =>
        new()
        {
            Id = entry.Id,
            ChatSessionId = entry.ChatSessionId,
            TestRunId = entry.TestRunId,
            Sequence = entry.Sequence,
            Kind = entry.Kind,
            Role = entry.Role,
            Content = entry.Content,
            ToolCallId = entry.ToolCallId,
            ToolName = entry.ToolName,
            MetadataJson = entry.MetadataJson,
            CreatedAtUtc = entry.CreatedAtUtc,
        };
}
