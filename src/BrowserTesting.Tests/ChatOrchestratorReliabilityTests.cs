using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using BrowserTesting.Core.Abstractions;
using BrowserTesting.Core.Llm;
using BrowserTesting.Core.Models;
using BrowserTesting.Core.Services;
using BrowserTesting.Infrastructure.Tools;
using Xunit;

namespace BrowserTesting.Tests;

public sealed class ChatOrchestratorReliabilityTests
{
    [Fact]
    public async Task ProseOnlyAfterTerminalGoalsForcesEndTaskInsteadOfCompletingEarly()
    {
        var repo = new InMemoryChatRepository();
        var chatId = Guid.NewGuid();
        var goalId = Guid.NewGuid();
        repo.AddChat(new ChatSession { Id = chatId, Title = "Existing" });
        repo.ConfigureCreatedRun = run => run.Goals.Add(new GoalItem
        {
            Id = goalId,
            TestRunId = run.Id,
            Title = "Already done",
            SuccessCriteria = "Terminal",
            Status = GoalStatus.Passed,
            Evidence = "Evidence",
        });

        var llm = new ScriptedLlmClient(
            [new LlmTextDelta("All done."), new LlmStreamCompleted("stop")],
            [ToolCall("end_task", EndTaskArgs()), new LlmStreamCompleted("tool_calls")]);
        var orchestrator = CreateOrchestrator(repo, llm, new DelegatingToolExecutor((_, toolName, _, _) =>
            Task.FromResult(ToolExecutionResult.Successful(toolName == "end_task" ? "Task ended." : "ok"))));

        var run = await orchestrator.RunPromptAsync(chatId, "finish this", _ => { }, CancellationToken.None);

        Assert.Equal(TestRunStatus.Completed, run.Status);
        Assert.Equal(2, llm.Requests.Count);
        Assert.All(llm.Requests, request =>
        {
            Assert.Equal(LlmToolChoiceMode.ForceFunction, request.ToolChoiceMode);
            Assert.Equal("end_task", request.ForcedToolName);
        });
    }

    [Fact]
    public async Task RepeatedIdenticalToolFailuresDoNotEndRunBeforeEndTask()
    {
        var repo = new InMemoryChatRepository();
        var chatId = Guid.NewGuid();
        var goalId = Guid.NewGuid();
        repo.AddChat(new ChatSession { Id = chatId, Title = "Existing" });
        repo.ConfigureCreatedRun = run => run.Goals.Add(new GoalItem
        {
            Id = goalId,
            TestRunId = run.Id,
            Title = "Checkout",
            SuccessCriteria = "Checkout completes",
            Status = GoalStatus.Pending,
        });

        var llm = new ScriptedLlmClient(
            [ToolCall("click", "{}"), new LlmStreamCompleted("tool_calls")],
            [ToolCall("click", "{}"), new LlmStreamCompleted("tool_calls")],
            [ToolCall("click", "{}"), new LlmStreamCompleted("tool_calls")],
            [ToolCall("mark_goal_fail", new JsonObject
            {
                ["goal_id"] = goalId.ToString(),
                ["reason"] = "Blocked",
                ["evidence"] = "The click failed repeatedly.",
            }.ToJsonString()), new LlmStreamCompleted("tool_calls")],
            [ToolCall("end_task", EndTaskArgs("failed")), new LlmStreamCompleted("tool_calls")]);

        var executor = new DelegatingToolExecutor(async (context, toolName, arguments, cancellationToken) =>
        {
            if (toolName == "click")
            {
                return ToolExecutionResult.Failed("Click failed.", "same failure");
            }

            if (toolName == "mark_goal_fail")
            {
                var chat = await repo.GetChatAsync(context.ChatSessionId, cancellationToken);
                var run = chat!.Runs.Single(candidate => candidate.Id == context.TestRunId);
                var goal = run.Goals.Single(candidate => candidate.Id == Guid.Parse(arguments["goal_id"]!.GetValue<string>()));
                goal.Status = GoalStatus.Failed;
                goal.Note = arguments["reason"]!.GetValue<string>();
                goal.Evidence = arguments["evidence"]!.GetValue<string>();
                await repo.UpdateRunAsync(run, cancellationToken);
                return ToolExecutionResult.Successful("Goal marked Failed.");
            }

            return ToolExecutionResult.Successful("Task ended.");
        });
        var orchestrator = CreateOrchestrator(repo, llm, executor, maxToolIterations: 2);

        var runResult = await orchestrator.RunPromptAsync(chatId, "checkout", _ => { }, CancellationToken.None);

        Assert.Equal(TestRunStatus.Failed, runResult.Status);
        Assert.True(llm.Requests.Count >= 5);
        Assert.Equal(LlmToolChoiceMode.ForceFunction, llm.Requests[^1].ToolChoiceMode);
        Assert.Equal("end_task", llm.Requests[^1].ForcedToolName);
    }

    [Fact]
    public async Task NewRunPromptDoesNotIncludeOldRunGoalIds()
    {
        var repo = new InMemoryChatRepository();
        var chatId = Guid.NewGuid();
        var oldGoalId = Guid.NewGuid();
        repo.AddChat(new ChatSession
        {
            Id = chatId,
            Title = "Existing",
            Runs =
            [
                new TestRun
                {
                    Id = Guid.NewGuid(),
                    ChatSessionId = chatId,
                    UserPrompt = "old run",
                    Status = TestRunStatus.Completed,
                    Goals =
                    [
                        new GoalItem
                        {
                            Id = oldGoalId,
                            TestRunId = Guid.NewGuid(),
                            Title = "Old goal",
                            SuccessCriteria = "Old",
                            Status = GoalStatus.Passed,
                            Evidence = "Old evidence",
                        },
                    ],
                },
            ],
        });

        var llm = new CapturingAndCancellingLlmClient();
        var orchestrator = CreateOrchestrator(repo, llm, new DelegatingToolExecutor((_, _, _, _) =>
            Task.FromResult(ToolExecutionResult.Successful("ok"))));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            orchestrator.RunPromptAsync(chatId, "new run", _ => { }, CancellationToken.None));

        var promptText = string.Join("\n", llm.Request!.Messages.Select(message => message.Content));
        Assert.DoesNotContain(oldGoalId.ToString(), promptText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActiveRunContextIncludesLastInspectPageRefs()
    {
        var repo = new InMemoryChatRepository();
        var chatId = Guid.NewGuid();
        repo.AddChat(new ChatSession { Id = chatId, Title = "Existing" });
        repo.AfterCreateRun = (chat, run) =>
        {
            chat.Timeline.Add(new TimelineEntry
            {
                Id = Guid.NewGuid(),
                ChatSessionId = chat.Id,
                TestRunId = run.Id,
                Sequence = 10,
                Kind = TimelineItemKind.ToolCallFinished,
                Role = "tool",
                ToolName = "inspect_page",
                ToolCallId = "inspect-1",
                Content = "Page inspected.",
                MetadataJson = new JsonObject
                {
                    ["success"] = true,
                    ["summary"] = "Page inspected.",
                    ["data"] = new JsonObject
                    {
                        ["url"] = "https://www.saucedemo.com/",
                        ["elements"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["ref"] = "e1",
                                ["tag"] = "input",
                                ["id"] = "user-name",
                                ["name"] = "user-name",
                            },
                        },
                    },
                }.ToJsonString(),
            });
        };

        var llm = new CapturingAndCancellingLlmClient();
        var orchestrator = CreateOrchestrator(repo, llm, new DelegatingToolExecutor((_, _, _, _) =>
            Task.FromResult(ToolExecutionResult.Successful("ok"))));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            orchestrator.RunPromptAsync(chatId, "log in", _ => { }, CancellationToken.None));

        var promptText = string.Join("\n", llm.Request!.Messages.Select(message => message.Content));
        Assert.Contains("last_page_inspection", promptText, StringComparison.Ordinal);
        Assert.Contains("e1", promptText, StringComparison.Ordinal);
        Assert.Contains("user-name", promptText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestedGoalCountPreventsEarlyEndTaskForceWhenGoalsAreMissing()
    {
        var repo = new InMemoryChatRepository();
        var chatId = Guid.NewGuid();
        repo.AddChat(new ChatSession { Id = chatId, Title = "Existing" });
        repo.ConfigureCreatedRun = run => run.Goals.Add(new GoalItem
        {
            Id = Guid.NewGuid(),
            TestRunId = run.Id,
            Title = "Only one goal",
            SuccessCriteria = "Already resolved",
            Status = GoalStatus.Passed,
            Evidence = "Evidence",
        });

        var llm = new CapturingAndCancellingLlmClient();
        var orchestrator = CreateOrchestrator(repo, llm, new DelegatingToolExecutor((_, _, _, _) =>
            Task.FromResult(ToolExecutionResult.Successful("ok"))));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            orchestrator.RunPromptAsync(chatId, "I want to make 3 goals: one, two, three.", _ => { }, CancellationToken.None));

        Assert.Equal(LlmToolChoiceMode.Required, llm.Request!.ToolChoiceMode);
        Assert.Null(llm.Request.ForcedToolName);
        var promptText = string.Join("\n", llm.Request.Messages.Select(message => message.Content));
        Assert.Contains("\"expected_goal_count\":3", promptText, StringComparison.Ordinal);
        Assert.Contains("only 1 active-run goals exist", promptText, StringComparison.Ordinal);
    }

    private static ChatOrchestrator CreateOrchestrator(
        InMemoryChatRepository repository,
        ILlmClient llmClient,
        IToolExecutor toolExecutor,
        int maxToolIterations = 18) =>
        new(
            repository,
            llmClient,
            new ToolRegistry(),
            toolExecutor,
            new NullBrowserSessionManager(),
            new NoOpSecretStore(),
            new AppSettings
            {
                MaxToolIterations = maxToolIterations,
                LocalModelName = "test-model",
            });

    private static LlmToolCallDelta ToolCall(string name, string argumentsJson) =>
        new(0, Guid.NewGuid().ToString("N"), name, argumentsJson);

    private static string EndTaskArgs(string outcome = "completed") =>
        new JsonObject
        {
            ["outcome"] = outcome,
            ["summary"] = "Resolved.",
            ["evidence"] = "Recorded evidence.",
            ["remaining_work"] = "none",
        }.ToJsonString();

    private sealed class ScriptedLlmClient(params IReadOnlyList<LlmStreamEvent>[] responses) : ILlmClient
    {
        private int index;
        public List<LlmRequest> Requests { get; } = [];

        public async IAsyncEnumerable<LlmStreamEvent> StreamCompletionAsync(
            LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (index >= responses.Length)
            {
                throw new InvalidOperationException("No scripted LLM response remains.");
            }

            foreach (var streamEvent in responses[index++])
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return streamEvent;
            }
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(LlmConnectionSettings connection, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(["test-model"]);
    }

    private sealed class CapturingAndCancellingLlmClient : ILlmClient
    {
        public LlmRequest? Request { get; private set; }

        public async IAsyncEnumerable<LlmStreamEvent> StreamCompletionAsync(
            LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Request = request;
            await Task.Yield();
            throw new OperationCanceledException();
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(LlmConnectionSettings connection, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(["test-model"]);
    }

    private sealed class DelegatingToolExecutor(
        Func<ToolInvocationContext, string, JsonObject, CancellationToken, Task<ToolExecutionResult>> execute) : IToolExecutor
    {
        public Task<ToolExecutionResult> ExecuteAsync(ToolInvocationContext context, string toolName, JsonObject arguments, CancellationToken cancellationToken) =>
            execute(context, toolName, arguments, cancellationToken);
    }
}
