using BrowserTesting.Core.Models;

namespace BrowserTesting.Core.Orchestration;

public abstract record OrchestratorUpdate;

public sealed record ChatSummariesUpdated(IReadOnlyList<ChatSessionSummary> Chats) : OrchestratorUpdate;

public sealed record ChatLoaded(ChatSession Chat) : OrchestratorUpdate;

public sealed record TimelineEntryUpserted(TimelineEntry Entry) : OrchestratorUpdate;

public sealed record RunUpdated(TestRun Run) : OrchestratorUpdate;

public sealed record BrowserSnapshotUpdated(Guid RunId, BrowserSessionSnapshot Snapshot) : OrchestratorUpdate;

public sealed record GoalsUpdated(Guid RunId, IReadOnlyList<GoalItem> Goals) : OrchestratorUpdate;

public sealed record OrchestrationError(string Message) : OrchestratorUpdate;
