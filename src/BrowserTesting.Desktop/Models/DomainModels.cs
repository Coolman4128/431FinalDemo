#pragma warning disable CA1416
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BrowserTesting.Desktop.Models;

public enum GoalStatus
{
    Pending = 0,
    Running = 1,
    Passed = 2,
    Failed = 3,
}

public enum TestRunStatus
{
    Pending = 0,
    Running = 1,
    WaitingForTool = 2,
    Completed = 3,
    Failed = 4,
    Cancelled = 5,
}

public enum BrowserState
{
    NotStarted = 0,
    Active = 1,
    Closed = 2,
    Failed = 3,
}

public enum TimelineItemKind
{
    UserMessage = 0,
    AssistantMessage = 1,
    ToolCallStarted = 2,
    ToolCallFinished = 3,
    GoalChanged = 4,
    SystemNotice = 5,
}

public sealed class ChatSession
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "New Chat";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<TestRun> Runs { get; set; } = [];
    public List<TimelineEntry> Timeline { get; set; } = [];
}

public sealed class ChatSessionSummary
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; }
    public int ActiveRuns { get; set; }
}

public sealed class TestRun
{
    public Guid Id { get; set; }
    public Guid ChatSessionId { get; set; }
    public string UserPrompt { get; set; } = string.Empty;
    public TestRunStatus Status { get; set; } = TestRunStatus.Pending;
    public string? FailureReason { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }
    public BrowserSessionSnapshot BrowserSnapshot { get; set; } = new();
    public List<GoalItem> Goals { get; set; } = [];
}

public sealed class GoalItem
{
    public Guid Id { get; set; }
    public Guid TestRunId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string SuccessCriteria { get; set; } = string.Empty;
    public GoalStatus Status { get; set; } = GoalStatus.Pending;
    public string? Note { get; set; }
    public string? Evidence { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }
}

public sealed class BrowserSessionSnapshot
{
    public Guid TestRunId { get; set; }
    public string? CurrentUrl { get; set; }
    public string? PageTitle { get; set; }
    public BrowserState State { get; set; } = BrowserState.NotStarted;
    public DateTime? LastCapturedAtUtc { get; set; }
    public List<BrowserTabInfo> Tabs { get; set; } = [];
}

public sealed class BrowserTabInfo
{
    public string Handle { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? Url { get; set; }
    public bool IsSelected { get; set; }
}

public sealed class TimelineEntry
{
    public Guid Id { get; set; }
    public Guid ChatSessionId { get; set; }
    public Guid? TestRunId { get; set; }
    public long Sequence { get; set; }
    public TimelineItemKind Kind { get; set; }
    public string Role { get; set; } = "system";
    public string Content { get; set; } = string.Empty;
    public string? ToolCallId { get; set; }
    public string? ToolName { get; set; }
    public string? MetadataJson { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ToolExecutionResult
{
    public bool Success { get; set; }
    public string Summary { get; set; } = string.Empty;
    public JsonNode? Data { get; set; }
    public string? Error { get; set; }
    public string? Hint { get; set; }
    public JsonNode? NormalizedArguments { get; set; }
    public JsonNode? ExpectedArguments { get; set; }
    public JsonNode? ExampleArguments { get; set; }

    public static ToolExecutionResult Successful(
        string summary,
        JsonNode? data = null,
        string? hint = null,
        JsonNode? normalizedArguments = null) =>
        new()
        {
            Success = true,
            Summary = summary,
            Data = data,
            Hint = hint,
            NormalizedArguments = normalizedArguments,
        };

    public static ToolExecutionResult Failed(
        string summary,
        string? error = null,
        JsonNode? data = null,
        string? hint = null,
        JsonNode? normalizedArguments = null,
        JsonNode? expectedArguments = null,
        JsonNode? exampleArguments = null) =>
        new()
        {
            Success = false,
            Summary = summary,
            Error = error,
            Data = data,
            Hint = hint,
            NormalizedArguments = normalizedArguments,
            ExpectedArguments = expectedArguments,
            ExampleArguments = exampleArguments,
        };
}

public sealed class ToolInvocationContext
{
    public required Guid ChatSessionId { get; init; }
    public required Guid TestRunId { get; init; }
    public required bool LaunchHeadless { get; init; }
    public BrowserSessionSnapshot? BrowserSnapshot { get; init; }
}

public sealed class AppSettings
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public const string LocalServerBaseUrl = "http://localhost:1234/v1";
    public const string OpenAiBaseUrl = "https://api.openai.com/v1";

    public LlmProvider Provider { get; set; } = LlmProvider.Local;
    public string LocalModelName { get; set; } = "gpt-4o-mini";
    public string OpenAiModelName { get; set; } = "gpt-4o-mini";
    public string? OpenAiApiKey { get; set; }
    public double Temperature { get; set; } = 0.2d;
    public int MaxToolIterations { get; set; } = 18;
    public bool LaunchHeadless { get; set; }
    public string DatabasePath { get; set; } = string.Empty;
    public string ScreenshotDirectory { get; set; } = string.Empty;
    public string ChromeProfileRoot { get; set; } = string.Empty;
    public string SettingsFilePath { get; set; } = string.Empty;

    public string CurrentModelName
    {
        get => Provider switch
        {
            LlmProvider.OpenAi => OpenAiModelName,
            _ => LocalModelName,
        };
        set
        {
            switch (Provider)
            {
                case LlmProvider.OpenAi:
                    OpenAiModelName = value;
                    break;
                default:
                    LocalModelName = value;
                    break;
            }
        }
    }

    public LlmConnectionSettings CreateConnectionSettings(
        LlmProvider? providerOverride = null,
        string? modelOverride = null,
        string? apiKeyOverride = null)
    {
        var provider = providerOverride ?? Provider;
        var model = modelOverride ?? (provider == LlmProvider.OpenAi ? OpenAiModelName : LocalModelName);
        var apiKey = provider == LlmProvider.OpenAi
            ? (apiKeyOverride ?? OpenAiApiKey)
            : null;

        return new LlmConnectionSettings
        {
            Provider = provider,
            BaseUrl = provider == LlmProvider.OpenAi ? OpenAiBaseUrl : LocalServerBaseUrl,
            Model = model,
            ApiKey = apiKey,
            Temperature = Temperature,
        };
    }

    public Task LoadAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(SettingsFilePath) || !File.Exists(SettingsFilePath))
        {
            return Task.CompletedTask;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = File.OpenRead(SettingsFilePath);
            var persisted = JsonSerializer.Deserialize<PersistedAppSettings>(stream, SerializerOptions);
            if (persisted is null)
            {
                return Task.CompletedTask;
            }

            Provider = Enum.IsDefined(typeof(LlmProvider), persisted.Provider)
                ? persisted.Provider
                : Provider;
            LocalModelName = string.IsNullOrWhiteSpace(persisted.LocalModelName)
                ? LocalModelName
                : persisted.LocalModelName;
            OpenAiModelName = string.IsNullOrWhiteSpace(persisted.OpenAiModelName)
                ? OpenAiModelName
                : persisted.OpenAiModelName;
            OpenAiApiKey = DecryptOrUsePlaintext(
                persisted.EncryptedOpenAiApiKey,
                persisted.OpenAiApiKey);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to load app settings from '{SettingsFilePath}': {ex}");
        }

        return Task.CompletedTask;
    }

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(SettingsFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var persisted = new PersistedAppSettings
        {
            Provider = Provider,
            LocalModelName = LocalModelName,
            OpenAiModelName = OpenAiModelName,
            EncryptedOpenAiApiKey = Encrypt(OpenAiApiKey),
        };

        await using var stream = File.Create(SettingsFilePath);
        await JsonSerializer.SerializeAsync(stream, persisted, SerializerOptions, cancellationToken);
    }

    public static AppSettings CreateDefault(string rootDirectory)
    {
        var appDataRoot = Path.Combine(rootDirectory, "AppData");
        Directory.CreateDirectory(appDataRoot);

        return new AppSettings
        {
            DatabasePath = Path.Combine(appDataRoot, "browser-testing-v2.db"),
            ScreenshotDirectory = Path.Combine(appDataRoot, "Screenshots"),
            ChromeProfileRoot = Path.Combine(appDataRoot, "ChromeProfiles"),
            SettingsFilePath = Path.Combine(appDataRoot, "settings.json"),
        };
    }

    private static string? Encrypt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var clearBytes = Encoding.UTF8.GetBytes(value);
        var encryptedBytes = ProtectedData.Protect(clearBytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encryptedBytes);
    }

    private static string? DecryptOrUsePlaintext(string? encryptedValue, string? plaintextValue)
    {
        if (!string.IsNullOrWhiteSpace(encryptedValue))
        {
            try
            {
                var encryptedBytes = Convert.FromBase64String(encryptedValue);
                var clearBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(clearBytes);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to decrypt saved OpenAI API key: {ex}");

                if (LooksLikePlaintextApiKey(encryptedValue))
                {
                    return encryptedValue;
                }
            }
        }

        return LooksLikePlaintextApiKey(plaintextValue)
            ? plaintextValue
            : null;
    }

    private static bool LooksLikePlaintextApiKey(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (value.StartsWith("sk-", StringComparison.OrdinalIgnoreCase) ||
         value.StartsWith("sess-", StringComparison.OrdinalIgnoreCase));

    private sealed class PersistedAppSettings
    {
        public LlmProvider Provider { get; set; } = LlmProvider.Local;
        public string? LocalModelName { get; set; }
        public string? OpenAiModelName { get; set; }
        public string? EncryptedOpenAiApiKey { get; set; }
        public string? OpenAiApiKey { get; set; }
    }
}
