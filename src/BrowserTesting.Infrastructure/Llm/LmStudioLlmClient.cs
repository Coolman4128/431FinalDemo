using System.Net.Http.Headers;
using System.Diagnostics;
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
        using var response = await SendAsyncWithHttpErrorHandlingAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

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

        using var response = await SendAsyncWithHttpErrorHandlingAsync(message, cancellationToken);

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

    private async Task<HttpResponseMessage> SendAsyncWithHttpErrorHandlingAsync(
        HttpRequestMessage message,
        CancellationToken cancellationToken) =>
        await SendAsyncWithHttpErrorHandlingAsync(message, completionOption: null, cancellationToken);

    private async Task<HttpResponseMessage> SendAsyncWithHttpErrorHandlingAsync(
        HttpRequestMessage message,
        HttpCompletionOption? completionOption,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;

        try
        {
            response = completionOption is { } option
                ? await httpClient.SendAsync(message, option, cancellationToken)
                : await httpClient.SendAsync(message, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            response?.Dispose();

            var errorMessage = $"HTTP request timed out: {FormatRequest(message)}.";
            Trace.WriteLine(errorMessage);
            throw new HttpRequestException(errorMessage);
        }
        catch (HttpRequestException ex) when (response is null)
        {
            var errorMessage = BuildTransportErrorMessage(message, ex);
            Trace.WriteLine($"{errorMessage}{Environment.NewLine}{ex}");
            throw new HttpRequestException(errorMessage, ex, ex.StatusCode);
        }

        try
        {
            await EnsureSuccessAsync(response, cancellationToken);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static JsonObject BuildPayload(LlmRequest request)
    {
        var payload = new JsonObject
        {
            ["model"] = request.Connection.Model,
            ["stream"] = request.Stream,
            ["messages"] = BuildMessages(request.Messages),
            ["tool_choice"] = BuildToolChoice(request),
            ["parallel_tool_calls"] = request.ParallelToolCalls,
            ["tools"] = BuildTools(request.Tools, request.Connection.Provider),
        };

        if (ShouldSendTemperature(request.Connection))
        {
            payload["temperature"] = request.Connection.Temperature;
        }

        return payload;
    }

    private static JsonNode BuildToolChoice(LlmRequest request) =>
        request.ToolChoiceMode switch
        {
            LlmToolChoiceMode.Required => JsonValue.Create("required")!,
            LlmToolChoiceMode.ForceFunction when !string.IsNullOrWhiteSpace(request.ForcedToolName) => new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = request.ForcedToolName,
                },
            },
            _ => JsonValue.Create("auto")!,
        };

    private static bool ShouldSendTemperature(LlmConnectionSettings connection)
    {
        if (connection.Provider != LlmProvider.OpenAi)
        {
            return true;
        }

        if (!UsesFixedOpenAiTemperature(connection.Model))
        {
            return true;
        }

        return Math.Abs(connection.Temperature - 1.0d) < 0.0001d;
    }

    private static bool UsesFixedOpenAiTemperature(string model)
    {
        var normalized = model.Trim().ToLowerInvariant();
        return normalized.StartsWith("gpt-5", StringComparison.Ordinal) ||
               normalized.StartsWith("o1", StringComparison.Ordinal) ||
               normalized.StartsWith("o3", StringComparison.Ordinal) ||
               normalized.StartsWith("o4", StringComparison.Ordinal) ||
               normalized.StartsWith("codex-", StringComparison.Ordinal);
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
        var requestDescription = FormatRequest(response.RequestMessage);
        var message = string.IsNullOrWhiteSpace(body)
            ? $"HTTP request failed: {requestDescription} returned {(int)response.StatusCode} {response.ReasonPhrase}."
            : $"HTTP request failed: {requestDescription} returned {(int)response.StatusCode} {response.ReasonPhrase}. Response body: {body}";
        Trace.WriteLine(message);
        throw new HttpRequestException(message, null, response.StatusCode);
    }

    private static string BuildTransportErrorMessage(HttpRequestMessage? message, HttpRequestException exception)
    {
        var details = string.IsNullOrWhiteSpace(exception.Message)
            ? exception.InnerException?.Message
            : exception.Message;

        return string.IsNullOrWhiteSpace(details)
            ? $"HTTP request failed: {FormatRequest(message)}."
            : $"HTTP request failed: {FormatRequest(message)}. Error: {details}";
    }

    private static string FormatRequest(HttpRequestMessage? message)
    {
        var method = message?.Method.Method ?? "HTTP";
        var uri = message?.RequestUri?.ToString() ?? "unknown endpoint";
        return $"{method} {uri}";
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

            var function = new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = parameters,
            };

            if (provider == LlmProvider.OpenAi)
            {
                function["strict"] = true;
            }

            array.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = function,
            });
        }

        return array;
    }

    private static JsonObject BuildOpenAiCompatibleSchema(JsonObject schema) =>
        (JsonObject)NormalizeSchemaNodeForOpenAi(schema)!;

    private static JsonNode? NormalizeSchemaNodeForOpenAi(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;

            case JsonObject obj:
            {
                if (string.Equals(obj["type"]?.GetValue<string>(), "object", StringComparison.OrdinalIgnoreCase))
                {
                    return NormalizeObjectSchemaForOpenAi(obj);
                }

                var clone = new JsonObject();
                foreach (var property in obj)
                {
                    clone[property.Key] = NormalizeSchemaNodeForOpenAi(property.Value);
                }

                return clone;
            }

            case JsonArray array:
            {
                var clone = new JsonArray();
                foreach (var item in array)
                {
                    clone.Add(NormalizeSchemaNodeForOpenAi(item));
                }

                return clone;
            }

            default:
                return node.DeepClone();
        }
    }

    private static JsonObject NormalizeObjectSchemaForOpenAi(JsonObject obj)
    {
        var clone = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
        };

        var properties = obj["properties"]?.AsObject() ?? new JsonObject();
        var originallyRequired = obj["required"]?.AsArray()
            .Select(item => item?.GetValue<string>())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal)
            ?? [];

        var normalizedProperties = new JsonObject();
        var required = new JsonArray();
        foreach (var property in properties)
        {
            if (property.Value is null)
            {
                continue;
            }

            var normalized = NormalizeSchemaNodeForOpenAi(property.Value);
            normalizedProperties[property.Key] = originallyRequired.Contains(property.Key)
                ? normalized
                : MakeNullableSchema(normalized);
            required.Add(property.Key);
        }

        clone["properties"] = normalizedProperties;
        clone["required"] = required;

        foreach (var property in obj)
        {
            if (property.Key is "type" or "properties" or "required" or "additionalProperties")
            {
                continue;
            }

            clone[property.Key] = NormalizeSchemaNodeForOpenAi(property.Value);
        }

        return clone;
    }

    private static JsonNode? MakeNullableSchema(JsonNode? schema)
    {
        if (schema is JsonObject schemaObject &&
            schemaObject["anyOf"] is JsonArray anyOf &&
            anyOf.OfType<JsonObject>().Any(candidate => string.Equals(candidate["type"]?.GetValue<string>(), "null", StringComparison.OrdinalIgnoreCase)))
        {
            return schemaObject;
        }

        return new JsonObject
        {
            ["anyOf"] = new JsonArray
            {
                schema,
                new JsonObject { ["type"] = "null" },
            },
        };
    }
}
