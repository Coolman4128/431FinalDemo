using System.Text.Json.Nodes;
using BrowserTesting.Core.Abstractions;
using BrowserTesting.Core.Llm;
using BrowserTesting.Core.Models;
using BrowserTesting.Core.Orchestration;
using BrowserTesting.Core.Services;
using BrowserTesting.Infrastructure.Persistence;
using BrowserTesting.Infrastructure.Tools;

namespace BrowserTesting.Tests;

public sealed class OrchestrationTests
{
    [Fact]
    public async Task RunPromptAsync_StreamsTextThenToolEventsThenContinues()
    {
        var repository = new InMemoryRepository();
        var browserManager = new FakeBrowserManager();
        var secretStore = new FakeSecretStore();
        var toolExecutor = new FakeToolExecutor();
        var llmClient = new FakeLlmClient(
            [
                new LlmTextDelta("Starting test."),
                new LlmToolCallDelta(0, "call_1", "create_goal", "{\"title\":\"Login\",\"success_criteria\":\"Dashboard appears\"}"),
                new LlmStreamCompleted("tool_calls"),
            ],
            [
                new LlmTextDelta("Goal created and complete."),
                new LlmStreamCompleted("stop"),
            ]);

        var orchestrator = new ChatOrchestrator(
            repository,
            llmClient,
            new FixedToolRegistry(),
            toolExecutor,
            browserManager,
            secretStore,
            AppSettings.CreateDefault(Path.GetTempPath()));

        await orchestrator.InitializeAsync(CancellationToken.None);
        var chat = await orchestrator.CreateChatAsync("Tests", CancellationToken.None);

        var updates = new List<OrchestratorUpdate>();
        await orchestrator.RunPromptAsync(chat.Id, "Log in to the site", updates.Add, CancellationToken.None);

        Assert.Contains(updates, update => update is TimelineEntryUpserted timeline && timeline.Entry.Kind == TimelineItemKind.AssistantMessage && timeline.Entry.Content.Contains("Starting test."));
        Assert.Contains(updates, update => update is TimelineEntryUpserted timeline && timeline.Entry.Kind == TimelineItemKind.ToolCallStarted && timeline.Entry.ToolName == "create_goal");
        Assert.Contains(updates, update => update is TimelineEntryUpserted timeline && timeline.Entry.Kind == TimelineItemKind.ToolCallFinished && timeline.Entry.Content.Contains("Tool executed"));
        Assert.Contains(updates, update => update is TimelineEntryUpserted timeline && timeline.Entry.Kind == TimelineItemKind.AssistantMessage && timeline.Entry.Content.Contains("Goal created and complete."));
        Assert.Equal(2, llmClient.RequestCount);
    }

    [Fact]
    public async Task RunPromptAsync_DoesNotStopWhenGoalsRemainPendingAndTurnHasNoTools()
    {
        var repository = new InMemoryRepository();
        var browserManager = new FakeBrowserManager();
        var goalService = new GoalService(repository);
        var secretStore = new FakeSecretStore();
        var toolExecutor = new ToolExecutor(browserManager, goalService, repository, secretStore, new ToolRegistry());
        var llmClient = new FakeLlmClient(
            [
                new LlmTextDelta("Creating the test goal."),
                new LlmToolCallDelta(0, "call_1", "create_goal", "{\"title\":\"Check time\",\"success_criteria\":\"A visible time is reported\"}"),
                new LlmStreamCompleted("tool_calls"),
            ],
            [
                new LlmTextDelta("The goal exists, so I will keep working on it."),
                new LlmStreamCompleted("stop"),
            ],
            [
                new LlmToolCallDelta(0, "call_2", "list_goals", "{}"),
                new LlmStreamCompleted("tool_calls"),
            ]);

        var settings = AppSettings.CreateDefault(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        settings.MaxToolIterations = 3;

        var orchestrator = new ChatOrchestrator(
            repository,
            llmClient,
            new FixedToolRegistry(),
            toolExecutor,
            browserManager,
            secretStore,
            settings);

        await orchestrator.InitializeAsync(CancellationToken.None);
        var chat = await orchestrator.CreateChatAsync("Pending Goals", CancellationToken.None);

        var updates = new List<OrchestratorUpdate>();
        var run = await orchestrator.RunPromptAsync(chat.Id, "Find the time on the page", updates.Add, CancellationToken.None);

        Assert.Equal(3, llmClient.RequestCount);
        Assert.Equal(TestRunStatus.Failed, run.Status);
        Assert.Contains(updates, update => update is TimelineEntryUpserted timeline && timeline.Entry.Kind == TimelineItemKind.SystemNotice);
    }

    [Fact]
    public async Task RunPromptAsync_BuildsSystemPromptWithToolFirstExecutionGuidance()
    {
        var repository = new InMemoryRepository();
        var browserManager = new FakeBrowserManager();
        var secretStore = new FakeSecretStore();
        var toolExecutor = new FakeToolExecutor();
        var llmClient = new FakeLlmClient(
            [
                new LlmToolCallDelta(0, "call_1", "create_goal", "{\"title\":\"Login\",\"success_criteria\":\"Page can be verified\"}"),
                new LlmStreamCompleted("tool_calls"),
            ]);

        var orchestrator = new ChatOrchestrator(
            repository,
            llmClient,
            new FixedToolRegistry(),
            toolExecutor,
            browserManager,
            secretStore,
            AppSettings.CreateDefault(Path.GetTempPath()));

        await orchestrator.InitializeAsync(CancellationToken.None);
        var chat = await orchestrator.CreateChatAsync("Prompt", CancellationToken.None);

        await orchestrator.RunPromptAsync(chat.Id, "Verify the login page", _ => { }, CancellationToken.None);

        var systemPrompt = llmClient.Requests[0].Messages[0].Content;
        Assert.NotNull(systemPrompt);
        Assert.Contains("browser-testing agent controlling Selenium tools", systemPrompt);
        Assert.Contains("Always create explicit goals before significant browser work", systemPrompt);
        Assert.Contains("Do not repeat the same tool name with the same arguments", systemPrompt);
        Assert.Contains("\"UserPrompt\":\"Verify the login page\"", systemPrompt);
    }

    [Fact]
    public async Task RunPromptAsync_RecoversTaggedToolCallSyntaxFromAssistantText()
    {
        var repository = new InMemoryRepository();
        var browserManager = new FakeBrowserManager();
        var secretStore = new FakeSecretStore();
        var toolExecutor = new FakeToolExecutor();
        var llmClient = new FakeLlmClient(
            [
                new LlmTextDelta("I created the goal and will open the browser next.\n\n<tool_call>\n<function=open_browser>\n<parameter=url>\nhttps://www.google.com\n</parameter>\n</function>\n</tool_call>"),
                new LlmStreamCompleted("stop"),
            ],
            [
                new LlmTextDelta("Browser opened."),
                new LlmStreamCompleted("stop"),
            ]);

        var orchestrator = new ChatOrchestrator(
            repository,
            llmClient,
            new FixedToolRegistry(),
            toolExecutor,
            browserManager,
            secretStore,
            AppSettings.CreateDefault(Path.GetTempPath()));

        await orchestrator.InitializeAsync(CancellationToken.None);
        var chat = await orchestrator.CreateChatAsync("Tagged", CancellationToken.None);

        var updates = new List<OrchestratorUpdate>();
        await orchestrator.RunPromptAsync(chat.Id, "Open Google", updates.Add, CancellationToken.None);

        Assert.Contains(updates, update => update is TimelineEntryUpserted timeline && timeline.Entry.Kind == TimelineItemKind.ToolCallStarted && timeline.Entry.ToolName == "open_browser");
        Assert.Contains(updates, update => update is TimelineEntryUpserted timeline && timeline.Entry.Kind == TimelineItemKind.AssistantMessage && !timeline.Entry.Content.Contains("<tool_call>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunPromptAsync_RecoversNarratedToolCallWithInlineJsonArguments()
    {
        var repository = new InMemoryRepository();
        var browserManager = new FakeBrowserManager();
        var secretStore = new FakeSecretStore();
        var toolExecutor = new FakeToolExecutor();
        var llmClient = new FakeLlmClient(
            [
                new LlmTextDelta("I will call open_browser with {\"url\":\"https://www.google.com\"}."),
                new LlmStreamCompleted("stop"),
            ],
            [
                new LlmTextDelta("Browser opened."),
                new LlmStreamCompleted("stop"),
            ]);

        var orchestrator = new ChatOrchestrator(
            repository,
            llmClient,
            new FixedToolRegistry(),
            toolExecutor,
            browserManager,
            secretStore,
            AppSettings.CreateDefault(Path.GetTempPath()));

        await orchestrator.InitializeAsync(CancellationToken.None);
        var chat = await orchestrator.CreateChatAsync("Narrated", CancellationToken.None);

        var updates = new List<OrchestratorUpdate>();
        await orchestrator.RunPromptAsync(chat.Id, "Open Google", updates.Add, CancellationToken.None);

        Assert.Contains(updates, update => update is TimelineEntryUpserted timeline && timeline.Entry.Kind == TimelineItemKind.ToolCallStarted && timeline.Entry.ToolName == "open_browser");
        Assert.Contains(updates, update => update is TimelineEntryUpserted timeline && timeline.Entry.Kind == TimelineItemKind.AssistantMessage && !timeline.Entry.Content.Contains("\"url\":\"https://www.google.com\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunPromptAsync_InfersOpenBrowserFromNarratedIntent()
    {
        var repository = new InMemoryRepository();
        var browserManager = new FakeBrowserManager();
        var goalService = new GoalService(repository);
        var secretStore = new FakeSecretStore();
        var toolExecutor = new ToolExecutor(browserManager, goalService, repository, secretStore, new ToolRegistry());
        var llmClient = new FakeLlmClient(
            [
                new LlmToolCallDelta(0, "call_1", "create_goal", "{\"title\":\"Check time\",\"success_criteria\":\"Google shows a time\"}"),
                new LlmStreamCompleted("tool_calls"),
            ],
            [
                new LlmTextDelta("Goal created successfully. Let me start by opening a browser and navigating to Google."),
                new LlmStreamCompleted("stop"),
            ],
            [
                new LlmTextDelta("Browser opened."),
                new LlmStreamCompleted("stop"),
            ]);

        var settings = AppSettings.CreateDefault(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        settings.MaxToolIterations = 3;

        var orchestrator = new ChatOrchestrator(
            repository,
            llmClient,
            new ToolRegistry(),
            toolExecutor,
            browserManager,
            secretStore,
            settings);

        await orchestrator.InitializeAsync(CancellationToken.None);
        var chat = await orchestrator.CreateChatAsync("Infer Open Browser", CancellationToken.None);

        var updates = new List<OrchestratorUpdate>();
        await orchestrator.RunPromptAsync(chat.Id, "Search Google for what is the time", updates.Add, CancellationToken.None);

        Assert.Contains(browserManager.ExecutedTools, executed => executed.ToolName == "open_browser" &&
                                                                  executed.Arguments["url"]!.GetValue<string>() == "https://www.google.com");
        Assert.Contains(updates, update => update is TimelineEntryUpserted timeline &&
                                           timeline.Entry.Kind == TimelineItemKind.SystemNotice &&
                                           timeline.Entry.Content.Contains("Recovered an implied tool action", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunPromptAsync_AddsNoticeWhenAssistantNarratesToolIntentWithoutStructuredCall()
    {
        var repository = new InMemoryRepository();
        var browserManager = new FakeBrowserManager();
        var goalService = new GoalService(repository);
        var secretStore = new FakeSecretStore();
        var toolExecutor = new ToolExecutor(browserManager, goalService, repository, secretStore, new ToolRegistry());
        var llmClient = new FakeLlmClient(
            [
                new LlmToolCallDelta(0, "call_1", "create_goal", "{\"title\":\"Open browser\",\"success_criteria\":\"Google is opened\"}"),
                new LlmStreamCompleted("tool_calls"),
            ],
            [
                new LlmTextDelta("I will use open_browser next."),
                new LlmStreamCompleted("stop"),
            ],
            [
                new LlmTextDelta("Still no tool call."),
                new LlmStreamCompleted("stop"),
            ]);

        var settings = AppSettings.CreateDefault(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        settings.MaxToolIterations = 2;

        var orchestrator = new ChatOrchestrator(
            repository,
            llmClient,
            new ToolRegistry(),
            toolExecutor,
            browserManager,
            secretStore,
            settings);

        await orchestrator.InitializeAsync(CancellationToken.None);
        var chat = await orchestrator.CreateChatAsync("Narrated Intent", CancellationToken.None);

        var updates = new List<OrchestratorUpdate>();
        await orchestrator.RunPromptAsync(chat.Id, "Open Google", updates.Add, CancellationToken.None);

        Assert.Contains(updates, update => update is TimelineEntryUpserted timeline &&
                                           timeline.Entry.Kind == TimelineItemKind.SystemNotice &&
                                           timeline.Entry.Content.Contains("did not emit a structured tool call", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunPromptAsync_FailsAfterRepeatedEmptyTurnsWithUnresolvedGoals()
    {
        var repository = new InMemoryRepository();
        var browserManager = new FakeBrowserManager();
        var goalService = new GoalService(repository);
        var secretStore = new FakeSecretStore();
        var toolExecutor = new ToolExecutor(browserManager, goalService, repository, secretStore, new ToolRegistry());
        var llmClient = new FakeLlmClient(
            [
                new LlmToolCallDelta(0, "call_1", "create_goal", "{\"title\":\"Check time\",\"success_criteria\":\"Google shows a time\"}"),
                new LlmStreamCompleted("tool_calls"),
            ],
            [
                new LlmStreamCompleted("stop"),
            ],
            [
                new LlmStreamCompleted("stop"),
            ],
            [
                new LlmStreamCompleted("stop"),
            ]);

        var settings = AppSettings.CreateDefault(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        settings.MaxToolIterations = 5;

        var orchestrator = new ChatOrchestrator(
            repository,
            llmClient,
            new ToolRegistry(),
            toolExecutor,
            browserManager,
            secretStore,
            settings);

        await orchestrator.InitializeAsync(CancellationToken.None);
        var chat = await orchestrator.CreateChatAsync("Empty Turns", CancellationToken.None);

        var updates = new List<OrchestratorUpdate>();
        var run = await orchestrator.RunPromptAsync(chat.Id, "Search Google for what is the time", updates.Add, CancellationToken.None);

        Assert.Equal(TestRunStatus.Failed, run.Status);
        Assert.Contains("repeated empty or no-tool turns", run.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(updates, update => update is TimelineEntryUpserted timeline &&
                                           timeline.Entry.Kind == TimelineItemKind.SystemNotice &&
                                           timeline.Entry.Content.Contains("Call exactly one tool", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RunPromptAsync_ContinuesAfterFailedToolCall()
    {
        var repository = new InMemoryRepository();
        var browserManager = new FakeBrowserManager();
        var secretStore = new FakeSecretStore();
        var toolExecutor = new SequencedToolExecutor(
            ToolExecutionResult.Failed("Open browser failed.", "Chrome was already closed."),
            ToolExecutionResult.Successful("Browser opened."));
        var llmClient = new FakeLlmClient(
            [
                new LlmToolCallDelta(0, "call_1", "open_browser", "{\"url\":\"https://www.google.com\"}"),
                new LlmStreamCompleted("tool_calls"),
            ],
            [
                new LlmToolCallDelta(0, "call_2", "open_browser", "{\"url\":\"https://www.google.com\"}"),
                new LlmStreamCompleted("tool_calls"),
            ],
            [
                new LlmTextDelta("Recovered after the browser error."),
                new LlmStreamCompleted("stop"),
            ]);

        var settings = AppSettings.CreateDefault(Path.GetTempPath());
        settings.MaxToolIterations = 3;

        var orchestrator = new ChatOrchestrator(
            repository,
            llmClient,
            new FixedToolRegistry(),
            toolExecutor,
            browserManager,
            secretStore,
            settings);

        await orchestrator.InitializeAsync(CancellationToken.None);
        var chat = await orchestrator.CreateChatAsync("Retry", CancellationToken.None);

        var updates = new List<OrchestratorUpdate>();
        await orchestrator.RunPromptAsync(chat.Id, "Open Google", updates.Add, CancellationToken.None);

        Assert.Equal(3, llmClient.RequestCount);
        Assert.Contains(updates, update => update is TimelineEntryUpserted timeline && timeline.Entry.Kind == TimelineItemKind.ToolCallFinished && timeline.Entry.Content.Contains("Open browser failed."));
        Assert.Contains(updates, update => update is TimelineEntryUpserted timeline && timeline.Entry.Kind == TimelineItemKind.SystemNotice && timeline.Entry.Content.Contains("failed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(updates, update => update is TimelineEntryUpserted timeline && timeline.Entry.Kind == TimelineItemKind.ToolCallFinished && timeline.Entry.Content.Contains("Browser opened."));
    }

    [Fact]
    public void ToolRegistry_LocatorToolsRequireLocatorArgument()
    {
        var registry = new ToolRegistry();
        var findElement = registry.GetToolDefinitions().Single(definition => definition.Name == "find_element");
        var click = registry.GetToolDefinitions().Single(definition => definition.Name == "click");

        Assert.Contains(findElement.Parameters["required"]!.AsArray(), item => item?.GetValue<string>() == "locator");
        Assert.Contains(click.Parameters["required"]!.AsArray(), item => item?.GetValue<string>() == "locator");
    }

    [Fact]
    public void ToolRegistry_ExecuteJavascriptArgumentsArrayDefinesItemsSchema()
    {
        var registry = new ToolRegistry();
        var executeJavascript = registry.GetToolDefinitions().Single(definition => definition.Name == "execute_javascript");

        var argumentsSchema = executeJavascript.Parameters["properties"]?["arguments"]?.AsObject();
        var itemOptions = argumentsSchema?["items"]?["anyOf"]?.AsArray();

        Assert.NotNull(argumentsSchema);
        Assert.Equal("array", argumentsSchema!["type"]?.GetValue<string>());
        Assert.NotNull(argumentsSchema["items"]);
        Assert.NotNull(itemOptions);
        Assert.DoesNotContain(itemOptions!, candidate => string.Equals(candidate?["type"]?.GetValue<string>(), "object", StringComparison.Ordinal));
        Assert.DoesNotContain(itemOptions!, candidate => string.Equals(candidate?["type"]?.GetValue<string>(), "array", StringComparison.Ordinal));
    }

    [Fact]
    public void ToolRegistry_AllSchemasProvideItemsForEveryArray()
    {
        var registry = new ToolRegistry();

        foreach (var definition in registry.GetToolDefinitions())
        {
            AssertSchemaArraysDefineItems(definition.Parameters, $"{definition.Name}.parameters");
        }
    }

    [Fact]
    public void ToolArgumentValidator_RejectsNonPrimitiveExecuteJavascriptArguments()
    {
        var registry = new ToolRegistry();
        var definition = registry.GetToolDefinitions().Single(candidate => candidate.Name == "execute_javascript");
        var arguments = new JsonObject
        {
            ["script"] = "return arguments[0];",
            ["arguments"] = new JsonArray
            {
                new JsonObject
                {
                    ["unexpected"] = true,
                },
            },
        };

        var validation = ToolArgumentValidator.Validate(definition, arguments);

        Assert.NotNull(validation);
        Assert.Contains("arguments[0]", validation!.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolArgumentValidator_ReturnsStructuredHintForFlatLocatorArguments()
    {
        var registry = new ToolRegistry();
        var definition = registry.GetToolDefinitions().Single(candidate => candidate.Name == "find_element");
        var arguments = new JsonObject
        {
            ["strategy"] = "name",
            ["value"] = "q",
        };

        var validation = ToolArgumentValidator.Validate(definition, arguments);

        Assert.NotNull(validation);
        Assert.Contains("Missing required argument `locator`.", validation!.Error);
        Assert.Equal("Wrap `strategy` and `value` inside a top-level `locator` object.", validation.Hint!.Message);
        Assert.Equal("name", validation.Hint.NormalizedArguments!["locator"]!["strategy"]!.GetValue<string>());
        Assert.Equal("q", validation.Hint.NormalizedArguments["locator"]!["value"]!.GetValue<string>());
        Assert.NotNull(validation.ExampleArguments?["locator"]);
    }

    [Fact]
    public async Task ToolExecutor_NormalizesLegacyLocatorArgumentsBeforeBrowserExecution()
    {
        var repository = new InMemoryRepository();
        var browserManager = new FakeBrowserManager();
        var goalService = new GoalService(repository);
        var secretStore = new FakeSecretStore();
        var toolExecutor = new ToolExecutor(browserManager, goalService, repository, secretStore, new ToolRegistry());
        await repository.InitializeAsync(CancellationToken.None);
        var chat = await repository.CreateChatAsync("Normalization", CancellationToken.None);
        var run = await repository.CreateRunAsync(chat.Id, "Find an element", CancellationToken.None);
        var context = new ToolInvocationContext
        {
            ChatSessionId = chat.Id,
            TestRunId = run.Id,
            LaunchHeadless = true,
            BrowserSnapshot = run.BrowserSnapshot,
        };

        var result = await toolExecutor.ExecuteAsync(
            context,
            "find_element",
            new JsonObject
            {
                ["strategy"] = "name",
                ["value"] = "q",
            },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.NormalizedArguments);
        Assert.Single(browserManager.ExecutedTools);
        Assert.Equal("find_element", browserManager.ExecutedTools[0].ToolName);
        Assert.Equal("name", browserManager.ExecutedTools[0].Arguments["locator"]!["strategy"]!.GetValue<string>());
        Assert.Equal("q", browserManager.ExecutedTools[0].Arguments["locator"]!["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task ToolExecutor_NormalizesStringifiedLocatorObjectBeforeBrowserExecution()
    {
        var repository = new InMemoryRepository();
        var browserManager = new FakeBrowserManager();
        var goalService = new GoalService(repository);
        var secretStore = new FakeSecretStore();
        var toolExecutor = new ToolExecutor(browserManager, goalService, repository, secretStore, new ToolRegistry());
        await repository.InitializeAsync(CancellationToken.None);
        var chat = await repository.CreateChatAsync("Stringified Locator", CancellationToken.None);
        var run = await repository.CreateRunAsync(chat.Id, "Find an element", CancellationToken.None);
        var context = new ToolInvocationContext
        {
            ChatSessionId = chat.Id,
            TestRunId = run.Id,
            LaunchHeadless = true,
            BrowserSnapshot = run.BrowserSnapshot,
        };

        var result = await toolExecutor.ExecuteAsync(
            context,
            "find_element",
            new JsonObject
            {
                ["locator"] = "{\"strategy\":\"name\",\"value\":\"q\"}",
            },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.NormalizedArguments);
        Assert.Single(browserManager.ExecutedTools);
        Assert.Equal("find_element", browserManager.ExecutedTools[0].ToolName);
        Assert.Equal("name", browserManager.ExecutedTools[0].Arguments["locator"]!["strategy"]!.GetValue<string>());
        Assert.Equal("q", browserManager.ExecutedTools[0].Arguments["locator"]!["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task ToolExecutor_NormalizesStringifiedLocatorObjectForTypeText()
    {
        var repository = new InMemoryRepository();
        var browserManager = new FakeBrowserManager();
        var goalService = new GoalService(repository);
        var secretStore = new FakeSecretStore();
        var toolExecutor = new ToolExecutor(browserManager, goalService, repository, secretStore, new ToolRegistry());
        await repository.InitializeAsync(CancellationToken.None);
        var chat = await repository.CreateChatAsync("Stringified TypeText", CancellationToken.None);
        var run = await repository.CreateRunAsync(chat.Id, "Type into an element", CancellationToken.None);
        var context = new ToolInvocationContext
        {
            ChatSessionId = chat.Id,
            TestRunId = run.Id,
            LaunchHeadless = true,
            BrowserSnapshot = run.BrowserSnapshot,
        };

        var result = await toolExecutor.ExecuteAsync(
            context,
            "type_text",
            new JsonObject
            {
                ["locator"] = "{\"strategy\":\"name\",\"value\":\"q\"}",
                ["text"] = "what is the time",
                ["clear_first"] = true,
            },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.NormalizedArguments);
        Assert.Single(browserManager.ExecutedTools);
        Assert.Equal("type_text", browserManager.ExecutedTools[0].ToolName);
        Assert.Equal("name", browserManager.ExecutedTools[0].Arguments["locator"]!["strategy"]!.GetValue<string>());
        Assert.Equal("q", browserManager.ExecutedTools[0].Arguments["locator"]!["value"]!.GetValue<string>());
        Assert.Equal("what is the time", browserManager.ExecutedTools[0].Arguments["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task RunPromptAsync_IncludesGoalUpdatesAndSystemNoticesInFollowUpRequests()
    {
        var repository = new InMemoryRepository();
        var browserManager = new FakeBrowserManager();
        var goalService = new GoalService(repository);
        var secretStore = new FakeSecretStore();
        var toolExecutor = new ToolExecutor(browserManager, goalService, repository, secretStore, new ToolRegistry());
        var llmClient = new FakeLlmClient(
            [
                new LlmToolCallDelta(0, "call_1", "create_goal", "{\"title\":\"Check time\",\"success_criteria\":\"A visible time is reported\"}"),
                new LlmStreamCompleted("tool_calls"),
            ],
            [
                new LlmTextDelta("I still need to work on the goal."),
                new LlmStreamCompleted("stop"),
            ],
            [
                new LlmTextDelta("Still thinking."),
                new LlmStreamCompleted("stop"),
            ]);

        var settings = AppSettings.CreateDefault(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        settings.MaxToolIterations = 3;

        var orchestrator = new ChatOrchestrator(
            repository,
            llmClient,
            new ToolRegistry(),
            toolExecutor,
            browserManager,
            secretStore,
            settings);

        await orchestrator.InitializeAsync(CancellationToken.None);
        var chat = await orchestrator.CreateChatAsync("Follow-up", CancellationToken.None);

        await orchestrator.RunPromptAsync(chat.Id, "Check the time on Google", _ => { }, CancellationToken.None);

        var secondRequestMessages = llmClient.Requests[1].Messages;
        Assert.Contains(secondRequestMessages, message => message.Role == "system" && message.Content!.Contains("Goal update:", StringComparison.Ordinal));
        Assert.Contains(llmClient.Requests[2].Messages, message => message.Role == "system" && message.Content!.Contains("System notice: Goals are still unresolved.", StringComparison.Ordinal));

        var assistantToolCallIndex = secondRequestMessages
            .Select((message, index) => new { message, index })
            .Single(item => item.message.Role == "assistant" && item.message.ToolCalls is { Count: > 0 })
            .index;
        var toolResponseIndex = secondRequestMessages
            .Select((message, index) => new { message, index })
            .Single(item => item.message.Role == "tool")
            .index;
        var goalUpdateIndex = secondRequestMessages
            .Select((message, index) => new { message, index })
            .Single(item => item.message.Role == "system" && item.message.Content!.Contains("Goal update:", StringComparison.Ordinal))
            .index;

        Assert.Equal(assistantToolCallIndex + 1, toolResponseIndex);
        Assert.True(goalUpdateIndex > toolResponseIndex);
    }

    [Fact]
    public async Task RunPromptAsync_DetectsRepeatedToolFailureLoopAndFailsEarly()
    {
        var repository = new InMemoryRepository();
        var browserManager = new FakeBrowserManager();
        var secretStore = new FakeSecretStore();
        var toolExecutor = new SequencedToolExecutor(
            ToolExecutionResult.Failed("Tool `find_element` received invalid arguments.", "Missing required argument `locator`.", hint: "Wrap `strategy` and `value` inside a top-level `locator` object.", exampleArguments: new JsonObject
            {
                ["locator"] = new JsonObject
                {
                    ["strategy"] = "name",
                    ["value"] = "q",
                },
            }),
            ToolExecutionResult.Failed("Tool `find_element` received invalid arguments.", "Missing required argument `locator`.", hint: "Wrap `strategy` and `value` inside a top-level `locator` object.", exampleArguments: new JsonObject
            {
                ["locator"] = new JsonObject
                {
                    ["strategy"] = "name",
                    ["value"] = "q",
                },
            }),
            ToolExecutionResult.Failed("Tool `find_element` received invalid arguments.", "Missing required argument `locator`.", hint: "Wrap `strategy` and `value` inside a top-level `locator` object.", exampleArguments: new JsonObject
            {
                ["locator"] = new JsonObject
                {
                    ["strategy"] = "name",
                    ["value"] = "q",
                },
            }));
        var llmClient = new FakeLlmClient(
            [
                new LlmToolCallDelta(0, "call_1", "find_element", "{\"strategy\":\"name\",\"value\":\"q\"}"),
                new LlmStreamCompleted("tool_calls"),
            ],
            [
                new LlmToolCallDelta(0, "call_2", "find_element", "{\"strategy\":\"name\",\"value\":\"q\"}"),
                new LlmStreamCompleted("tool_calls"),
            ],
            [
                new LlmToolCallDelta(0, "call_3", "find_element", "{\"strategy\":\"name\",\"value\":\"q\"}"),
                new LlmStreamCompleted("tool_calls"),
            ],
            [
                new LlmTextDelta("This fourth turn should never happen."),
                new LlmStreamCompleted("stop"),
            ]);

        var settings = AppSettings.CreateDefault(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        settings.MaxToolIterations = 10;

        var orchestrator = new ChatOrchestrator(
            repository,
            llmClient,
            new FixedToolRegistry(),
            toolExecutor,
            browserManager,
            secretStore,
            settings);

        await orchestrator.InitializeAsync(CancellationToken.None);
        var chat = await orchestrator.CreateChatAsync("Loop", CancellationToken.None);

        var updates = new List<OrchestratorUpdate>();
        var run = await orchestrator.RunPromptAsync(chat.Id, "Search Google for the time", updates.Add, CancellationToken.None);

        Assert.Equal(TestRunStatus.Failed, run.Status);
        Assert.Contains("identical `find_element` failures", run.FailureReason);
        Assert.Equal(3, llmClient.RequestCount);
        Assert.Contains(updates, update => update is TimelineEntryUpserted timeline &&
                                           timeline.Entry.Kind == TimelineItemKind.SystemNotice &&
                                           timeline.Entry.Content.Contains("Do not repeat the same call", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunPromptAsync_FollowUpPromptIncludesLocatorShapeAndRecoveryGuidanceAfterMalformedCall()
    {
        var repository = new InMemoryRepository();
        var browserManager = new FakeBrowserManager();
        var secretStore = new FakeSecretStore();
        var toolExecutor = new SequencedToolExecutor(
            ToolExecutionResult.Failed(
                "Tool `find_element` received invalid arguments.",
                "Missing required argument `locator`.",
                hint: "Wrap `strategy` and `value` inside a top-level `locator` object.",
                exampleArguments: new JsonObject
                {
                    ["locator"] = new JsonObject
                    {
                        ["strategy"] = "name",
                        ["value"] = "q",
                    },
                }));
        var llmClient = new FakeLlmClient(
            [
                new LlmToolCallDelta(0, "call_1", "find_element", "{\"strategy\":\"name\",\"value\":\"q\"}"),
                new LlmStreamCompleted("tool_calls"),
            ],
            [
                new LlmTextDelta("I will recover."),
                new LlmStreamCompleted("stop"),
            ]);

        var settings = AppSettings.CreateDefault(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        settings.MaxToolIterations = 2;

        var orchestrator = new ChatOrchestrator(
            repository,
            llmClient,
            new FixedToolRegistry(),
            toolExecutor,
            browserManager,
            secretStore,
            settings);

        await orchestrator.InitializeAsync(CancellationToken.None);
        var chat = await orchestrator.CreateChatAsync("Google Time", CancellationToken.None);

        await orchestrator.RunPromptAsync(chat.Id, "Search Google for what is the time", _ => { }, CancellationToken.None);

        var followUpSystemPrompt = llmClient.Requests[1].Messages[0].Content;
        Assert.NotNull(followUpSystemPrompt);
        Assert.Contains("{\"locator\":{\"strategy\":\"css\",\"value\":\"input[name='q']\"}}", followUpSystemPrompt);
        Assert.Contains("Do not repeat the same tool name with the same arguments.", followUpSystemPrompt);
        Assert.Contains(llmClient.Requests[1].Messages, message =>
            message.Role == "system" &&
            message.Content!.Contains("change the argument shape", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GoalService_TransitionsGoalAndStoresEvidence()
    {
        var repository = new InMemoryRepository();
        var goalService = new GoalService(repository);
        await repository.InitializeAsync(CancellationToken.None);
        var chat = await repository.CreateChatAsync("Goals", CancellationToken.None);
        var run = await repository.CreateRunAsync(chat.Id, "Prompt", CancellationToken.None);

        var goal = await goalService.CreateGoalAsync(chat.Id, run.Id, "Add item", "Cart count increments", CancellationToken.None);
        var updated = await goalService.UpdateGoalStatusAsync(chat.Id, run.Id, goal.Id, GoalStatus.Passed, "Observed badge", "Cart badge showed 1", CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal(GoalStatus.Passed, updated!.Status);
        Assert.Equal("Observed badge", updated.Note);
        Assert.Equal("Cart badge showed 1", updated.Evidence);
    }

    [Fact]
    public async Task SqliteRepository_LoadsChatWithoutNestedReaderFailure()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "BrowserTestingTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var settings = AppSettings.CreateDefault(tempRoot);
        var repository = new SqliteChatRepository(settings);
        await repository.InitializeAsync(CancellationToken.None);

        var chat = await repository.CreateChatAsync("SQLite Chat", CancellationToken.None);
        var run = await repository.CreateRunAsync(chat.Id, "Open homepage", CancellationToken.None);
        await repository.AddGoalAsync(
            new GoalItem
            {
                Id = Guid.NewGuid(),
                TestRunId = run.Id,
                Title = "Verify page",
                SuccessCriteria = "The page title is visible",
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            },
            CancellationToken.None);
        await repository.AddTimelineEntryAsync(
            new TimelineEntry
            {
                Id = Guid.NewGuid(),
                ChatSessionId = chat.Id,
                TestRunId = run.Id,
                Sequence = 1,
                Kind = TimelineItemKind.AssistantMessage,
                Role = "assistant",
                Content = "Loaded the page.",
                CreatedAtUtc = DateTime.UtcNow,
            },
            CancellationToken.None);

        var loaded = await repository.GetChatAsync(chat.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Single(loaded!.Runs);
        Assert.Single(loaded.Runs[0].Goals);
        Assert.Single(loaded.Timeline);
        Assert.Equal("Loaded the page.", loaded.Timeline[0].Content);
    }

    [Fact]
    public async Task LoadChatAsync_RestoresOnlyMostRecentRestorableRun()
    {
        var repository = new InMemoryRepository();
        var browserManager = new FakeBrowserManager();
        var secretStore = new FakeSecretStore();
        var orchestrator = new ChatOrchestrator(
            repository,
            new FakeLlmClient(Array.Empty<LlmStreamEvent>()),
            new FixedToolRegistry(),
            new FakeToolExecutor(),
            browserManager,
            secretStore,
            AppSettings.CreateDefault(Path.GetTempPath()));

        await orchestrator.InitializeAsync(CancellationToken.None);
        var chat = await repository.CreateChatAsync("Restore", CancellationToken.None);
        var firstRun = await repository.CreateRunAsync(chat.Id, "First", CancellationToken.None);
        var secondRun = await repository.CreateRunAsync(chat.Id, "Second", CancellationToken.None);

        firstRun.BrowserSnapshot = new BrowserSessionSnapshot
        {
            TestRunId = firstRun.Id,
            CurrentUrl = "https://example.com/first",
            ProfilePath = "first-profile",
        };
        secondRun.BrowserSnapshot = new BrowserSessionSnapshot
        {
            TestRunId = secondRun.Id,
            CurrentUrl = "https://example.com/second",
            ProfilePath = "second-profile",
        };
        await repository.UpdateRunAsync(firstRun, CancellationToken.None);
        await repository.UpdateRunAsync(secondRun, CancellationToken.None);

        await orchestrator.LoadChatAsync(chat.Id, restoreBrowser: true, onUpdate: null, CancellationToken.None);

        Assert.Single(browserManager.RestoreRequests);
        Assert.Equal(secondRun.Id, browserManager.RestoreRequests[0]);
    }

    [Fact]
    public async Task RunPromptAsync_ReusesInitialConnectionSettingsAcrossTurns()
    {
        var repository = new InMemoryRepository();
        var browserManager = new FakeBrowserManager();
        var goalService = new GoalService(repository);
        var secretStore = new FakeSecretStore();
        var toolExecutor = new ToolExecutor(browserManager, goalService, repository, secretStore, new ToolRegistry());
        var settings = AppSettings.CreateDefault(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        settings.Provider = LlmProvider.Local;
        settings.LocalModelName = "local-before";
        settings.MaxToolIterations = 2;

        var llmClient = new FakeLlmClient(
            (request, requestCount) =>
            {
                if (requestCount == 1)
                {
                    settings.Provider = LlmProvider.OpenAi;
                    settings.OpenAiModelName = "openai-after";
                    settings.OpenAiApiKey = "sk-after";
                }
            },
            [
                new LlmToolCallDelta(0, "call_1", "create_goal", "{\"title\":\"Check time\",\"success_criteria\":\"Goal is completed\"}"),
                new LlmStreamCompleted("tool_calls"),
            ],
            [
                new LlmTextDelta("I changed settings mid-run, but this request should still use the original connection."),
                new LlmStreamCompleted("stop"),
            ]);

        var orchestrator = new ChatOrchestrator(
            repository,
            llmClient,
            new ToolRegistry(),
            toolExecutor,
            browserManager,
            secretStore,
            settings);

        await orchestrator.InitializeAsync(CancellationToken.None);
        var chat = await orchestrator.CreateChatAsync("Connection Snapshot", CancellationToken.None);

        await orchestrator.RunPromptAsync(chat.Id, "Check the page", _ => { }, CancellationToken.None);

        Assert.Equal(2, llmClient.RequestCount);
        Assert.All(llmClient.Requests, request =>
        {
            Assert.Equal(LlmProvider.Local, request.Connection.Provider);
            Assert.Equal("local-before", request.Connection.Model);
            Assert.Null(request.Connection.ApiKey);
            Assert.Equal(AppSettings.LocalServerBaseUrl, request.Connection.BaseUrl);
        });
    }

    [Fact]
    public async Task RunPromptAsync_UsesDeveloperRoleForOpenAiRequests()
    {
        var repository = new InMemoryRepository();
        var browserManager = new FakeBrowserManager();
        var secretStore = new FakeSecretStore();
        var toolExecutor = new FakeToolExecutor();
        var settings = AppSettings.CreateDefault(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        settings.Provider = LlmProvider.OpenAi;
        settings.OpenAiModelName = "gpt-4o-mini";
        settings.OpenAiApiKey = "sk-test";

        var llmClient = new FakeLlmClient(
            [
                new LlmTextDelta("OpenAI request accepted."),
                new LlmStreamCompleted("stop"),
            ]);

        var orchestrator = new ChatOrchestrator(
            repository,
            llmClient,
            new FixedToolRegistry(),
            toolExecutor,
            browserManager,
            secretStore,
            settings);

        await orchestrator.InitializeAsync(CancellationToken.None);
        var chat = await orchestrator.CreateChatAsync("OpenAI Roles", CancellationToken.None);

        await orchestrator.RunPromptAsync(chat.Id, "Say hello", _ => { }, CancellationToken.None);

        Assert.Equal("developer", llmClient.Requests[0].Messages[0].Role);
    }

    private sealed class FixedToolRegistry : IToolRegistry
    {
        public IReadOnlyList<LlmToolDefinition> GetToolDefinitions() =>
        [
            new()
            {
                Name = "create_goal",
                Description = "Create a goal.",
                Parameters = new JsonObject(),
            },
            new()
            {
                Name = "list_goals",
                Description = "List goals.",
                Parameters = new JsonObject(),
            },
            new()
            {
                Name = "open_browser",
                Description = "Open browser.",
                Parameters = new JsonObject(),
            },
            new()
            {
                Name = "find_element",
                Description = "Find an element.",
                Parameters = new JsonObject(),
            },
        ];
    }

    private static void AssertSchemaArraysDefineItems(JsonNode? node, string path)
    {
        switch (node)
        {
            case null:
                return;

            case JsonObject obj:
                if (string.Equals(obj["type"]?.GetValue<string>(), "array", StringComparison.Ordinal))
                {
                    Assert.True(obj["items"] is JsonObject, $"Schema array at {path} must define an object-valued items schema.");
                }

                foreach (var property in obj)
                {
                    AssertSchemaArraysDefineItems(property.Value, $"{path}.{property.Key}");
                }

                break;

            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                {
                    AssertSchemaArraysDefineItems(array[index], $"{path}[{index}]");
                }

                break;
        }
    }

    private sealed class FakeLlmClient : ILlmClient
    {
        private readonly IReadOnlyList<LlmStreamEvent>[] responses;
        private readonly Action<LlmRequest, int>? onRequest;
        private int requestCount;

        public FakeLlmClient(params IReadOnlyList<LlmStreamEvent>[] responses)
            : this(onRequest: null, responses)
        {
        }

        public FakeLlmClient(Action<LlmRequest, int>? onRequest, params IReadOnlyList<LlmStreamEvent>[] responses)
        {
            this.onRequest = onRequest;
            this.responses = responses;
        }

        public int RequestCount => requestCount;
        public List<LlmRequest> Requests { get; } = [];

        public async IAsyncEnumerable<LlmStreamEvent> StreamCompletionAsync(LlmRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var responseIndex = Interlocked.Increment(ref requestCount) - 1;
            onRequest?.Invoke(request, responseIndex + 1);
            var response = responseIndex < responses.Length ? responses[responseIndex] : [];

            foreach (var item in response)
            {
                await Task.Yield();
                yield return item;
            }
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(LlmConnectionSettings connection, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class FakeToolExecutor : IToolExecutor
    {
        public Task<ToolExecutionResult> ExecuteAsync(ToolInvocationContext context, string toolName, JsonObject arguments, CancellationToken cancellationToken) =>
            Task.FromResult(ToolExecutionResult.Successful("Tool executed.", new JsonObject { ["tool"] = toolName }));
    }

    private sealed class SequencedToolExecutor(params ToolExecutionResult[] results) : IToolExecutor
    {
        private readonly Queue<ToolExecutionResult> remaining = new(results);

        public Task<ToolExecutionResult> ExecuteAsync(ToolInvocationContext context, string toolName, JsonObject arguments, CancellationToken cancellationToken) =>
            Task.FromResult(remaining.Count > 0
                ? remaining.Dequeue()
                : ToolExecutionResult.Successful("Tool executed.", new JsonObject { ["tool"] = toolName }));
    }

    private sealed class FakeBrowserManager : IBrowserSessionManager
    {
        public List<Guid> RestoreRequests { get; } = [];
        public List<(string ToolName, JsonObject Arguments)> ExecutedTools { get; } = [];

        public Task CloseBrowserAsync(Guid testRunId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ToolExecutionResult> ExecuteBrowserToolAsync(Guid testRunId, string toolName, JsonObject arguments, BrowserSessionSnapshot? persistedSnapshot, bool headless, CancellationToken cancellationToken)
        {
            ExecutedTools.Add((toolName, (JsonObject)arguments.DeepClone()));
            return Task.FromResult(ToolExecutionResult.Successful(toolName));
        }
        public Task<BrowserSessionSnapshot?> GetSnapshotAsync(Guid testRunId, CancellationToken cancellationToken) => Task.FromResult<BrowserSessionSnapshot?>(new BrowserSessionSnapshot { TestRunId = testRunId });
        public Task<BrowserSessionSnapshot> OpenBrowserAsync(Guid testRunId, string? startUrl, string profilePath, bool headless, CancellationToken cancellationToken) => Task.FromResult(new BrowserSessionSnapshot { TestRunId = testRunId, CurrentUrl = startUrl, ProfilePath = profilePath });
        public Task<BrowserSessionSnapshot> RestoreBrowserAsync(Guid testRunId, BrowserSessionSnapshot snapshot, bool headless, CancellationToken cancellationToken)
        {
            RestoreRequests.Add(testRunId);
            return Task.FromResult(snapshot);
        }
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> secrets = [];
        public Task<string?> GetSecretAsync(Guid chatId, string name, CancellationToken cancellationToken) => Task.FromResult(secrets.TryGetValue(name, out var value) ? value : null);
        public Task<IReadOnlyList<string>> ListSecretNamesAsync(Guid chatId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>(secrets.Keys.ToList());
        public Task SaveSecretAsync(Guid chatId, string name, string value, CancellationToken cancellationToken)
        {
            secrets[name] = value;
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryRepository : IChatRepository
    {
        private readonly Dictionary<Guid, ChatSession> chats = [];
        private readonly Dictionary<Guid, TestRun> runs = [];
        private readonly Dictionary<Guid, List<GoalItem>> goalsByRun = [];
        private readonly Dictionary<Guid, List<TimelineEntry>> timelineByChat = [];
        private readonly Dictionary<(Guid ChatId, string Name), string> secrets = [];

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<ChatSessionSummary>> ListChatsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ChatSessionSummary>>(chats.Values.Select(chat => new ChatSessionSummary
            {
                Id = chat.Id,
                Title = chat.Title,
                UpdatedAtUtc = chat.UpdatedAtUtc,
                ActiveRuns = chat.Runs.Count,
            }).ToList());

        public Task<ChatSession> CreateChatAsync(string? title, CancellationToken cancellationToken)
        {
            var chat = new ChatSession
            {
                Id = Guid.NewGuid(),
                Title = title ?? "New Chat",
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            };
            chats[chat.Id] = chat;
            timelineByChat[chat.Id] = [];
            return Task.FromResult(chat);
        }

        public Task<ChatSession?> GetChatAsync(Guid chatId, CancellationToken cancellationToken)
        {
            if (!chats.TryGetValue(chatId, out var chat))
            {
                return Task.FromResult<ChatSession?>(null);
            }

            chat.Runs = runs.Values.Where(run => run.ChatSessionId == chatId).OrderBy(run => run.CreatedAtUtc).ToList();
            foreach (var run in chat.Runs)
            {
                run.Goals = goalsByRun.TryGetValue(run.Id, out var goals) ? goals.ToList() : [];
            }

            chat.Timeline = timelineByChat[chatId].OrderBy(entry => entry.Sequence).ToList();
            return Task.FromResult<ChatSession?>(chat);
        }

        public Task UpdateChatAsync(ChatSession chat, CancellationToken cancellationToken)
        {
            chats[chat.Id] = chat;
            return Task.CompletedTask;
        }

        public Task<TestRun> CreateRunAsync(Guid chatId, string userPrompt, CancellationToken cancellationToken)
        {
            var run = new TestRun
            {
                Id = Guid.NewGuid(),
                ChatSessionId = chatId,
                UserPrompt = userPrompt,
                BrowserSnapshot = new BrowserSessionSnapshot { TestRunId = Guid.NewGuid() },
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            };
            run.BrowserSnapshot.TestRunId = run.Id;
            runs[run.Id] = run;
            goalsByRun[run.Id] = [];
            return Task.FromResult(run);
        }

        public Task UpdateRunAsync(TestRun run, CancellationToken cancellationToken)
        {
            runs[run.Id] = run;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<GoalItem>> ListGoalsAsync(Guid runId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GoalItem>>(goalsByRun.TryGetValue(runId, out var goals) ? goals.ToList() : []);

        public Task<GoalItem> AddGoalAsync(GoalItem goal, CancellationToken cancellationToken)
        {
            goalsByRun[goal.TestRunId].Add(goal);
            return Task.FromResult(goal);
        }

        public Task UpdateGoalAsync(GoalItem goal, CancellationToken cancellationToken)
        {
            var items = goalsByRun[goal.TestRunId];
            var index = items.FindIndex(candidate => candidate.Id == goal.Id);
            items[index] = goal;
            return Task.CompletedTask;
        }

        public Task<TimelineEntry> AddTimelineEntryAsync(TimelineEntry entry, CancellationToken cancellationToken)
        {
            if (entry.Sequence <= 0)
            {
                entry.Sequence = timelineByChat[entry.ChatSessionId].Count + 1;
            }

            timelineByChat[entry.ChatSessionId].Add(entry);
            return Task.FromResult(entry);
        }

        public Task UpdateTimelineEntryAsync(TimelineEntry entry, CancellationToken cancellationToken)
        {
            var items = timelineByChat[entry.ChatSessionId];
            var index = items.FindIndex(candidate => candidate.Id == entry.Id);
            if (index >= 0)
            {
                items[index] = entry;
            }

            return Task.CompletedTask;
        }

        public Task<long> GetNextSequenceAsync(Guid chatId, CancellationToken cancellationToken) =>
            Task.FromResult((long)(timelineByChat[chatId].Count + 1));

        public Task SaveBrowserSnapshotAsync(Guid runId, BrowserSessionSnapshot snapshot, CancellationToken cancellationToken)
        {
            runs[runId].BrowserSnapshot = snapshot;
            return Task.CompletedTask;
        }

        public Task SaveSecretAsync(Guid chatId, string name, string encryptedValue, CancellationToken cancellationToken)
        {
            secrets[(chatId, name)] = encryptedValue;
            return Task.CompletedTask;
        }

        public Task<string?> GetSecretAsync(Guid chatId, string name, CancellationToken cancellationToken) =>
            Task.FromResult(secrets.TryGetValue((chatId, name), out var value) ? value : null);

        public Task<IReadOnlyList<string>> ListSecretNamesAsync(Guid chatId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(secrets.Keys.Where(item => item.ChatId == chatId).Select(item => item.Name).ToList());
    }
}
