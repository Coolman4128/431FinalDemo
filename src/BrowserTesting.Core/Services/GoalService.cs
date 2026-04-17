using System.Text.Json;
using BrowserTesting.Core.Abstractions;
using BrowserTesting.Core.Models;

namespace BrowserTesting.Core.Services;

public sealed class GoalService(IChatRepository repository) : IGoalService
{
    public async Task<GoalItem> CreateGoalAsync(Guid chatId, Guid runId, string title, string successCriteria, CancellationToken cancellationToken)
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

    public async Task<IReadOnlyList<GoalItem>> ListGoalsAsync(Guid runId, CancellationToken cancellationToken) =>
        await repository.ListGoalsAsync(runId, cancellationToken);

    public async Task<GoalItem?> UpdateGoalStatusAsync(
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
}
