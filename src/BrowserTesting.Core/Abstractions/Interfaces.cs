using System.Text.Json.Nodes;
using BrowserTesting.Core.Llm;
using BrowserTesting.Core.Models;
using BrowserTesting.Core.Orchestration;

namespace BrowserTesting.Core.Abstractions;

public interface ILlmClient
{
    IAsyncEnumerable<LlmStreamEvent> StreamCompletionAsync(LlmRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> ListModelsAsync(LlmConnectionSettings connection, CancellationToken cancellationToken);
}

public interface IToolRegistry
{
    IReadOnlyList<LlmToolDefinition> GetToolDefinitions();
}

public interface IToolExecutor
{
    Task<ToolExecutionResult> ExecuteAsync(ToolInvocationContext context, string toolName, JsonObject arguments, CancellationToken cancellationToken);
}

public interface IBrowserSessionManager
{
    Task<BrowserSessionSnapshot> OpenBrowserAsync(Guid testRunId, string? startUrl, string profilePath, bool headless, CancellationToken cancellationToken);
    Task<BrowserSessionSnapshot?> GetSnapshotAsync(Guid testRunId, CancellationToken cancellationToken);
    Task<ToolExecutionResult> ExecuteBrowserToolAsync(Guid testRunId, string toolName, JsonObject arguments, BrowserSessionSnapshot? persistedSnapshot, bool headless, CancellationToken cancellationToken);
    Task CloseBrowserAsync(Guid testRunId, CancellationToken cancellationToken);
}

public interface IGoalService
{
    Task<GoalItem> CreateGoalAsync(Guid chatId, Guid runId, string title, string successCriteria, CancellationToken cancellationToken);
    Task<GoalItem?> UpdateGoalStatusAsync(Guid chatId, Guid runId, Guid goalId, GoalStatus status, string? note, string? evidence, CancellationToken cancellationToken);
    Task<IReadOnlyList<GoalItem>> ListGoalsAsync(Guid runId, CancellationToken cancellationToken);
}

public interface IChatRepository
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ChatSessionSummary>> ListChatsAsync(CancellationToken cancellationToken);
    Task<ChatSession> CreateChatAsync(string? title, CancellationToken cancellationToken);
    Task<ChatSession?> GetChatAsync(Guid chatId, CancellationToken cancellationToken);
    Task UpdateChatAsync(ChatSession chat, CancellationToken cancellationToken);
    Task<TestRun> CreateRunAsync(Guid chatId, string userPrompt, CancellationToken cancellationToken);
    Task UpdateRunAsync(TestRun run, CancellationToken cancellationToken);
    Task<IReadOnlyList<GoalItem>> ListGoalsAsync(Guid runId, CancellationToken cancellationToken);
    Task<GoalItem> AddGoalAsync(GoalItem goal, CancellationToken cancellationToken);
    Task UpdateGoalAsync(GoalItem goal, CancellationToken cancellationToken);
    Task<TimelineEntry> AddTimelineEntryAsync(TimelineEntry entry, CancellationToken cancellationToken);
    Task UpdateTimelineEntryAsync(TimelineEntry entry, CancellationToken cancellationToken);
    Task<long> GetNextSequenceAsync(Guid chatId, CancellationToken cancellationToken);
    Task SaveBrowserSnapshotAsync(Guid runId, BrowserSessionSnapshot snapshot, CancellationToken cancellationToken);
    Task SaveSecretAsync(Guid chatId, string name, string encryptedValue, CancellationToken cancellationToken);
    Task<string?> GetSecretAsync(Guid chatId, string name, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> ListSecretNamesAsync(Guid chatId, CancellationToken cancellationToken);
}

public interface ISecretStore
{
    Task SaveSecretAsync(Guid chatId, string name, string value, CancellationToken cancellationToken);
    Task<string?> GetSecretAsync(Guid chatId, string name, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> ListSecretNamesAsync(Guid chatId, CancellationToken cancellationToken);
}

public interface IAppSettingsStore
{
    Task LoadAsync(AppSettings settings, CancellationToken cancellationToken);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken);
}

public interface IChatOrchestrator
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ChatSessionSummary>> ListChatsAsync(CancellationToken cancellationToken);
    Task<ChatSession> CreateChatAsync(string? title, CancellationToken cancellationToken);
    Task<ChatSession?> LoadChatAsync(Guid chatId, Action<OrchestratorUpdate>? onUpdate, CancellationToken cancellationToken);
    Task<BrowserSessionSnapshot?> CloseBrowserAsync(Guid runId, Action<OrchestratorUpdate>? onUpdate, CancellationToken cancellationToken);
    Task<BrowserSessionSnapshot?> RefreshBrowserSnapshotAsync(Guid runId, Action<OrchestratorUpdate>? onUpdate, CancellationToken cancellationToken);
    Task<TestRun> RunPromptAsync(Guid chatId, string prompt, Action<OrchestratorUpdate> onUpdate, CancellationToken cancellationToken);
}
