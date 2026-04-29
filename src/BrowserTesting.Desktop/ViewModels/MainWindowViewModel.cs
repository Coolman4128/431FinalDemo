using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;
using BrowserTesting.Desktop.Models;
using BrowserTesting.Desktop.Classes;
using BrowserTesting.Desktop.Services;
using Avalonia.Media;
using Avalonia.Threading;

namespace BrowserTesting.Desktop.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions TranscriptJsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly ChatOrchestrator orchestrator;
    private readonly Func<string, string, string, CancellationToken, Task<string?>> saveTextAsync;
    private readonly AppSettings settings;
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
        Func<string, string, string, CancellationToken, Task<string?>> saveTextAsync,
        AppSettings settings,
        LmStudioLlmClient llmClient,
        Action<Action>? uiDispatcher = null)
    {
        this.orchestrator = orchestrator;
        this.saveTextAsync = saveTextAsync;
        this.settings = settings;
        this.uiDispatcher = uiDispatcher ?? DispatchToUiThread;

        Settings = new LlmSettingsViewModel(settings, llmClient);
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

            var filePath = await saveTextAsync(
                "Export chat history",
                BuildExportFileName(chat),
                FormatTranscriptForExport(chat),
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

        var toolItemsByCallId = new Dictionary<string, TimelineItemViewModel>(StringComparer.Ordinal);

        foreach (var entry in currentChat.Timeline.OrderBy(item => item.Sequence))
        {
            switch (entry.Kind)
            {
                case TimelineItemKind.UserMessage:
                    Timeline.Add(new TimelineItemViewModel(entry));
                    break;

                case TimelineItemKind.AssistantMessage:
                    Timeline.Add(new TimelineItemViewModel(entry));
                    break;

                case TimelineItemKind.ToolCallStarted:
                {
                    var toolItem = new TimelineItemViewModel(entry);
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
                        var toolItem = new TimelineItemViewModel(entry);
                        toolItem.ApplyFinishedEntry(entry);
                        toolItemsByCallId[toolItem.ItemKey] = toolItem;
                        Timeline.Add(toolItem);
                    }

                    break;
                }

                case TimelineItemKind.GoalChanged:
                    Timeline.Add(new TimelineItemViewModel(entry));
                    break;

                default:
                    Timeline.Add(new TimelineItemViewModel(entry));
                    break;
            }
        }
    }

    private void RefreshSelectionTranscript() =>
        SelectionTranscriptText = currentChat is null
            ? string.Empty
            : FormatTranscriptForSelection(currentChat);

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

    private static string FormatTranscriptForSelection(ChatSession chat)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Chat: {chat.Title}");
        builder.AppendLine($"Chat Id: {chat.Id}");
        builder.AppendLine();

        foreach (var entry in chat.Timeline.OrderBy(item => item.Sequence))
        {
            builder.Append('[')
                .Append(entry.CreatedAtUtc.ToLocalTime().ToString("g"))
                .Append("] ")
                .AppendLine(BuildTranscriptEntryHeading(entry));

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

    private static string FormatTranscriptForExport(ChatSession chat)
    {
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
                AppendMultiline(builder, JsonSerializer.Serialize(run.BrowserSnapshot, TranscriptJsonOptions), "  ");
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

    private static string BuildTranscriptEntryHeading(TimelineEntry entry) =>
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
            return JsonNode.Parse(rawJson)?.ToJsonString(TranscriptJsonOptions) ?? rawJson;
        }
        catch
        {
            return rawJson;
        }
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

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    protected void RaisePropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => execute();
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class AsyncRelayCommand(Func<Task> executeAsync, Func<bool>? canExecute = null) : ICommand
{
    private bool isRunning;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !isRunning && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        try
        {
            isRunning = true;
            RaiseCanExecuteChanged();
            await executeAsync();
        }
        finally
        {
            isRunning = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

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

public sealed class TimelineItemViewModel : ObservableObject
{
    private string content;
    private string toolName;
    private bool isRunning;
    private bool success;
    private string? finalSummary;
    private string? finalTestResults;
    private string? finalRemainingWork;

    public TimelineItemViewModel(TimelineEntry entry)
    {
        ItemKey = entry.ToolCallId ?? entry.Id.ToString("N");
        CreatedAtUtc = entry.CreatedAtUtc;
        Kind = entry.Kind;
        content = entry.Content;
        toolName = entry.ToolName ?? "tool";
        isRunning = entry.Kind == TimelineItemKind.ToolCallStarted;
        success = entry.Kind != TimelineItemKind.ToolCallFinished || ReadSuccess(entry.MetadataJson);
        finalSummary = ReadEndTaskValue(entry.ToolName, entry.MetadataJson, "summary");
        finalTestResults = ReadEndTaskValue(entry.ToolName, entry.MetadataJson, "test_results");
        finalRemainingWork = ReadEndTaskValue(entry.ToolName, entry.MetadataJson, "remaining_work");
    }

    public string ItemKey { get; }
    public DateTime CreatedAtUtc { get; }
    public TimelineItemKind Kind { get; }
    public string Timestamp => CreatedAtUtc.ToLocalTime().ToString("t");
    public bool IsUserMessage => Kind == TimelineItemKind.UserMessage;
    public bool IsAssistantMessage => Kind == TimelineItemKind.AssistantMessage;
    public bool IsTool => Kind is TimelineItemKind.ToolCallStarted or TimelineItemKind.ToolCallFinished;
    public bool IsGoalChanged => Kind == TimelineItemKind.GoalChanged;
    public bool IsSystem => !IsUserMessage && !IsAssistantMessage && !IsTool && !IsGoalChanged;

    public string Content
    {
        get => content;
        set => SetProperty(ref content, value);
    }

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
