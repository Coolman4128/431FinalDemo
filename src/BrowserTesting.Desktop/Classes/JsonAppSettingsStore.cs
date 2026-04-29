#pragma warning disable CA1416
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BrowserTesting.Desktop.Models;

namespace BrowserTesting.Desktop.Classes;

public sealed class JsonAppSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public Task LoadAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.SettingsFilePath) || !File.Exists(settings.SettingsFilePath))
        {
            return Task.CompletedTask;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = File.OpenRead(settings.SettingsFilePath);
            var persisted = JsonSerializer.Deserialize<PersistedAppSettings>(stream, SerializerOptions);
            if (persisted is null)
            {
                return Task.CompletedTask;
            }

            settings.Provider = Enum.IsDefined(typeof(LlmProvider), persisted.Provider)
                ? persisted.Provider
                : settings.Provider;
            settings.LocalModelName = string.IsNullOrWhiteSpace(persisted.LocalModelName)
                ? settings.LocalModelName
                : persisted.LocalModelName;
            settings.OpenAiModelName = string.IsNullOrWhiteSpace(persisted.OpenAiModelName)
                ? settings.OpenAiModelName
                : persisted.OpenAiModelName;
            settings.OpenAiApiKey = DecryptOrUsePlaintext(
                persisted.EncryptedOpenAiApiKey,
                persisted.OpenAiApiKey);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to load app settings from '{settings.SettingsFilePath}': {ex}");
        }

        return Task.CompletedTask;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(settings.SettingsFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var persisted = new PersistedAppSettings
        {
            Provider = settings.Provider,
            LocalModelName = settings.LocalModelName,
            OpenAiModelName = settings.OpenAiModelName,
            EncryptedOpenAiApiKey = Encrypt(settings.OpenAiApiKey),
        };

        await using var stream = File.Create(settings.SettingsFilePath);
        await JsonSerializer.SerializeAsync(stream, persisted, SerializerOptions, cancellationToken);
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
