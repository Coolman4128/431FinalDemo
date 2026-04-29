using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BrowserTesting.Core.Models;

namespace BrowserTesting.Core.Services;

public static class ChatTranscriptFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static string FormatForSelection(ChatSession chat)
    {
        ArgumentNullException.ThrowIfNull(chat);

        var builder = new StringBuilder();
        builder.AppendLine($"Chat: {chat.Title}");
        builder.AppendLine($"Chat Id: {chat.Id}");
        builder.AppendLine();

        foreach (var entry in chat.Timeline.OrderBy(item => item.Sequence))
        {
            builder.Append('[')
                .Append(entry.CreatedAtUtc.ToLocalTime().ToString("g"))
                .Append("] ")
                .AppendLine(BuildEntryHeading(entry));

            if (!string.IsNullOrWhiteSpace(entry.Content))
            {
                builder.AppendLine(entry.Content.TrimEnd());
            }

            if (!string.IsNullOrWhiteSpace(entry.ToolName))
            {
                builder.Append("Tool: ").AppendLine(entry.ToolName);
            }

            if (!string.IsNullOrWhiteSpace(entry.MetadataJson))
            {
                builder.AppendLine("Metadata:");
                AppendMultiline(builder, TryFormatJson(entry.MetadataJson), "  ");
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    public static string FormatForExport(ChatSession chat)
    {
        ArgumentNullException.ThrowIfNull(chat);

        var builder = new StringBuilder();
        builder.AppendLine("Chat Export");
        builder.AppendLine("===========");
        builder.Append("Exported At (UTC): ").AppendLine(DateTime.UtcNow.ToString("O"));
        builder.Append("Chat Id: ").AppendLine(chat.Id.ToString());
        builder.Append("Title: ").AppendLine(chat.Title);
        builder.Append("Created At (UTC): ").AppendLine(chat.CreatedAtUtc.ToString("O"));
        builder.Append("Updated At (UTC): ").AppendLine(chat.UpdatedAtUtc.ToString("O"));
        builder.AppendLine();

        builder.AppendLine("Runs");
        builder.AppendLine("----");
        if (chat.Runs.Count == 0)
        {
            builder.AppendLine("(none)");
        }
        else
        {
            foreach (var run in chat.Runs.OrderBy(item => item.CreatedAtUtc))
            {
                builder.AppendLine($"Run {run.Id}");
                builder.Append("Prompt: ").AppendLine(run.UserPrompt);
                builder.Append("Status: ").AppendLine(run.Status.ToString());
                builder.Append("Failure Reason: ").AppendLine(run.FailureReason ?? "(none)");
                builder.Append("Created At (UTC): ").AppendLine(run.CreatedAtUtc.ToString("O"));
                builder.Append("Updated At (UTC): ").AppendLine(run.UpdatedAtUtc.ToString("O"));
                builder.Append("Completed At (UTC): ").AppendLine(run.CompletedAtUtc?.ToString("O") ?? "(not completed)");
                builder.AppendLine("Browser Snapshot:");
                AppendMultiline(builder, JsonSerializer.Serialize(run.BrowserSnapshot, JsonOptions), "  ");
                builder.AppendLine("Goals:");

                if (run.Goals.Count == 0)
                {
                    builder.AppendLine("  (none)");
                }
                else
                {
                    foreach (var goal in run.Goals.OrderBy(item => item.CreatedAtUtc))
                    {
                        builder.Append("  - Goal Id: ").AppendLine(goal.Id.ToString());
                        builder.Append("    Title: ").AppendLine(goal.Title);
                        builder.Append("    Success Criteria: ").AppendLine(goal.SuccessCriteria);
                        builder.Append("    Status: ").AppendLine(goal.Status.ToString());
                        builder.Append("    Note: ").AppendLine(goal.Note ?? "(none)");
                        builder.Append("    Evidence: ").AppendLine(goal.Evidence ?? "(none)");
                        builder.Append("    Created At (UTC): ").AppendLine(goal.CreatedAtUtc.ToString("O"));
                        builder.Append("    Updated At (UTC): ").AppendLine(goal.UpdatedAtUtc.ToString("O"));
                        builder.Append("    Completed At (UTC): ").AppendLine(goal.CompletedAtUtc?.ToString("O") ?? "(not completed)");
                    }
                }

                builder.AppendLine();
            }
        }

        builder.AppendLine("Timeline");
        builder.AppendLine("--------");
        if (chat.Timeline.Count == 0)
        {
            builder.AppendLine("(none)");
        }
        else
        {
            foreach (var entry in chat.Timeline.OrderBy(item => item.Sequence))
            {
                builder.Append('[').Append(entry.Sequence).AppendLine("]");
                builder.Append("Entry Id: ").AppendLine(entry.Id.ToString());
                builder.Append("Run Id: ").AppendLine(entry.TestRunId?.ToString() ?? "(none)");
                builder.Append("Created At (UTC): ").AppendLine(entry.CreatedAtUtc.ToString("O"));
                builder.Append("Kind: ").AppendLine(entry.Kind.ToString());
                builder.Append("Role: ").AppendLine(entry.Role);
                builder.Append("Tool Call Id: ").AppendLine(entry.ToolCallId ?? "(none)");
                builder.Append("Tool Name: ").AppendLine(entry.ToolName ?? "(none)");
                builder.AppendLine("Content:");
                AppendMultiline(builder, entry.Content, "  ");
                builder.AppendLine("Metadata:");
                AppendMultiline(builder, string.IsNullOrWhiteSpace(entry.MetadataJson) ? "(none)" : TryFormatJson(entry.MetadataJson), "  ");
                builder.AppendLine();
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildEntryHeading(TimelineEntry entry) =>
        entry.Kind switch
        {
            TimelineItemKind.UserMessage => "You",
            TimelineItemKind.AssistantMessage => "Assistant",
            TimelineItemKind.ToolCallStarted => $"Tool Call Started: {entry.ToolName ?? "tool"}",
            TimelineItemKind.ToolCallFinished => $"Tool Response: {entry.ToolName ?? "tool"}",
            TimelineItemKind.GoalChanged => "Goal Update",
            TimelineItemKind.SystemNotice => "System",
            _ => entry.Kind.ToString(),
        };

    private static void AppendMultiline(StringBuilder builder, string? value, string indent)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "(empty)" : value.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
        foreach (var line in text.Split('\n'))
        {
            builder.Append(indent).AppendLine(line);
        }
    }

    private static string TryFormatJson(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return rawJson;
        }

        try
        {
            return JsonNode.Parse(rawJson)?.ToJsonString(JsonOptions) ?? rawJson;
        }
        catch
        {
            return rawJson;
        }
    }
}
