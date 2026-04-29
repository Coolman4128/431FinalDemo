using System.Text.Json.Nodes;
using BrowserTesting.Desktop.Models;
using Avalonia.Media;

namespace BrowserTesting.Desktop.ViewModels;

public sealed class ChatListItemViewModel : ObservableObject
{
    private string title = string.Empty;
    private string subtitle = string.Empty;
    private DateTime updatedAtUtc;
    private int activeRuns;
    private bool isSelected;

    public Guid? Id { get; init; }
    public bool IsDraft { get; init; }

    public string Title
    {
        get => title;
        set => SetProperty(ref title, value);
    }

    public string Subtitle
    {
        get => subtitle;
        private set => SetProperty(ref subtitle, value);
    }

    public DateTime UpdatedAtUtc
    {
        get => updatedAtUtc;
        set
        {
            if (SetProperty(ref updatedAtUtc, value))
            {
                RefreshDerivedState();
            }
        }
    }

    public int ActiveRuns
    {
        get => activeRuns;
        set
        {
            if (SetProperty(ref activeRuns, value))
            {
                RefreshDerivedState();
            }
        }
    }

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (SetProperty(ref isSelected, value))
            {
                RaisePropertyChanged(nameof(CardBackground));
                RaisePropertyChanged(nameof(CardBorderBrush));
                RaisePropertyChanged(nameof(TitleBrush));
                RaisePropertyChanged(nameof(SubtitleBrush));
            }
        }
    }

    public bool ShowSubtitle => !IsDraft && !string.IsNullOrWhiteSpace(Subtitle);
    public bool HasActiveRuns => !IsDraft && ActiveRuns > 0;
    public string ActiveRunsText => ActiveRuns == 1 ? "1 active" : $"{ActiveRuns} active";
    public string CardBackground => IsSelected ? "#183049" : IsDraft ? "#13283A" : "#102030";
    public string CardBorderBrush => IsSelected ? "#4D7AA6" : IsDraft ? "#31506A" : "#223648";
    public string TitleBrush => IsSelected ? "#F2F8FF" : "#EAF2FB";
    public string SubtitleBrush => IsSelected ? "#C6D8EA" : "#88A1B8";

    public static ChatListItemViewModel CreateDraft() =>
        new()
        {
            IsDraft = true,
            Title = "New Chat",
        };

    private void RefreshDerivedState()
    {
        if (IsDraft)
        {
            Subtitle = string.Empty;
        }
        else
        {
            Subtitle = UpdatedAtUtc == default
                ? string.Empty
                : UpdatedAtUtc.ToLocalTime().ToString("MMM d, h:mm tt");
        }

        RaisePropertyChanged(nameof(ShowSubtitle));
        RaisePropertyChanged(nameof(HasActiveRuns));
        RaisePropertyChanged(nameof(ActiveRunsText));
    }
}

public abstract class TimelineItemViewModel(string itemKey, DateTime createdAtUtc) : ObservableObject
{
    public string ItemKey { get; } = itemKey;
    public DateTime CreatedAtUtc { get; } = createdAtUtc;
    public string Timestamp => CreatedAtUtc.ToLocalTime().ToString("t");
}

public sealed class UserTimelineItemViewModel(TimelineEntry entry) : TimelineItemViewModel(entry.Id.ToString("N"), entry.CreatedAtUtc)
{
    private string content = entry.Content;
    public string Content { get => content; set => SetProperty(ref content, value); }
}

public sealed class AssistantTimelineItemViewModel(TimelineEntry entry) : TimelineItemViewModel(entry.Id.ToString("N"), entry.CreatedAtUtc)
{
    private string content = entry.Content;
    public string Content { get => content; set => SetProperty(ref content, value); }
}

public sealed class ToolTimelineItemViewModel(TimelineEntry entry)
    : TimelineItemViewModel(entry.ToolCallId ?? entry.Id.ToString("N"), entry.CreatedAtUtc)
{
    private string toolName = entry.ToolName ?? "tool";
    private bool isRunning = entry.Kind == TimelineItemKind.ToolCallStarted;
    private bool success = entry.Kind != TimelineItemKind.ToolCallFinished || ReadSuccess(entry.MetadataJson);
    private string? finalSummary = ReadEndTaskValue(entry.ToolName, entry.MetadataJson, "summary");
    private string? finalTestResults = ReadEndTaskValue(entry.ToolName, entry.MetadataJson, "test_results");
    private string? finalRemainingWork = ReadEndTaskValue(entry.ToolName, entry.MetadataJson, "remaining_work");

    public string ToolName
    {
        get => toolName;
        private set
        {
            if (SetProperty(ref toolName, value))
            {
                RaisePropertyChanged(nameof(DisplayTitle));
            }
        }
    }

    public string DisplayTitle => HasFinalReport ? "Run Summary" : ToolName;

    public bool IsRunning
    {
        get => isRunning;
        private set
        {
            if (SetProperty(ref isRunning, value))
            {
                RaisePropertyChanged(nameof(IsCompleted));
                RaisePropertyChanged(nameof(StatusText));
                RaisePropertyChanged(nameof(StatusBrush));
            }
        }
    }

    public bool IsCompleted => !IsRunning;

    public bool Success
    {
        get => success;
        private set
        {
            if (SetProperty(ref success, value))
            {
                RaisePropertyChanged(nameof(StatusText));
                RaisePropertyChanged(nameof(StatusBrush));
            }
        }
    }

    public string StatusText => IsRunning ? "Running" : Success ? "Success" : "Failed";

    public IBrush StatusBrush => IsRunning
        ? Brushes.Goldenrod
        : Success
            ? Brushes.MediumSeaGreen
            : Brushes.IndianRed;

    public string? FinalSummary
    {
        get => finalSummary;
        private set
        {
            if (SetProperty(ref finalSummary, value))
            {
                RaiseFinalReportPropertiesChanged();
            }
        }
    }

    public string? FinalTestResults
    {
        get => finalTestResults;
        private set
        {
            if (SetProperty(ref finalTestResults, value))
            {
                RaiseFinalReportPropertiesChanged();
            }
        }
    }

    public string? FinalRemainingWork
    {
        get => finalRemainingWork;
        private set
        {
            if (SetProperty(ref finalRemainingWork, value))
            {
                RaiseFinalReportPropertiesChanged();
            }
        }
    }

    public bool HasFinalSummary => !string.IsNullOrWhiteSpace(FinalSummary);
    public bool HasFinalTestResults => !string.IsNullOrWhiteSpace(FinalTestResults);
    public bool HasFinalRemainingWork => !string.IsNullOrWhiteSpace(FinalRemainingWork);
    public bool HasFinalReport => HasFinalSummary || HasFinalTestResults || HasFinalRemainingWork;

    public void ApplyFinishedEntry(TimelineEntry entry)
    {
        ToolName = entry.ToolName ?? ToolName;
        Success = ReadSuccess(entry.MetadataJson);
        FinalSummary = ReadEndTaskValue(entry.ToolName, entry.MetadataJson, "summary");
        FinalTestResults = ReadEndTaskValue(entry.ToolName, entry.MetadataJson, "test_results");
        FinalRemainingWork = ReadEndTaskValue(entry.ToolName, entry.MetadataJson, "remaining_work");
        IsRunning = false;
    }

    private void RaiseFinalReportPropertiesChanged()
    {
        RaisePropertyChanged(nameof(HasFinalSummary));
        RaisePropertyChanged(nameof(HasFinalTestResults));
        RaisePropertyChanged(nameof(HasFinalRemainingWork));
        RaisePropertyChanged(nameof(HasFinalReport));
        RaisePropertyChanged(nameof(DisplayTitle));
    }

    private static bool ReadSuccess(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return true;
        }

        try
        {
            var metadata = JsonNode.Parse(metadataJson)?.AsObject();
            return metadata?["success"]?.GetValue<bool>()
                ?? metadata?["Success"]?.GetValue<bool>()
                ?? true;
        }
        catch
        {
            return !metadataJson.Contains(@"""success"":false", StringComparison.OrdinalIgnoreCase)
                && !metadataJson.Contains(@"""Success"":false", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string? ReadEndTaskValue(string? toolName, string? metadataJson, string propertyName)
    {
        if (!string.Equals(toolName, "end_task", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(metadataJson))
        {
            return null;
        }

        try
        {
            var metadata = JsonNode.Parse(metadataJson)?.AsObject();
            if (metadata?["data"] is not JsonObject data ||
                data[propertyName] is not JsonValue value ||
                !value.TryGetValue<string>(out var text) ||
                string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            return text.Trim();
        }
        catch
        {
            return null;
        }
    }
}

public sealed class GoalChangedTimelineItemViewModel(TimelineEntry entry) : TimelineItemViewModel(entry.Id.ToString("N"), entry.CreatedAtUtc)
{
    private string content = entry.Content;
    public string Content { get => content; set => SetProperty(ref content, value); }
}

public sealed class SystemTimelineItemViewModel(TimelineEntry entry) : TimelineItemViewModel(entry.Id.ToString("N"), entry.CreatedAtUtc)
{
    private string content = entry.Content;
    public string Content { get => content; set => SetProperty(ref content, value); }
}

public sealed class GoalItemViewModel : ObservableObject
{
    private GoalStatus status;
    private string? note;
    private string? evidence;

    public Guid Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string SuccessCriteria { get; init; } = string.Empty;

    public GoalStatus Status
    {
        get => status;
        set
        {
            if (SetProperty(ref status, value))
            {
                RaisePropertyChanged(nameof(StatusText));
                RaisePropertyChanged(nameof(StatusBrush));
            }
        }
    }

    public string? Note { get => note; set => SetProperty(ref note, value); }
    public string? Evidence { get => evidence; set => SetProperty(ref evidence, value); }

    public string StatusText => Status.ToString();

    public IBrush StatusBrush => Status switch
    {
        GoalStatus.Passed => Brushes.MediumSeaGreen,
        GoalStatus.Failed => Brushes.IndianRed,
        GoalStatus.Running => Brushes.Goldenrod,
        _ => Brushes.SlateGray,
    };
}
