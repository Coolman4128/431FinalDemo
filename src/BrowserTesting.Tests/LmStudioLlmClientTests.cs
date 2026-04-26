using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BrowserTesting.Core.Llm;
using BrowserTesting.Core.Models;
using BrowserTesting.Infrastructure.Llm;
using BrowserTesting.Infrastructure.Tools;
using Xunit;

namespace BrowserTesting.Tests;

public sealed class LmStudioLlmClientTests
{
    [Fact]
    public async Task OpenAiPayloadUsesForcedToolChoiceStrictSchemasAndNoFixedTemperature()
    {
        var handler = new CapturingHandler();
        var client = new LmStudioLlmClient(new HttpClient(handler));
        var request = new LlmRequest
        {
            Connection = new LlmConnectionSettings
            {
                Provider = LlmProvider.OpenAi,
                BaseUrl = "https://api.openai.test/v1",
                Model = "gpt-5.4",
                ApiKey = "test",
                Temperature = 0.2d,
            },
            Messages = [new LlmConversationMessage { Role = "user", Content = "test" }],
            Tools = new ToolRegistry().GetToolDefinitions(),
            ToolChoiceMode = LlmToolChoiceMode.ForceFunction,
            ForcedToolName = "end_task",
            ParallelToolCalls = false,
        };

        await foreach (var _ in client.StreamCompletionAsync(request, CancellationToken.None))
        {
        }

        var payload = JsonNode.Parse(handler.Body)!.AsObject();
        Assert.False(payload.ContainsKey("temperature"));
        Assert.False(payload["parallel_tool_calls"]!.GetValue<bool>());
        Assert.Equal("end_task", payload["tool_choice"]!["function"]!["name"]!.GetValue<string>());

        var openBrowser = payload["tools"]!.AsArray()
            .Select(node => node!["function"]!.AsObject())
            .Single(function => function["name"]!.GetValue<string>() == "open_browser");
        Assert.True(openBrowser["strict"]!.GetValue<bool>());

        var parameters = openBrowser["parameters"]!.AsObject();
        Assert.False(parameters["additionalProperties"]!.GetValue<bool>());
        Assert.Equal(["url", "profile_name"], parameters["required"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray());
        Assert.Contains(
            parameters["properties"]!["url"]!["anyOf"]!.AsArray(),
            node => string.Equals(node!["type"]!.GetValue<string>(), "null", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReasoningContentIsNotReturnedAsVisibleText()
    {
        var handler = new CapturingHandler(
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"hidden\",\"content\":\"visible\"}}]}\n\n" +
            "data: [DONE]\n\n");
        var client = new LmStudioLlmClient(new HttpClient(handler));

        var request = new LlmRequest
        {
            Connection = new LlmConnectionSettings
            {
                Provider = LlmProvider.Local,
                BaseUrl = "http://local.test/v1",
                Model = "local-model",
            },
            Messages = [new LlmConversationMessage { Role = "user", Content = "test" }],
            Tools = [],
        };

        var text = new StringBuilder();
        await foreach (var streamEvent in client.StreamCompletionAsync(request, CancellationToken.None))
        {
            if (streamEvent is LlmTextDelta delta)
            {
                text.Append(delta.Content);
            }
        }

        Assert.Equal("visible", text.ToString());
    }

    private sealed class CapturingHandler(string? responseBody = null) : HttpMessageHandler
    {
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    responseBody ?? "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n",
                    Encoding.UTF8,
                    "text/event-stream"),
            };
        }
    }
}
