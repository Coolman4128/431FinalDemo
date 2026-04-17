using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BrowserTesting.Core.Abstractions;
using BrowserTesting.Core.Llm;
using BrowserTesting.Core.Models;

namespace BrowserTesting.Infrastructure.Llm;

public sealed class LmStudioLlmClient(HttpClient httpClient) : ILlmClient
{
    public async IAsyncEnumerable<LlmStreamEvent> StreamCompletionAsync(LlmRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var payload = BuildPayload(request);

        using var message = new HttpRequestMessage(HttpMethod.Post, $"{request.Connection.BaseUrl.TrimEnd('/')}/chat/completions");
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrWhiteSpace(request.Connection.ApiKey))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.Connection.ApiKey);
        }

        message.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (true)
        {
            var line = await reader.ReadLineAsync();
            if (line is null)
            {
                yield break;
            }

            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var data = line[5..].Trim();
            if (string.Equals(data, "[DONE]", StringComparison.Ordinal))
            {
                yield return new LlmStreamCompleted("stop");
                yield break;
            }

            using var document = JsonDocument.Parse(data);
            if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                continue;
            }

            var choice = choices[0];
            if (choice.TryGetProperty("delta", out var delta))
            {
                if (delta.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.String)
                {
                    yield return new LlmTextDelta(contentElement.GetString() ?? string.Empty);
                }

                if (delta.TryGetProperty("reasoning_content", out var reasoningElement) && reasoningElement.ValueKind == JsonValueKind.String)
                {
                    yield return new LlmTextDelta(reasoningElement.GetString() ?? string.Empty);
                }

                if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var toolCall in toolCalls.EnumerateArray())
                    {
                        var index = toolCall.TryGetProperty("index", out var indexElement) ? indexElement.GetInt32() : 0;
                        string? id = toolCall.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                            ? idElement.GetString()
                            : null;
                        string? name = null;
                        string? arguments = null;

                        if (toolCall.TryGetProperty("function", out var functionElement))
                        {
                            if (functionElement.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
                            {
                                name = nameElement.GetString();
                            }

                            if (functionElement.TryGetProperty("arguments", out var argumentsElement) && argumentsElement.ValueKind == JsonValueKind.String)
                            {
                                arguments = argumentsElement.GetString();
                            }
                        }

                        yield return new LlmToolCallDelta(index, id, name, arguments);
                    }
                }
            }

            if (choice.TryGetProperty("finish_reason", out var finishReasonElement) && finishReasonElement.ValueKind == JsonValueKind.String)
            {
                yield return new LlmStreamCompleted(finishReasonElement.GetString());
            }
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(LlmConnectionSettings connection, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, $"{connection.BaseUrl.TrimEnd('/')}/models");
        if (!string.IsNullOrWhiteSpace(connection.ApiKey))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.ApiKey);
        }

        using var response = await httpClient.SendAsync(message, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("data", out var dataElement) || dataElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var modelIds = dataElement
            .EnumerateArray()
            .Select(item => item.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString()
                : null)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return connection.Provider == LlmProvider.OpenAi
            ? modelIds.Where(IsLikelyOpenAiChatModel).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray()
            : modelIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static JsonObject BuildPayload(LlmRequest request)
    {
        var payload = new JsonObject
        {
            ["model"] = request.Connection.Model,
            ["stream"] = request.Stream,
            ["messages"] = BuildMessages(request.Messages),
            ["tool_choice"] = "auto",
            ["tools"] = BuildTools(request.Tools, request.Connection.Provider),
        };

        payload["temperature"] = request.Connection.Temperature;
        return payload;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = response.Content is null
            ? null
            : await response.Content.ReadAsStringAsync(cancellationToken);
        var message = string.IsNullOrWhiteSpace(body)
            ? $"OpenAI-compatible request failed with {(int)response.StatusCode} {response.ReasonPhrase}."
            : $"OpenAI-compatible request failed with {(int)response.StatusCode} {response.ReasonPhrase}: {body}";
        throw new HttpRequestException(message, null, response.StatusCode);
    }

    private static bool IsLikelyOpenAiChatModel(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return false;
        }

        var normalized = modelId.Trim().ToLowerInvariant();
        if (normalized.StartsWith("text-embedding-", StringComparison.Ordinal) ||
            normalized.StartsWith("whisper-", StringComparison.Ordinal) ||
            normalized.StartsWith("tts-", StringComparison.Ordinal) ||
            normalized.StartsWith("omni-moderation-", StringComparison.Ordinal) ||
            normalized.StartsWith("dall-e-", StringComparison.Ordinal) ||
            normalized.StartsWith("gpt-image-", StringComparison.Ordinal) ||
            normalized.StartsWith("gpt-realtime-", StringComparison.Ordinal) ||
            normalized.StartsWith("gpt-audio-", StringComparison.Ordinal) ||
            normalized.Contains("transcribe", StringComparison.Ordinal) ||
            normalized.Contains("embed", StringComparison.Ordinal) ||
            normalized.Contains("moderation", StringComparison.Ordinal) ||
            normalized.Contains("image", StringComparison.Ordinal))
        {
            return false;
        }

        return normalized.StartsWith("gpt-", StringComparison.Ordinal) ||
               normalized.StartsWith("o", StringComparison.Ordinal) ||
               normalized.StartsWith("codex-", StringComparison.Ordinal);
    }

    private static JsonArray BuildMessages(IReadOnlyList<LlmConversationMessage> messages)
    {
        var array = new JsonArray();
        foreach (var message in messages)
        {
            var node = new JsonObject
            {
                ["role"] = message.Role,
            };

            if (!string.IsNullOrWhiteSpace(message.Content))
            {
                node["content"] = message.Content;
            }

            if (!string.IsNullOrWhiteSpace(message.ToolCallId))
            {
                node["tool_call_id"] = message.ToolCallId;
            }

            if (!string.IsNullOrWhiteSpace(message.Name))
            {
                node["name"] = message.Name;
            }

            if (message.ToolCalls is { Count: > 0 })
            {
                var toolCalls = new JsonArray();
                foreach (var toolCall in message.ToolCalls)
                {
                    toolCalls.Add(new JsonObject
                    {
                        ["id"] = toolCall.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = toolCall.Name,
                            ["arguments"] = toolCall.ArgumentsJson,
                        },
                    });
                }

                node["tool_calls"] = toolCalls;
            }

            array.Add(node);
        }

        return array;
    }

    private static JsonArray BuildTools(IReadOnlyList<LlmToolDefinition> tools, LlmProvider provider)
    {
        var array = new JsonArray();
        foreach (var tool in tools)
        {
            var parameters = provider == LlmProvider.OpenAi
                ? BuildOpenAiCompatibleSchema(tool.Parameters)
                : tool.Parameters.DeepClone();

            array.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = parameters,
                },
            });
        }

        return array;
    }

    private static JsonObject BuildOpenAiCompatibleSchema(JsonObject schema) =>
        (JsonObject)NormalizeSchemaNode(schema)!;

    private static JsonNode? NormalizeSchemaNode(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;

            case JsonObject obj:
            {
                var clone = new JsonObject();
                foreach (var property in obj)
                {
                    clone[property.Key] = NormalizeSchemaNode(property.Value);
                }

                if (string.Equals(clone["type"]?.GetValue<string>(), "object", StringComparison.OrdinalIgnoreCase))
                {
                    clone["additionalProperties"] = false;
                }

                return clone;
            }

            case JsonArray array:
            {
                var clone = new JsonArray();
                foreach (var item in array)
                {
                    clone.Add(NormalizeSchemaNode(item));
                }

                return clone;
            }

            default:
                return node.DeepClone();
        }
    }
}
