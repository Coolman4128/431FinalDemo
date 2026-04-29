using System.Collections.ObjectModel;
using BrowserTesting.Core.Models;
using BrowserTesting.Core.Orchestration;
using BrowserTesting.Core.Services;
using BrowserTesting.Desktop.Services;
using Avalonia.Threading;

namespace BrowserTesting.Desktop.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly ChatOrchestrator orchestrator;
    private readonly TextFileSaveService textFileSaveService;
    private readonly LlmSettingsService llmSettingsService;
    private readonly Action<Action> uiDispatcher;
    private readonly ChatListItemViewModel draftChatItem = ChatListItemViewModel.CreateDraft();
    private ChatListItemViewModel? selectedChat;
    private ChatSession? currentChat;
    private string composerText = string.Empty;
    private string browserStatus = "No browser";
    private string browserUrl = "n/a";
    private string browserTitle = "n/a";
    private string restoreStatus = "Not started";
    private string selectedRunTitle = "Draft conversation";
    private string statusText = "Ready";
    private string providerModelStatusText = string.Empty;
    private Guid? selectedChatId;
    private Guid? selectedRunId;
    private Guid? loadingChatId;
    private bool suppressSelectedChatLoad;
    private bool isSelectionModeEnabled;
    private string selectionTranscriptText = string.Empty;

    public MainWindowViewModel(
        ChatOrchestrator orchestrator,
        TextFileSaveService textFileSaveService,
        LlmSettingsService llmSettingsService,
        Action<Action>? uiDispatcher = null)
    {
        this.orchestrator = orchestrator;
        this.textFileSaveService = textFileSaveService;
        this.llmSettingsService = llmSettingsService;
        this.uiDispatcher = uiDispatcher ?? DispatchToUiThread;

        Settings = new LlmSettingsViewModel(llmSettingsService);
        Settings.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(LlmSettingsViewModel.IsOpen))
            {
                RaisePropertyChanged(nameof(IsTimelineVisible));
                RaisePropertyChanged(nameof(IsDraftWorkspaceVisible));
                RaisePropertyChanged(nameof(IsSelectionTranscriptVisible));
                RaisePropertyChanged(nameof(IsComposerVisible));
            }
        };
        Settings.Completed += message => this.uiDispatcher(() =>
        {
            StatusText = message;
            RefreshProviderModelStatusText();
        });

        NewChatCommand = new AsyncRelayCommand(SelectDraftChatAsync);
        SendCommand = new AsyncRelayCommand(SendAsync, () => !string.IsNullOrWhiteSpace(ComposerText));
        ExportChatCommand = new AsyncRelayCommand(ExportChatAsync, () => CanExportSelectedChat());

        RefreshProviderModelStatusText();
        InitializationTask = InitializeAsync();
    }

    public Task InitializationTask { get; }
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
            if (!SetProperty(ref selectedChat, value))
            {
                return;
            }

            UpdateChatSelectionState();
            ExportChatCommand.RaiseCanExecuteChanged();
            RaisePropertyChanged(nameof(IsDraftSelected));
            RaisePropertyChanged(nameof(IsTimelineVisible));
            RaisePropertyChanged(nameof(IsDraftWorkspaceVisible));

            if (suppressSelectedChatLoad)
            {
                return;
            }

            if (value is null || value.IsDraft || value.Id is null)
            {
                _ = TransitionToDraftWorkspaceAsync();
                return;
            }

            _ = SwitchToChatAsync(value.Id.Value);
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
    public string ProviderModelStatusText { get => providerModelStatusText; private set => SetProperty(ref providerModelStatusText, value); }
    public string SelectionTranscriptText { get => selectionTranscriptText; set => SetProperty(ref selectionTranscriptText, value); }
    public bool IsDraftSelected => SelectedChat?.IsDraft ?? true;
    public bool IsTimelineVisible => !Settings.IsOpen && !IsSelectionModeEnabled && !IsDraftSelected;
    public bool IsDraftWorkspaceVisible => !Settings.IsOpen && !IsSelectionModeEnabled && IsDraftSelected;
    public bool IsSelectionTranscriptVisible => !Settings.IsOpen && IsSelectionModeEnabled;
    public bool IsComposerVisible => !Settings.IsOpen;

    public bool IsSelectionModeEnabled
    {
        get => isSelectionModeEnabled;
        set
        {
            if (SetProperty(ref isSelectionModeEnabled, value))
            {
                RaisePropertyChanged(nameof(IsTimelineVisible));
                RaisePropertyChanged(nameof(IsDraftWorkspaceVisible));
                RaisePropertyChanged(nameof(IsSelectionTranscriptVisible));
            }
        }
    }

    private async Task InitializeAsync()
    {
        await orchestrator.InitializeAsync(CancellationToken.None);
        var chats = await orchestrator.ListChatsAsync(CancellationToken.None);
        ReplaceChats(chats);
        SelectChatWithoutLoading(draftChatItem);
        ResetDraftWorkspace("Ready");
    }

    private Task SelectDraftChatAsync()
    {
        SelectChatWithoutLoading(draftChatItem);
        return TransitionToDraftWorkspaceAsync();
    }

    private async Task<ChatSession> CreatePersistedChatAsync()
    {
        var chat = await orchestrator.CreateChatAsync("New Chat", CancellationToken.None);
        ApplyChat(chat, "New chat created.");
        return chat;
    }

    private async Task LoadChatAsync(Guid chatId)
    {
        if (loadingChatId == chatId)
        {
            return;
        }

        loadingChatId = chatId;
        selectedChatId = chatId;
        StatusText = "Loading chat...";

        try
        {
            var chat = await orchestrator.LoadChatAsync(chatId, ApplyUpdate, CancellationToken.None);
            if (chat is null && selectedChatId == chatId)
            {
                ResetDraftWorkspace("Unable to load the selected chat.");
            }
        }
        finally
        {
            if (loadingChatId == chatId)
            {
                loadingChatId = null;
            }
        }
    }

    private async Task SwitchToChatAsync(Guid chatId)
    {
        var previousRunId = selectedRunId;
        if (previousRunId is not null)
        {
            await orchestrator.CloseBrowserAsync(previousRunId.Value, ApplyUpdate, CancellationToken.None);
        }

        await LoadChatAsync(chatId);
    }

    private async Task TransitionToDraftWorkspaceAsync()
    {
        var previousRunId = selectedRunId;
        if (previousRunId is not null)
        {
            await orchestrator.CloseBrowserAsync(previousRunId.Value, ApplyUpdate, CancellationToken.None);
        }

        ResetDraftWorkspace("New chat draft ready.");
    }

    private async Task SendAsync()
    {
        var prompt = ComposerText.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }

        if (selectedChatId is null || SelectedChat?.IsDraft != false)
        {
            await CreatePersistedChatAsync();
        }

        if (selectedChatId is null)
        {
            return;
        }

        ComposerText = string.Empty;
        StatusText = "Running test...";
        await orchestrator.RunPromptAsync(selectedChatId.Value, prompt, ApplyUpdate, CancellationToken.None);
        StatusText = "Run finished.";
    }

    private async Task ExportChatAsync()
    {
        if (SelectedChat?.Id is not Guid chatId)
        {
            return;
        }

        try
        {
            var chat = await orchestrator.LoadChatAsync(chatId, onUpdate: null, CancellationToken.None);
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

    private void ApplyUpdate(OrchestratorUpdate update) =>
        uiDispatcher(() =>
        {
            switch (update)
            {
                case ChatLoaded loaded when selectedChatId == loaded.Chat.Id:
                    ApplyChat(loaded.Chat, "Chat loaded.");
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

    private void ApplyChat(ChatSession chat, string statusMessage)
    {
        currentChat = chat;
        selectedChatId = chat.Id;

        var savedChat = UpsertChatSummary(chat);
        SelectChatWithoutLoading(savedChat);
        RebuildTimeline();

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
            ResetBrowserSummary();
            SelectedRunTitle = "No run selected";
        }

        RefreshSelectionTranscript();
        StatusText = statusMessage;
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
        BrowserStatus = snapshot.State switch
        {
            BrowserState.Closed => "Browser closed",
            BrowserState.Failed => "Browser unavailable",
            BrowserState.NotStarted when snapshot.CurrentUrl is null => "No browser",
            _ when snapshot.CurrentUrl is null => "Browser idle",
            _ => "Browser active",
        };
        BrowserUrl = snapshot.CurrentUrl ?? "n/a";
        BrowserTitle = snapshot.PageTitle ?? "n/a";
        RestoreStatus = snapshot.State.ToString();
    }

    private void ReplaceChats(IReadOnlyList<ChatSessionSummary> chats)
    {
        Chats.Clear();
        Chats.Add(draftChatItem);

        foreach (var chat in chats.OrderByDescending(item => item.UpdatedAtUtc))
        {
            Chats.Add(new ChatListItemViewModel
            {
                Id = chat.Id,
                Title = chat.Title,
                UpdatedAtUtc = chat.UpdatedAtUtc,
                ActiveRuns = chat.ActiveRuns,
            });
        }

        UpdateChatSelectionState();
    }

    private ChatListItemViewModel UpsertChatSummary(ChatSession chat)
    {
        var activeRuns = chat.Runs.Count(run => run.Status is TestRunStatus.Pending or TestRunStatus.Running or TestRunStatus.WaitingForTool);
        var current = Chats.FirstOrDefault(item => !item.IsDraft && item.Id == chat.Id);

        if (current is null)
        {
            current = new ChatListItemViewModel
            {
                Id = chat.Id,
                Title = chat.Title,
                UpdatedAtUtc = chat.UpdatedAtUtc,
                ActiveRuns = activeRuns,
            };
            InsertSavedChatItemSorted(current);
        }
        else
        {
            current.Title = chat.Title;
            current.UpdatedAtUtc = chat.UpdatedAtUtc;
            current.ActiveRuns = activeRuns;
        }

        UpdateChatSelectionState();
        return current;
    }

    private void InsertSavedChatItemSorted(ChatListItemViewModel item)
    {
        var insertIndex = GetSortedInsertIndex(item);
        Chats.Insert(insertIndex, item);
    }

    private int GetSortedInsertIndex(ChatListItemViewModel item)
    {
        var insertIndex = 1;
        while (insertIndex < Chats.Count)
        {
            var candidate = Chats[insertIndex];
            if (ReferenceEquals(candidate, item))
            {
                insertIndex++;
                continue;
            }

            if (candidate.UpdatedAtUtc <= item.UpdatedAtUtc)
            {
                break;
            }

            insertIndex++;
        }

        return insertIndex;
    }

    private void UpsertTimeline(TimelineEntry entry)
    {
        if (currentChat?.Id != entry.ChatSessionId)
        {
            return;
        }

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
        UpsertChatSummary(currentChat);
        RefreshSelectionTranscript();
        RebuildTimeline();
    }

    private void RebuildTimeline()
    {
        Timeline.Clear();
        if (currentChat is null)
        {
            return;
        }

        var toolItemsByCallId = new Dictionary<string, ToolTimelineItemViewModel>(StringComparer.Ordinal);

        foreach (var entry in currentChat.Timeline.OrderBy(item => item.Sequence))
        {
            switch (entry.Kind)
            {
                case TimelineItemKind.UserMessage:
                    Timeline.Add(new UserTimelineItemViewModel(entry));
                    break;

                case TimelineItemKind.AssistantMessage:
                    Timeline.Add(new AssistantTimelineItemViewModel(entry));
                    break;

                case TimelineItemKind.ToolCallStarted:
                {
                    var toolItem = new ToolTimelineItemViewModel(entry);
                    toolItemsByCallId[toolItem.ItemKey] = toolItem;
                    Timeline.Add(toolItem);
                    break;
                }

                case TimelineItemKind.ToolCallFinished:
                {
                    var itemKey = entry.ToolCallId ?? entry.Id.ToString("N");
                    if (toolItemsByCallId.TryGetValue(itemKey, out var existing))
                    {
                        existing.ApplyFinishedEntry(entry);
                    }
                    else
                    {
                        var toolItem = new ToolTimelineItemViewModel(entry);
                        toolItem.ApplyFinishedEntry(entry);
                        toolItemsByCallId[toolItem.ItemKey] = toolItem;
                        Timeline.Add(toolItem);
                    }

                    break;
                }

                case TimelineItemKind.GoalChanged:
                    Timeline.Add(new GoalChangedTimelineItemViewModel(entry));
                    break;

                default:
                    Timeline.Add(new SystemTimelineItemViewModel(entry));
                    break;
            }
        }
    }

    private void RefreshSelectionTranscript() =>
        SelectionTranscriptText = currentChat is null
            ? string.Empty
            : ChatTranscriptFormatter.FormatForSelection(currentChat);

    private void SelectChatWithoutLoading(ChatListItemViewModel chatItem)
    {
        suppressSelectedChatLoad = true;
        try
        {
            SelectedChat = chatItem;
        }
        finally
        {
            suppressSelectedChatLoad = false;
        }
    }

    private void UpdateChatSelectionState()
    {
        foreach (var chat in Chats)
        {
            chat.IsSelected = ReferenceEquals(chat, SelectedChat);
        }
    }

    private void ResetDraftWorkspace(string statusMessage)
    {
        currentChat = null;
        selectedChatId = null;
        selectedRunId = null;
        loadingChatId = null;
        ComposerText = string.Empty;
        Timeline.Clear();
        Goals.Clear();
        SelectedRunTitle = "Draft conversation";
        ResetBrowserSummary();
        SelectionTranscriptText = string.Empty;
        StatusText = statusMessage;
    }

    private void ResetBrowserSummary()
    {
        BrowserStatus = "No browser";
        BrowserUrl = "n/a";
        BrowserTitle = "n/a";
        RestoreStatus = "Not started";
    }

    private bool CanExportSelectedChat() => SelectedChat?.Id is not null && !SelectedChat.IsDraft;

    private void RefreshProviderModelStatusText()
    {
        var settings = llmSettingsService.Settings;
        var providerLabel = settings.Provider == LlmProvider.OpenAi ? "OpenAI" : "Local";
        var modelName = string.IsNullOrWhiteSpace(settings.CurrentModelName) ? "No model selected" : settings.CurrentModelName;
        ProviderModelStatusText = $"{providerLabel} | {modelName}";
    }

    private static void DispatchToUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.UIThread.Post(action);
    }

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
