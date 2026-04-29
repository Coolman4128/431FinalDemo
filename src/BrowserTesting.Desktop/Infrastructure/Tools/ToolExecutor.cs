using System.Text.Json;
using System.Text.Json.Nodes;
using BrowserTesting.Core.Llm;
using BrowserTesting.Core.Models;
using BrowserTesting.Infrastructure.Browser;
using BrowserTesting.Infrastructure.Persistence;
using BrowserTesting.Infrastructure.Secrets;

namespace BrowserTesting.Infrastructure.Tools;

public sealed class ToolExecutor(
    BrowserSessionManager browserSessionManager,
    SqliteChatRepository repository,
    DpapiSecretStore secretStore,
    ToolRegistry toolRegistry)
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

    private readonly IReadOnlyDictionary<string, LlmToolDefinition> definitionsByName =
        toolRegistry.GetToolDefinitions().ToDictionary(definition => definition.Name, StringComparer.Ordinal);

    public async Task<ToolExecutionResult> ExecuteAsync(ToolInvocationContext context, string toolName, JsonObject arguments, CancellationToken cancellationToken)
    {
        definitionsByName.TryGetValue(toolName, out var definition);
        var normalizedArguments = NormalizeArguments(toolName, definition, arguments);
        if (definition is not null)
        {
            var validation = ToolArgumentValidator.Validate(definition, normalizedArguments);
            if (validation is not null)
            {
                return ToolExecutionResult.Failed(
                    validation.Summary,
                    validation.Error,
                    hint: validation.Hint?.Message,
                    normalizedArguments: validation.Hint?.NormalizedArguments,
                    expectedArguments: validation.ExpectedArguments,
                    exampleArguments: validation.ExampleArguments);
            }
        }

        ToolExecutionResult result = toolName switch
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

        await secretStore.SaveSecretAsync(context.ChatSessionId, name, value, cancellationToken);
        return ToolExecutionResult.Successful("Secret saved.", new JsonObject { ["name"] = name });
    }

    private async Task<ToolExecutionResult> GetSecretAsync(ToolInvocationContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        var name = GetString(arguments, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return ToolExecutionResult.Failed("Secret name is required.");
        }

        var value = await secretStore.GetSecretAsync(context.ChatSessionId, name, cancellationToken);
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
        var names = await secretStore.ListSecretNamesAsync(context.ChatSessionId, cancellationToken);
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
                if (property.Value is not JsonObject propertySchema)
                {
                    continue;
                }

                if (objectValue[property.Key] is null)
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
}
