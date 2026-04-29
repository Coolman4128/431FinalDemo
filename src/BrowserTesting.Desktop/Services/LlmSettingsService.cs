using BrowserTesting.Core.Models;
using BrowserTesting.Infrastructure.Llm;
using BrowserTesting.Infrastructure.Settings;

namespace BrowserTesting.Desktop.Services;

public sealed class LlmSettingsService(
    AppSettings settings,
    JsonAppSettingsStore settingsStore,
    LmStudioLlmClient llmClient)
{
    public AppSettings Settings => settings;

    public Task<IReadOnlyList<string>> ListModelsAsync(LlmProvider provider, string? openAiApiKey, CancellationToken cancellationToken)
    {
        var apiKey = provider == LlmProvider.OpenAi ? Normalize(openAiApiKey) : null;
        return llmClient.ListModelsAsync(
            settings.CreateConnectionSettings(provider, modelOverride: string.Empty, apiKeyOverride: apiKey),
            cancellationToken);
    }

    public Task SaveAsync(
        LlmProvider provider,
        string? localModelName,
        string? openAiModelName,
        string? openAiApiKey,
        CancellationToken cancellationToken)
    {
        settings.Provider = provider;
        settings.LocalModelName = Normalize(localModelName) ?? settings.LocalModelName;
        settings.OpenAiModelName = Normalize(openAiModelName) ?? settings.OpenAiModelName;
        settings.OpenAiApiKey = Normalize(openAiApiKey);
        return settingsStore.SaveAsync(settings, cancellationToken);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
