using BrowserTesting.Core.Models;
using Avalonia.Media;

namespace BrowserTesting.Desktop.ViewModels;

public sealed class ChatListItemViewModel : ObservableObject
{
    private string title = string.Empty;
    private DateTime updatedAtUtc;
    private int activeRuns;

    public Guid Id { get; init; }
    public string Title { get => title; set => SetProperty(ref title, value); }
    public DateTime UpdatedAtUtc
    {
        get => updatedAtUtc;
        set
        {
            if (SetProperty(ref updatedAtUtc, value))
            {
                RaisePropertyChanged(nameof(Subtitle));
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
                RaisePropertyChanged(nameof(Subtitle));
            }
        }
    }

    public string Subtitle => $"{UpdatedAtUtc.ToLocalTime():g}  |  {ActiveRuns} active";
}

public abstract class TimelineItemViewModel : ObservableObject
{
    protected TimelineItemViewModel(TimelineEntry entry)
    {
        EntryId = entry.Id;
        CreatedAtUtc = entry.CreatedAtUtc;
    }

    public Guid EntryId { get; }
    public DateTime CreatedAtUtc { get; }
    public string Timestamp => CreatedAtUtc.ToLocalTime().ToString("t");
}

public sealed class UserTimelineItemViewModel(TimelineEntry entry) : TimelineItemViewModel(entry)
{
    private string content = entry.Content;
    public string Content { get => content; set => SetProperty(ref content, value); }
}

public sealed class AssistantTimelineItemViewModel(TimelineEntry entry) : TimelineItemViewModel(entry)
{
    private string content = entry.Content;
    public string Content { get => content; set => SetProperty(ref content, value); }
}

public sealed class ToolStartedTimelineItemViewModel(TimelineEntry entry) : TimelineItemViewModel(entry)
{
    public string ToolName { get; } = entry.ToolName ?? "tool";
    private string summary = entry.Content;
    public string Summary { get => summary; set => SetProperty(ref summary, value); }
    public string ArgumentsPreview { get; } = entry.MetadataJson ?? "{}";
}

public sealed class ToolFinishedTimelineItemViewModel(TimelineEntry entry) : TimelineItemViewModel(entry)
{
    public string ToolName { get; } = entry.ToolName ?? "tool";
    private string summary = entry.Content;
    public string Summary { get => summary; set => SetProperty(ref summary, value); }
    private bool success = !(entry.MetadataJson?.Contains(@"""Success"":false", StringComparison.OrdinalIgnoreCase) ?? false)
        && !(entry.MetadataJson?.Contains(@"""success"":false", StringComparison.OrdinalIgnoreCase) ?? false);
    public bool Success { get => success; set => SetProperty(ref success, value); }
    public IBrush StatusBrush => Success ? Brushes.MediumSeaGreen : Brushes.IndianRed;
    public string ResultJson { get; } = entry.MetadataJson ?? "{}";
}

public sealed class GoalChangedTimelineItemViewModel(TimelineEntry entry) : TimelineItemViewModel(entry)
{
    private string content = entry.Content;
    public string Content { get => content; set => SetProperty(ref content, value); }
}

public sealed class SystemTimelineItemViewModel(TimelineEntry entry) : TimelineItemViewModel(entry)
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
