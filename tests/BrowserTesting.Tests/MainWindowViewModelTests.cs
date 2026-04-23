using BrowserTesting.Core.Abstractions;
using BrowserTesting.Core.Models;
using BrowserTesting.Core.Orchestration;
using BrowserTesting.Desktop.Services;
using BrowserTesting.Desktop.ViewModels;

namespace BrowserTesting.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public async Task MainWindowViewModel_InitializeAsync_LeavesDraftSelectedWhenChatsExist()
    {
        var orchestrator = new FakeChatOrchestrator();
        orchestrator.SeedChat("Checkout smoke test");
        orchestrator.SeedChat("Pricing verification");

        var viewModel = CreateViewModel(orchestrator, new FakeLlmSettingsService());
        await viewModel.InitializationTask;

        Assert.NotNull(viewModel.SelectedChat);
        Assert.True(viewModel.SelectedChat!.IsDraft);
        Assert.True(viewModel.Chats[0].IsDraft);
        Assert.Equal(3, viewModel.Chats.Count);
        Assert.True(viewModel.IsDraftWorkspaceVisible);
        Assert.All(viewModel.Chats.Skip(1), chat => Assert.False(chat.IsSelected));
    }

    [Fact]
    public async Task MainWindowViewModel_SendFromDraft_CreatesChatAndRunsPrompt()
    {
        var orchestrator = new FakeChatOrchestrator();
        var viewModel = CreateViewModel(orchestrator, new FakeLlmSettingsService());
        await viewModel.InitializationTask;

        viewModel.ComposerText = "Open example.com and verify the login form.";
        viewModel.SendCommand.Execute(null);

        var run = await orchestrator.RunPromptCalled.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, orchestrator.CreateChatCallCount);
        Assert.Equal("Open example.com and verify the login form.", run.prompt);
        Assert.False(viewModel.SelectedChat!.IsDraft);
        Assert.Equal(run.chatId, viewModel.SelectedChat.Id);
        Assert.True(viewModel.IsTimelineVisible);
        Assert.False(viewModel.IsDraftWorkspaceVisible);
    }

    [Fact]
    public async Task MainWindowViewModel_SelectingTopMostLatestChat_DoesNotReorder()
    {
        var orchestrator = new FakeChatOrchestrator();
        orchestrator.SeedChat("Older");
        await Task.Delay(10);
        var latest = orchestrator.SeedChat("Latest");

        var viewModel = CreateViewModel(orchestrator, new FakeLlmSettingsService());
        await viewModel.InitializationTask;

        var beforeSelectionOrder = viewModel.Chats.Where(chat => !chat.IsDraft).Select(chat => chat.Id).ToArray();
        viewModel.SelectedChat = viewModel.Chats.Single(item => item.Id == latest.Id);
        var afterSelectionOrder = viewModel.Chats.Where(chat => !chat.IsDraft).Select(chat => chat.Id).ToArray();

        Assert.Equal(beforeSelectionOrder, afterSelectionOrder);
        Assert.Equal(3, viewModel.Chats.Count);
        Assert.Equal(latest.Id, viewModel.Chats[1].Id);
    }

    [Fact]
    public async Task MainWindowViewModel_SelectingNonTopChat_DoesNotReorderWhenAlreadySorted()
    {
        var orchestrator = new FakeChatOrchestrator();
        var oldest = orchestrator.SeedChat("Oldest");
        await Task.Delay(10);
        orchestrator.SeedChat("Latest");

        var viewModel = CreateViewModel(orchestrator, new FakeLlmSettingsService());
        await viewModel.InitializationTask;

        var beforeSelectionOrder = viewModel.Chats.Where(chat => !chat.IsDraft).Select(chat => chat.Id).ToArray();
        viewModel.SelectedChat = viewModel.Chats.Single(item => item.Id == oldest.Id);
        var afterSelectionOrder = viewModel.Chats.Where(chat => !chat.IsDraft).Select(chat => chat.Id).ToArray();

        Assert.Equal(beforeSelectionOrder, afterSelectionOrder);
    }

    [Fact]
    public async Task MainWindowViewModel_MergesToolLifecycleIntoSingleTimelineItem()
    {
        var orchestrator = new FakeChatOrchestrator();
        var chat = orchestrator.SeedChat("Saved session");
        var viewModel = CreateViewModel(orchestrator, new FakeLlmSettingsService());
        await viewModel.InitializationTask;

        viewModel.SelectedChat = viewModel.Chats.Single(item => item.Id == chat.Id);

        var runId = Guid.NewGuid();
        var started = new TimelineEntry
        {
            Id = Guid.NewGuid(),
            ChatSessionId = chat.Id,
            TestRunId = runId,
            Sequence = 1,
            Kind = TimelineItemKind.ToolCallStarted,
            ToolCallId = "tool-call-1",
            ToolName = "click",
            CreatedAtUtc = DateTime.UtcNow,
        };

        orchestrator.Emit(new TimelineEntryUpserted(started));

        var toolItem = Assert.IsType<ToolTimelineItemViewModel>(Assert.Single(viewModel.Timeline));
        Assert.True(toolItem.IsRunning);
        Assert.Equal("Running", toolItem.StatusText);

        var finished = new TimelineEntry
        {
            Id = Guid.NewGuid(),
            ChatSessionId = chat.Id,
            TestRunId = runId,
            Sequence = 2,
            Kind = TimelineItemKind.ToolCallFinished,
            ToolCallId = "tool-call-1",
            ToolName = "click",
            Content = "Clicked the button.",
            MetadataJson = """{"success":true}""",
            CreatedAtUtc = DateTime.UtcNow.AddSeconds(1),
        };

        orchestrator.Emit(new TimelineEntryUpserted(finished));

        toolItem = Assert.IsType<ToolTimelineItemViewModel>(Assert.Single(viewModel.Timeline));
        Assert.False(toolItem.IsRunning);
        Assert.True(toolItem.Success);
        Assert.Equal("Success", toolItem.StatusText);
    }

    [Fact]
    public async Task MainWindowViewModel_RefreshesProviderStatusAfterSettingsSave()
    {
        var settingsService = new FakeLlmSettingsService();
        settingsService.Settings.Provider = LlmProvider.Local;
        settingsService.Settings.LocalModelName = "local-model";
        settingsService.ModelsByProvider[LlmProvider.Local] = ["local-model"];
        settingsService.ModelsByProvider[LlmProvider.OpenAi] = ["gpt-5.4-mini"];

        var viewModel = CreateViewModel(new FakeChatOrchestrator(), settingsService);
        await viewModel.InitializationTask;

        await viewModel.Settings.OpenAsync();
        viewModel.Settings.SelectedProviderOption = viewModel.Settings.ProviderOptions.Single(option => option.Value == LlmProvider.OpenAi);
        viewModel.Settings.OpenAiApiKey = "sk-test";
        await viewModel.Settings.RefreshModelsAsync();
        viewModel.Settings.SelectedModel = "gpt-5.4-mini";
        await viewModel.Settings.SaveAsync();

        Assert.Equal("OpenAI | gpt-5.4-mini", viewModel.ProviderModelStatusText);
    }

    [Fact]
    public async Task MainWindowViewModel_SelectingAnotherSavedChat_ClosesPreviousRunBrowser()
    {
        var orchestrator = new FakeChatOrchestrator();
        var firstChat = orchestrator.SeedChat("First", "Open first site");
        var secondChat = orchestrator.SeedChat("Second", "Open second site");
        var viewModel = CreateViewModel(orchestrator, new FakeLlmSettingsService());
        await viewModel.InitializationTask;

        viewModel.SelectedChat = viewModel.Chats.Single(item => item.Id == firstChat.Id);
        await orchestrator.WaitForLoadCountAsync(1);

        viewModel.SelectedChat = viewModel.Chats.Single(item => item.Id == secondChat.Id);
        await orchestrator.WaitForLoadCountAsync(2);

        Assert.Collection(
            orchestrator.ClosedRunIds,
            runId => Assert.Equal(firstChat.Runs[0].Id, runId));
        Assert.Equal("Browser closed", viewModel.BrowserStatus);
    }

    [Fact]
    public async Task MainWindowViewModel_SelectingDraft_ClosesPreviousRunBrowserAndResetsWorkspace()
    {
        var orchestrator = new FakeChatOrchestrator();
        var savedChat = orchestrator.SeedChat("Saved", "Resume old session");
        var viewModel = CreateViewModel(orchestrator, new FakeLlmSettingsService());
        await viewModel.InitializationTask;

        viewModel.SelectedChat = viewModel.Chats.Single(item => item.Id == savedChat.Id);
        await orchestrator.WaitForLoadCountAsync(1);

        viewModel.SelectedChat = viewModel.Chats[0];
        await orchestrator.WaitForCloseCountAsync(1);

        Assert.Collection(
            orchestrator.ClosedRunIds,
            runId => Assert.Equal(savedChat.Runs[0].Id, runId));
        Assert.True(viewModel.SelectedChat!.IsDraft);
        Assert.Equal("No browser", viewModel.BrowserStatus);
        Assert.Equal("Draft conversation", viewModel.SelectedRunTitle);
    }

    [Fact]
    public async Task MainWindowViewModel_SwitchingBetweenSavedChatsAndDraft_PreservesChatList()
    {
        var orchestrator = new FakeChatOrchestrator();
        var oldest = orchestrator.SeedChat("Oldest", "Old run");
        await Task.Delay(10);
        var newest = orchestrator.SeedChat("Newest", "New run");
        var viewModel = CreateViewModel(orchestrator, new FakeLlmSettingsService());
        await viewModel.InitializationTask;

        var expectedChatIds = viewModel.Chats.Where(chat => !chat.IsDraft).Select(chat => chat.Id).ToArray();

        viewModel.SelectedChat = viewModel.Chats.Single(item => item.Id == newest.Id);
        await orchestrator.WaitForLoadCountAsync(1);
        Assert.Equal(expectedChatIds, viewModel.Chats.Where(chat => !chat.IsDraft).Select(chat => chat.Id).ToArray());

        viewModel.SelectedChat = viewModel.Chats.Single(item => item.Id == oldest.Id);
        await orchestrator.WaitForLoadCountAsync(2);
        Assert.Equal(expectedChatIds, viewModel.Chats.Where(chat => !chat.IsDraft).Select(chat => chat.Id).ToArray());

        viewModel.SelectedChat = viewModel.Chats[0];
        await orchestrator.WaitForCloseCountAsync(2);
        Assert.Equal(expectedChatIds, viewModel.Chats.Where(chat => !chat.IsDraft).Select(chat => chat.Id).ToArray());

        viewModel.SelectedChat = viewModel.Chats.Single(item => item.Id == newest.Id);
        await orchestrator.WaitForLoadCountAsync(3);
        Assert.Equal(expectedChatIds, viewModel.Chats.Where(chat => !chat.IsDraft).Select(chat => chat.Id).ToArray());
        Assert.Equal(3, viewModel.Chats.Count);
    }

    private static MainWindowViewModel CreateViewModel(FakeChatOrchestrator orchestrator, FakeLlmSettingsService settingsService) =>
        new(orchestrator, new FakeTextFileSaveService(), settingsService, action => action());

    private sealed class FakeTextFileSaveService : ITextFileSaveService
    {
        public Task<string?> SaveTextAsync(string title, string suggestedFileName, string content, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("chat.txt");
    }

    private sealed class FakeLlmSettingsService : ILlmSettingsService
    {
        public AppSettings Settings { get; } = AppSettings.CreateDefault(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        public Dictionary<LlmProvider, IReadOnlyList<string>> ModelsByProvider { get; } = [];

        public Task<IReadOnlyList<string>> ListModelsAsync(LlmProvider provider, string? openAiApiKey, CancellationToken cancellationToken) =>
            Task.FromResult(ModelsByProvider.TryGetValue(provider, out var models) ? models : (IReadOnlyList<string>)[]);

        public Task SaveAsync(
            LlmProvider provider,
            string? localModelName,
            string? openAiModelName,
            string? openAiApiKey,
            CancellationToken cancellationToken)
        {
            Settings.Provider = provider;
            Settings.LocalModelName = localModelName ?? Settings.LocalModelName;
            Settings.OpenAiModelName = openAiModelName ?? Settings.OpenAiModelName;
            Settings.OpenAiApiKey = openAiApiKey;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeChatOrchestrator : IChatOrchestrator
    {
        private readonly Dictionary<Guid, ChatSession> chats = [];
        private readonly List<ChatSessionSummary> chatSummaries = [];
        private int loadCallCount;

        public TaskCompletionSource<(Guid chatId, string prompt)> RunPromptCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action<OrchestratorUpdate>? LatestOnUpdate { get; private set; }
        public int CreateChatCallCount { get; private set; }
        public List<Guid> ClosedRunIds { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<ChatSessionSummary>> ListChatsAsync(CancellationToken cancellationToken) =>
            Task.FromResult((IReadOnlyList<ChatSessionSummary>)chatSummaries.OrderByDescending(item => item.UpdatedAtUtc).ToList());

        public Task<ChatSession> CreateChatAsync(string? title, CancellationToken cancellationToken)
        {
            CreateChatCallCount++;
            var chat = new ChatSession
            {
                Id = Guid.NewGuid(),
                Title = title ?? "New Chat",
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            };

            chats[chat.Id] = chat;
            chatSummaries.Insert(0, new ChatSessionSummary
            {
                Id = chat.Id,
                Title = chat.Title,
                UpdatedAtUtc = chat.UpdatedAtUtc,
                ActiveRuns = 0,
            });

            return Task.FromResult(Clone(chat));
        }

        public Task<ChatSession?> LoadChatAsync(
            Guid chatId,
            Action<OrchestratorUpdate>? onUpdate,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref loadCallCount);
            LatestOnUpdate = onUpdate;
            if (!chats.TryGetValue(chatId, out var chat))
            {
                return Task.FromResult<ChatSession?>(null);
            }

            var clone = Clone(chat);
            if (onUpdate is not null)
            {
                var run = clone.Runs.OrderByDescending(candidate => candidate.UpdatedAtUtc).FirstOrDefault();
                if (run is not null)
                {
                    run.BrowserSnapshot = new BrowserSessionSnapshot
                    {
                        TestRunId = run.Id,
                        ProfilePath = run.BrowserSnapshot.ProfilePath,
                        RestoreStatus = RestoreStatus.Closed,
                        LastCapturedAtUtc = DateTime.UtcNow,
                    };
                }
            }
            onUpdate?.Invoke(new ChatLoaded(clone));
            return Task.FromResult<ChatSession?>(clone);
        }

        public Task<BrowserSessionSnapshot?> CloseBrowserAsync(Guid runId, Action<OrchestratorUpdate>? onUpdate, CancellationToken cancellationToken)
        {
            ClosedRunIds.Add(runId);

            foreach (var chat in chats.Values)
            {
                var run = chat.Runs.FirstOrDefault(candidate => candidate.Id == runId);
                if (run is null)
                {
                    continue;
                }

                run.BrowserSnapshot = new BrowserSessionSnapshot
                {
                    TestRunId = runId,
                    ProfilePath = run.BrowserSnapshot.ProfilePath,
                    RestoreStatus = RestoreStatus.Closed,
                    LastCapturedAtUtc = DateTime.UtcNow,
                };
                onUpdate?.Invoke(new BrowserSnapshotUpdated(runId, run.BrowserSnapshot));
                return Task.FromResult<BrowserSessionSnapshot?>(run.BrowserSnapshot);
            }

            return Task.FromResult<BrowserSessionSnapshot?>(null);
        }

        public Task<BrowserSessionSnapshot?> RefreshBrowserSnapshotAsync(Guid runId, Action<OrchestratorUpdate>? onUpdate, CancellationToken cancellationToken) =>
            Task.FromResult<BrowserSessionSnapshot?>(null);

        public Task<TestRun> RunPromptAsync(Guid chatId, string prompt, Action<OrchestratorUpdate> onUpdate, CancellationToken cancellationToken)
        {
            LatestOnUpdate = onUpdate;
            RunPromptCalled.TrySetResult((chatId, prompt));
            return Task.FromResult(new TestRun
            {
                Id = Guid.NewGuid(),
                ChatSessionId = chatId,
                UserPrompt = prompt,
                Status = TestRunStatus.Running,
                UpdatedAtUtc = DateTime.UtcNow,
            });
        }

        public ChatSession SeedChat(string title, string? runPrompt = null)
        {
            var chat = new ChatSession
            {
                Id = Guid.NewGuid(),
                Title = title,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            };

            if (!string.IsNullOrWhiteSpace(runPrompt))
            {
                chat.Runs.Add(new TestRun
                {
                    Id = Guid.NewGuid(),
                    ChatSessionId = chat.Id,
                    UserPrompt = runPrompt,
                    Status = TestRunStatus.Completed,
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow,
                    BrowserSnapshot = new BrowserSessionSnapshot
                    {
                        TestRunId = Guid.NewGuid(),
                        CurrentUrl = "https://example.com",
                        PageTitle = "Example",
                        RestoreStatus = RestoreStatus.Active,
                    },
                });
                chat.Runs[0].BrowserSnapshot.TestRunId = chat.Runs[0].Id;
            }

            chats[chat.Id] = chat;
            chatSummaries.Add(new ChatSessionSummary
            {
                Id = chat.Id,
                Title = title,
                UpdatedAtUtc = chat.UpdatedAtUtc,
                ActiveRuns = 0,
            });

            return Clone(chat);
        }

        public void Emit(OrchestratorUpdate update) => LatestOnUpdate?.Invoke(update);

        public async Task WaitForLoadCountAsync(int expectedCount)
        {
            await WaitForConditionAsync(() => Volatile.Read(ref loadCallCount) >= expectedCount);
        }

        public async Task WaitForCloseCountAsync(int expectedCount)
        {
            await WaitForConditionAsync(() => ClosedRunIds.Count >= expectedCount);
        }

        private static async Task WaitForConditionAsync(Func<bool> condition)
        {
            var timeoutAt = DateTime.UtcNow.AddSeconds(2);
            while (!condition())
            {
                if (DateTime.UtcNow >= timeoutAt)
                {
                    throw new TimeoutException("Timed out waiting for view model update.");
                }

                await Task.Delay(10);
            }
        }

        private static ChatSession Clone(ChatSession source) =>
            new()
            {
                Id = source.Id,
                Title = source.Title,
                CreatedAtUtc = source.CreatedAtUtc,
                UpdatedAtUtc = source.UpdatedAtUtc,
                Runs = source.Runs.Select(run => new TestRun
                {
                    Id = run.Id,
                    ChatSessionId = run.ChatSessionId,
                    UserPrompt = run.UserPrompt,
                    Status = run.Status,
                    FailureReason = run.FailureReason,
                    CreatedAtUtc = run.CreatedAtUtc,
                    UpdatedAtUtc = run.UpdatedAtUtc,
                    CompletedAtUtc = run.CompletedAtUtc,
                    BrowserSnapshot = run.BrowserSnapshot,
                    Goals = run.Goals,
                }).ToList(),
                Timeline = source.Timeline.Select(entry => new TimelineEntry
                {
                    Id = entry.Id,
                    ChatSessionId = entry.ChatSessionId,
                    TestRunId = entry.TestRunId,
                    Sequence = entry.Sequence,
                    Kind = entry.Kind,
                    Role = entry.Role,
                    Content = entry.Content,
                    ToolCallId = entry.ToolCallId,
                    ToolName = entry.ToolName,
                    MetadataJson = entry.MetadataJson,
                    CreatedAtUtc = entry.CreatedAtUtc,
                }).ToList(),
            };
    }
}
