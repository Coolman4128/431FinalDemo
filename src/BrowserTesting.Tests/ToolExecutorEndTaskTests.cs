using System.Text.Json.Nodes;
using BrowserTesting.Core.Abstractions;
using BrowserTesting.Core.Models;
using BrowserTesting.Infrastructure.Tools;
using Xunit;

namespace BrowserTesting.Tests;

public sealed class ToolExecutorEndTaskTests
{
    [Fact]
    public async Task EndTaskFailsWithExactUnresolvedGoalIds()
    {
        var pendingGoal = Goal(GoalStatus.Pending);
        var runningGoal = Goal(GoalStatus.Running);
        var executor = CreateExecutor([pendingGoal, runningGoal]);

        var result = await executor.ExecuteAsync(Context(), "end_task", EndTaskArgs(), CancellationToken.None);

        Assert.False(result.Success);
        var unresolved = result.Data!["unresolved_goals"]!.AsArray();
        Assert.Equal(
            [pendingGoal.Id.ToString(), runningGoal.Id.ToString()],
            unresolved.Select(node => node!["id"]!.GetValue<string>()).ToArray());
    }

    [Fact]
    public async Task EndTaskSucceedsOnlyAfterAllGoalsAreTerminal()
    {
        var executor = CreateExecutor([Goal(GoalStatus.Passed), Goal(GoalStatus.Passed)]);

        var result = await executor.ExecuteAsync(Context(), "end_task", EndTaskArgs(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("completed", result.Data!["outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task EndTaskReportsFailedOutcomeWhenAnyGoalFailed()
    {
        var executor = CreateExecutor([Goal(GoalStatus.Passed), Goal(GoalStatus.Failed)]);

        var result = await executor.ExecuteAsync(Context(), "end_task", EndTaskArgs("completed"), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("failed", result.Data!["outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task CreateGoalReturnsExistingGoalForSemanticDuplicate()
    {
        var existing = new GoalItem
        {
            Id = Guid.NewGuid(),
            TestRunId = Guid.NewGuid(),
            Title = "Logging in",
            SuccessCriteria = "Log in to saucedemo with standard_user / secret_sauce and reach the inventory page.",
            Status = GoalStatus.Pending,
        };
        var goalService = new MutableGoalService([existing]);
        var executor = new ToolExecutor(
            new NullBrowserSessionManager(),
            goalService,
            new NoOpChatRepository(),
            new NoOpSecretStore(),
            new ToolRegistry());

        var result = await executor.ExecuteAsync(Context(), "create_goal", new JsonObject
        {
            ["title"] = "Log in to Sauce Demo",
            ["success_criteria"] = "Log in with standard_user / secret_sauce and reach the inventory page.",
        }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("Goal already exists.", result.Summary);
        Assert.Equal(existing.Id.ToString(), result.Data!["id"]!.GetValue<string>());
        Assert.Equal(0, goalService.CreatedCount);
    }

    private static ToolExecutor CreateExecutor(IReadOnlyList<GoalItem> goals) =>
        new(
            new NullBrowserSessionManager(),
            new FixedGoalService(goals),
            new NoOpChatRepository(),
            new NoOpSecretStore(),
            new ToolRegistry());

    private static ToolInvocationContext Context() =>
        new()
        {
            ChatSessionId = Guid.NewGuid(),
            TestRunId = Guid.NewGuid(),
            LaunchHeadless = true,
        };

    private static JsonObject EndTaskArgs(string outcome = "completed") =>
        new()
        {
            ["outcome"] = outcome,
            ["summary"] = "All goals resolved.",
            ["evidence"] = "Goal evidence recorded.",
            ["remaining_work"] = "none",
        };

    private static GoalItem Goal(GoalStatus status) =>
        new()
        {
            Id = Guid.NewGuid(),
            TestRunId = Guid.NewGuid(),
            Title = $"{status} goal",
            SuccessCriteria = "Criterion",
            Status = status,
            Evidence = status is GoalStatus.Passed or GoalStatus.Failed ? "Evidence" : null,
        };

    private sealed class MutableGoalService(IReadOnlyList<GoalItem> initialGoals) : IGoalService
    {
        private readonly List<GoalItem> goals = [..initialGoals];

        public int CreatedCount { get; private set; }

        public Task<GoalItem> CreateGoalAsync(Guid chatId, Guid runId, string title, string successCriteria, CancellationToken cancellationToken)
        {
            CreatedCount++;
            var goal = new GoalItem
            {
                Id = Guid.NewGuid(),
                TestRunId = runId,
                Title = title,
                SuccessCriteria = successCriteria,
            };
            goals.Add(goal);
            return Task.FromResult(goal);
        }

        public Task<GoalItem?> UpdateGoalStatusAsync(Guid chatId, Guid runId, Guid goalId, GoalStatus status, string? note, string? evidence, CancellationToken cancellationToken) =>
            Task.FromResult<GoalItem?>(null);

        public Task<IReadOnlyList<GoalItem>> ListGoalsAsync(Guid runId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GoalItem>>(goals);
    }
}
