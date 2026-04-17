using System.Collections.ObjectModel;
using System.Linq;
using BrowserTesting.Core.Abstractions;
using BrowserTesting.Core.Models;
using BrowserTesting.Core.Orchestration;
using BrowserTesting.Core.Services;
using BrowserTesting.Desktop.Services;
using Avalonia.Threading;

namespace BrowserTesting.Desktop.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly IChatOrchestrator orchestrator;
    private readonly ITextFileSaveService textFileSaveService;
    private readonly DispatcherTimer browserStatusTimer;
    private ChatListItemViewModel? selectedChat;
    private ChatSession? currentChat;
    private string composerText = string.Empty;
    private string browserStatus = "No browser";
    private string browserUrl = "n/a";
    private string browserTitle = "n/a";
    private string restoreStatus = "Not started";
    private string selectedRunTitle = "No run selected";
    private string statusText = "Ready";
    private Guid? selectedChatId;
    private Guid? selectedRunId;
    private int browserStatusRefreshInProgress;
    private Guid? loadingChatId;
    private bool suppressSelectedChatLoad;
    private bool isSelectionModeEnabled;
    private string selectionTranscriptText = string.Empty;

    public MainWindowViewModel(
        IChatOrchestrator orchestrator,
        ITextFileSaveService textFileSaveService,
        ILlmSettingsService llmSettingsService)
    {
        this.orchestrator = orchestrator;
        this.textFileSaveService = textFileSaveService;
        Settings = new LlmSettingsViewModel(llmSettingsService);
        Settings.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(LlmSettingsViewModel.IsOpen))
            {
                RaisePropertyChanged(nameof(IsTimelineVisible));
                RaisePropertyChanged(nameof(IsSelectionTranscriptVisible));
                RaisePropertyChanged(nameof(IsComposerVisible));
            }
        };
        Settings.Completed += message => Dispatcher.UIThread.Post(() => StatusText = message);
        NewChatCommand = new AsyncRelayCommand(CreateNewChatAsync);
        SendCommand = new AsyncRelayCommand(SendAsync, () => !string.IsNullOrWhiteSpace(ComposerText));
        ExportChatCommand = new AsyncRelayCommand(ExportChatAsync, () => SelectedChat is not null);
        browserStatusTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        browserStatusTimer.Tick += async (_, _) => await RefreshSelectedRunBrowserAsync();
        browserStatusTimer.Start();
        _ = InitializeAsync();
    }

    public ObservableCollection<ChatListItemViewModel> Chats { get; } = [];
    public ObservableCollection<TimelineItemViewModel> Timeline { get; } = [];
    public ObservableCollection<GoalItemViewModel> Goals { get; } = [];
    public LlmSettingsViewModel Settings { get; }
    public AsyncRelayCommand NewChatCommand { get; }
    public AsyncRelayCommand SendCommand { get; }
    public AsyncRelayCommand ExportChatCommand { get; }

    public ChatListItemViewModel? SelectedChat
    {
        get => selectedChat;
        set
        {
            if (SetProperty(ref selectedChat, value))
            {
                ExportChatCommand.RaiseCanExecuteChanged();
                if (!suppressSelectedChatLoad)
                {
                    _ = LoadChatAsync(value?.Id);
                }
            }
        }
    }

    public string ComposerText
    {
        get => composerText;
        set
        {
            if (SetProperty(ref composerText, value))
            {
                SendCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string BrowserStatus { get => browserStatus; set => SetProperty(ref browserStatus, value); }
    public string BrowserUrl { get => browserUrl; set => SetProperty(ref browserUrl, value); }
    public string BrowserTitle { get => browserTitle; set => SetProperty(ref browserTitle, value); }
    public string RestoreStatus { get => restoreStatus; set => SetProperty(ref restoreStatus, value); }
    public string SelectedRunTitle { get => selectedRunTitle; set => SetProperty(ref selectedRunTitle, value); }
    public string StatusText { get => statusText; set => SetProperty(ref statusText, value); }
    public string SelectionTranscriptText { get => selectionTranscriptText; set => SetProperty(ref selectionTranscriptText, value); }
    public bool IsBubbleViewEnabled => !IsSelectionModeEnabled;
    public bool IsTimelineVisible => !Settings.IsOpen && IsBubbleViewEnabled;
    public bool IsSelectionTranscriptVisible => !Settings.IsOpen && IsSelectionModeEnabled;
    public bool IsComposerVisible => !Settings.IsOpen;
    public bool IsSelectionModeEnabled
    {
        get => isSelectionModeEnabled;
        set
        {
            if (SetProperty(ref isSelectionModeEnabled, value))
            {
                RaisePropertyChanged(nameof(IsBubbleViewEnabled));
                RaisePropertyChanged(nameof(IsTimelineVisible));
                RaisePropertyChanged(nameof(IsSelectionTranscriptVisible));
            }
        }
    }

    private async Task InitializeAsync()
    {
        await orchestrator.InitializeAsync(CancellationToken.None);
        var chats = await orchestrator.ListChatsAsync(CancellationToken.None);
        ReplaceChats(chats);
        if (Chats.Count == 0)
        {
            await CreateNewChatAsync();
        }
        else
        {
            SelectedChat = Chats[0];
        }
    }

    private async Task CreateNewChatAsync()
    {
        var chat = await orchestrator.CreateChatAsync("New Chat", CancellationToken.None);
        var item = new ChatListItemViewModel
        {
            Id = chat.Id,
            Title = chat.Title,
            UpdatedAtUtc = chat.UpdatedAtUtc,
            ActiveRuns = 0,
        };

        Chats.Insert(0, item);
        SelectedChat = item;
        StatusText = "New chat created.";
    }

    private async Task LoadChatAsync(Guid? chatId)
    {
        if (chatId is null || loadingChatId == chatId)
        {
            return;
        }

        loadingChatId = chatId;
        selectedChatId = chatId;
        StatusText = "Loading chat...";
        try
        {
            await orchestrator.LoadChatAsync(chatId.Value, restoreBrowser: true, ApplyUpdate, CancellationToken.None);
        }
        finally
        {
            if (loadingChatId == chatId)
            {
                loadingChatId = null;
            }
        }
    }

    private async Task SendAsync()
    {
        if (selectedChatId is null)
        {
            await CreateNewChatAsync();
        }

        if (selectedChatId is null || string.IsNullOrWhiteSpace(ComposerText))
        {
            return;
        }

        var prompt = ComposerText.Trim();
        ComposerText = string.Empty;
        StatusText = "Running test...";
        await orchestrator.RunPromptAsync(selectedChatId.Value, prompt, ApplyUpdate, CancellationToken.None);
        StatusText = "Run finished.";
    }

    private async Task ExportChatAsync()
    {
        if (SelectedChat is null)
        {
            return;
        }

        try
        {
            var chat = await orchestrator.LoadChatAsync(SelectedChat.Id, restoreBrowser: false, onUpdate: null, CancellationToken.None);
            if (chat is null)
            {
                StatusText = "Unable to load the selected chat for export.";
                return;
            }

            var filePath = await textFileSaveService.SaveTextAsync(
                "Export chat history",
                BuildExportFileName(chat),
                ChatTranscriptFormatter.FormatForExport(chat),
                CancellationToken.None);

            if (filePath is not null)
            {
                StatusText = $"Chat exported to {filePath}";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Chat export failed: {ex.Message}";
        }
    }

    private void ApplyUpdate(OrchestratorUpdate update)
    {
        Dispatcher.UIThread.Post(() =>
        {
            switch (update)
            {
                case ChatLoaded loaded:
                    ApplyChat(loaded.Chat);
                    break;
                case TimelineEntryUpserted timelineUpdate:
                    UpsertTimeline(timelineUpdate.Entry);
                    break;
                case BrowserSnapshotUpdated browserUpdate:
                    if (selectedRunId == browserUpdate.RunId)
                    {
                        ApplyBrowserSnapshot(browserUpdate.Snapshot);
                    }
                    break;
                case GoalsUpdated goalsUpdate:
                    if (selectedRunId == goalsUpdate.RunId)
                    {
                        ApplyGoals(goalsUpdate.Goals);
                    }
                    break;
                case RunUpdated runUpdate:
                    ApplyRun(runUpdate.Run);
                    break;
                case OrchestrationError error:
                    StatusText = error.Message;
                    break;
            }
        });
    }

    private void ApplyChat(ChatSession chat)
    {
        currentChat = chat;
        selectedChatId = chat.Id;
        UpsertChatSummary(chat);

        Timeline.Clear();
        foreach (var entry in chat.Timeline.OrderBy(item => item.Sequence))
        {
            Timeline.Add(CreateTimelineViewModel(entry));
        }

        var run = chat.Runs
            .OrderByDescending(candidate => candidate.UpdatedAtUtc)
            .FirstOrDefault();

        if (run is not null)
        {
            ApplyRun(run);
            ApplyGoals(run.Goals);
            ApplyBrowserSnapshot(run.BrowserSnapshot);
        }
        else
        {
            selectedRunId = null;
            Goals.Clear();
            SelectedRunTitle = "No run selected";
            BrowserStatus = "No browser";
            BrowserUrl = "n/a";
            BrowserTitle = "n/a";
            RestoreStatus = "Not started";
        }

        RefreshSelectionTranscript();
        StatusText = "Chat loaded.";
    }

    private void ApplyRun(TestRun run)
    {
        selectedRunId = run.Id;
        SelectedRunTitle = $"{run.Status}: {run.UserPrompt}";
        ApplyBrowserSnapshot(run.BrowserSnapshot);
        ApplyGoals(run.Goals);
    }

    private void ApplyGoals(IReadOnlyList<GoalItem> goals)
    {
        Goals.Clear();
        foreach (var goal in goals.OrderBy(item => item.CreatedAtUtc))
        {
            Goals.Add(new GoalItemViewModel
            {
                Id = goal.Id,
                Title = goal.Title,
                SuccessCriteria = goal.SuccessCriteria,
                Status = goal.Status,
                Note = goal.Note,
                Evidence = goal.Evidence,
            });
        }
    }

    private void ApplyBrowserSnapshot(BrowserSessionSnapshot snapshot)
    {
        BrowserStatus = snapshot.RestoreStatus switch
        {
            BrowserTesting.Core.Models.RestoreStatus.Closed => "Browser closed",
            BrowserTesting.Core.Models.RestoreStatus.Failed => "Browser unavailable",
            BrowserTesting.Core.Models.RestoreStatus.NotStarted when snapshot.CurrentUrl is null => "No browser",
            _ when snapshot.CurrentUrl is null => "Browser idle",
            _ => "Browser active",
        };
        BrowserUrl = snapshot.CurrentUrl ?? "n/a";
        BrowserTitle = snapshot.PageTitle ?? "n/a";
        RestoreStatus = snapshot.RestoreStatus.ToString();
    }

    private async Task RefreshSelectedRunBrowserAsync()
    {
        if (selectedRunId is null || Interlocked.Exchange(ref browserStatusRefreshInProgress, 1) == 1)
        {
            return;
        }

        try
        {
            await orchestrator.RefreshBrowserSnapshotAsync(selectedRunId.Value, ApplyUpdate, CancellationToken.None);
        }
        catch
        {
        }
        finally
        {
            Interlocked.Exchange(ref browserStatusRefreshInProgress, 0);
        }
    }

    private void ReplaceChats(IReadOnlyList<ChatSessionSummary> chats)
    {
        Chats.Clear();
        foreach (var chat in chats)
        {
            Chats.Add(new ChatListItemViewModel
            {
                Id = chat.Id,
                Title = chat.Title,
                UpdatedAtUtc = chat.UpdatedAtUtc,
                ActiveRuns = chat.ActiveRuns,
            });
        }
    }

    private void UpsertChatSummary(ChatSession chat)
    {
        if (currentChat?.Id == chat.Id)
        {
            currentChat.Title = chat.Title;
            currentChat.UpdatedAtUtc = chat.UpdatedAtUtc;
            RefreshSelectionTranscript();
        }

        var current = Chats.FirstOrDefault(item => item.Id == chat.Id);
        var activeRuns = chat.Runs.Count(run => run.Status is TestRunStatus.Pending or TestRunStatus.Running or TestRunStatus.WaitingForTool);

        if (current is null)
        {
            Chats.Insert(0, new ChatListItemViewModel
            {
                Id = chat.Id,
                Title = chat.Title,
                UpdatedAtUtc = chat.UpdatedAtUtc,
                ActiveRuns = activeRuns,
            });
        }
        else
        {
            current.Title = chat.Title;
            current.UpdatedAtUtc = chat.UpdatedAtUtc;
            current.ActiveRuns = activeRuns;

            if (selectedChat?.Id == current.Id && !ReferenceEquals(selectedChat, current))
            {
                suppressSelectedChatLoad = true;
                try
                {
                    SelectedChat = current;
                }
                finally
                {
                    suppressSelectedChatLoad = false;
                }
            }
        }
    }

    private void UpsertTimeline(TimelineEntry entry)
    {
        if (currentChat?.Id == entry.ChatSessionId)
        {
            var currentEntry = currentChat.Timeline.FirstOrDefault(item => item.Id == entry.Id);
            if (currentEntry is null)
            {
                currentChat.Timeline.Add(entry);
            }
            else
            {
                currentEntry.Sequence = entry.Sequence;
                currentEntry.Kind = entry.Kind;
                currentEntry.Role = entry.Role;
                currentEntry.Content = entry.Content;
                currentEntry.ToolCallId = entry.ToolCallId;
                currentEntry.ToolName = entry.ToolName;
                currentEntry.MetadataJson = entry.MetadataJson;
                currentEntry.CreatedAtUtc = entry.CreatedAtUtc;
                currentEntry.TestRunId = entry.TestRunId;
            }

            currentChat.UpdatedAtUtc = DateTime.UtcNow;
            RefreshSelectionTranscript();
        }

        var existing = Timeline.FirstOrDefault(item => item.EntryId == entry.Id);
        if (existing is null)
        {
            Timeline.Add(CreateTimelineViewModel(entry));
            return;
        }

        switch (existing)
        {
            case UserTimelineItemViewModel user:
                user.Content = entry.Content;
                break;
            case AssistantTimelineItemViewModel assistant:
                assistant.Content = entry.Content;
                break;
            case ToolStartedTimelineItemViewModel started:
                started.Summary = entry.Content;
                break;
            case ToolFinishedTimelineItemViewModel finished:
                finished.Summary = entry.Content;
                break;
            case GoalChangedTimelineItemViewModel goal:
                goal.Content = entry.Content;
                break;
            case SystemTimelineItemViewModel system:
                system.Content = entry.Content;
                break;
        }
    }

    private static TimelineItemViewModel CreateTimelineViewModel(TimelineEntry entry) =>
        entry.Kind switch
        {
            TimelineItemKind.UserMessage => new UserTimelineItemViewModel(entry),
            TimelineItemKind.AssistantMessage => new AssistantTimelineItemViewModel(entry),
            TimelineItemKind.ToolCallStarted => new ToolStartedTimelineItemViewModel(entry),
            TimelineItemKind.ToolCallFinished => new ToolFinishedTimelineItemViewModel(entry),
            TimelineItemKind.GoalChanged => new GoalChangedTimelineItemViewModel(entry),
            _ => new SystemTimelineItemViewModel(entry),
        };

    private void RefreshSelectionTranscript() =>
        SelectionTranscriptText = currentChat is null
            ? string.Empty
            : ChatTranscriptFormatter.FormatForSelection(currentChat);

    private static string BuildExportFileName(ChatSession chat)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var sanitizedTitle = new string(chat.Title.Select(character => invalidCharacters.Contains(character) ? '_' : character).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(sanitizedTitle))
        {
            sanitizedTitle = "chat";
        }

        return $"{sanitizedTitle}-{chat.Id:N}.txt";
    }
}
