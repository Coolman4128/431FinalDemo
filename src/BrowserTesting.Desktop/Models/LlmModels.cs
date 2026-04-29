using System.Text.Json.Nodes;

namespace BrowserTesting.Desktop.Models;

public sealed class LlmConnectionSettings
{
    public required LlmProvider Provider { get; init; }
    public required string BaseUrl { get; init; }
    public required string Model { get; init; }
    public string? ApiKey { get; init; }
    public double Temperature { get; init; } = 0.2d;
}

public sealed class LlmRequest
{
    public required LlmConnectionSettings Connection { get; init; }
    public required IReadOnlyList<LlmConversationMessage> Messages { get; init; }
    public required IReadOnlyList<LlmToolDefinition> Tools { get; init; }
    public bool Stream { get; init; } = true;
    public LlmToolChoiceMode ToolChoiceMode { get; init; } = LlmToolChoiceMode.Auto;
    public string? ForcedToolName { get; init; }
    public bool ParallelToolCalls { get; init; }
    public string Model => Connection.Model;
    public double Temperature => Connection.Temperature;
}

public enum LlmToolChoiceMode
{
    Auto = 0,
    Required = 1,
    ForceFunction = 2,
}

public sealed class LlmConversationMessage
{
    public required string Role { get; init; }
    public string? Content { get; init; }
    public IReadOnlyList<LlmToolCall>? ToolCalls { get; init; }
    public string? ToolCallId { get; init; }
    public string? Name { get; init; }
}

public sealed class LlmToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required JsonObject Parameters { get; init; }
}

public sealed class LlmToolCall
{
    public required int Index { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string ArgumentsJson { get; init; }
}

public abstract record LlmStreamEvent;

public sealed record LlmTextDelta(string Content) : LlmStreamEvent;

public sealed record LlmToolCallDelta(int Index, string? IdPart, string? NamePart, string? ArgumentsPart) : LlmStreamEvent;

public sealed record LlmStreamCompleted(string? FinishReason) : LlmStreamEvent;

public sealed record LlmStreamFaulted(string Message) : LlmStreamEvent;
