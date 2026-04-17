using System.Net;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Text.Json;
using BrowserTesting.Core.Llm;
using BrowserTesting.Core.Models;
using BrowserTesting.Desktop.Services;
using BrowserTesting.Desktop.ViewModels;
using BrowserTesting.Infrastructure.Llm;
using BrowserTesting.Infrastructure.Settings;
using BrowserTesting.Infrastructure.Tools;

namespace BrowserTesting.Tests;

[SupportedOSPlatform("windows")]
public sealed class LlmSettingsTests
{
    [Fact]
    public async Task JsonAppSettingsStore_LeavesDefaultsWhenSettingsFileIsMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "BrowserTestingTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settings = AppSettings.CreateDefault(root);
        var originalProvider = settings.Provider;
        var originalLocalModel = settings.LocalModelName;

        var store = new JsonAppSettingsStore();

        await store.LoadAsync(settings, CancellationToken.None);

        Assert.Equal(originalProvider, settings.Provider);
        Assert.Equal(originalLocalModel, settings.LocalModelName);
        Assert.Null(settings.OpenAiApiKey);
    }

    [Fact]
    public async Task JsonAppSettingsStore_SavesAndLoadsEncryptedApiKey()
    {
        var root = Path.Combine(Path.GetTempPath(), "BrowserTestingTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settings = AppSettings.CreateDefault(root);
        settings.Provider = LlmProvider.OpenAi;
        settings.LocalModelName = "local-model";
        settings.OpenAiModelName = "gpt-5-mini";
        settings.OpenAiApiKey = "sk-secret-value";

        var store = new JsonAppSettingsStore();

        await store.SaveAsync(settings, CancellationToken.None);

        var json = await File.ReadAllTextAsync(settings.SettingsFilePath, CancellationToken.None);
        Assert.DoesNotContain("sk-secret-value", json, StringComparison.Ordinal);

        var reloaded = AppSettings.CreateDefault(root);
        await store.LoadAsync(reloaded, CancellationToken.None);

        Assert.Equal(LlmProvider.OpenAi, reloaded.Provider);
        Assert.Equal("local-model", reloaded.LocalModelName);
        Assert.Equal("gpt-5-mini", reloaded.OpenAiModelName);
        Assert.Equal("sk-secret-value", reloaded.OpenAiApiKey);
    }

    [Fact]
    public async Task JsonAppSettingsStore_ToleratesInvalidEncryptedApiKey()
    {
        var root = Path.Combine(Path.GetTempPath(), "BrowserTestingTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settings = AppSettings.CreateDefault(root);
        await File.WriteAllTextAsync(
            settings.SettingsFilePath,
            """
            {
              "provider": 1,
              "localModelName": "local-model",
              "openAiModelName": "gpt-5.4",
              "encryptedOpenAiApiKey": "not-a-valid-dpapi-value"
            }
            """,
            CancellationToken.None);

        var store = new JsonAppSettingsStore();

        await store.LoadAsync(settings, CancellationToken.None);

        Assert.Equal(LlmProvider.OpenAi, settings.Provider);
        Assert.Equal("gpt-5.4", settings.OpenAiModelName);
        Assert.Null(settings.OpenAiApiKey);
    }

    [Fact]
    public async Task JsonAppSettingsStore_LoadsLegacyPlaintextApiKey()
    {
        var root = Path.Combine(Path.GetTempPath(), "BrowserTestingTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settings = AppSettings.CreateDefault(root);
        await File.WriteAllTextAsync(
            settings.SettingsFilePath,
            """
            {
              "provider": 1,
              "openAiModelName": "gpt-5.4",
              "openAiApiKey": "sk-legacy-plaintext"
            }
            """,
            CancellationToken.None);

        var store = new JsonAppSettingsStore();

        await store.LoadAsync(settings, CancellationToken.None);

        Assert.Equal(LlmProvider.OpenAi, settings.Provider);
        Assert.Equal("sk-legacy-plaintext", settings.OpenAiApiKey);
    }

    [Fact]
    public async Task LmStudioLlmClient_ListModelsAsync_SortsModelsAndIncludesAuthorization()
    {
        var handler = new RecordingHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[{\"id\":\"gpt-5\"},{\"id\":\"text-embedding-3-small\"},{\"id\":\"gpt-4o-mini\"},{\"id\":\"gpt-5\"}]}"),
            });
        var client = new LmStudioLlmClient(new HttpClient(handler));

        var models = await client.ListModelsAsync(
            new LlmConnectionSettings
            {
                Provider = LlmProvider.OpenAi,
                BaseUrl = AppSettings.OpenAiBaseUrl,
                Model = string.Empty,
                ApiKey = "sk-test",
                Temperature = 0.2d,
            },
            CancellationToken.None);

        Assert.Equal($"{AppSettings.OpenAiBaseUrl}/models", handler.Requests[0].RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Requests[0].Headers.Authorization?.Scheme);
        Assert.Equal("sk-test", handler.Requests[0].Headers.Authorization?.Parameter);
        Assert.Equal(["gpt-4o-mini", "gpt-5"], models);
    }

    [Fact]
    public async Task LmStudioLlmClient_ListModelsAsync_OmitsAuthorizationForLocalProvider()
    {
        var handler = new RecordingHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[{\"id\":\"local-model\"}]}"),
            });
        var client = new LmStudioLlmClient(new HttpClient(handler));

        var models = await client.ListModelsAsync(
            new LlmConnectionSettings
            {
                Provider = LlmProvider.Local,
                BaseUrl = AppSettings.LocalServerBaseUrl,
                Model = string.Empty,
                Temperature = 0.2d,
            },
            CancellationToken.None);

        Assert.Equal($"{AppSettings.LocalServerBaseUrl}/models", handler.Requests[0].RequestUri!.ToString());
        Assert.Null(handler.Requests[0].Headers.Authorization);
        Assert.Equal(["local-model"], models);
    }

    [Fact]
    public async Task LlmSettingsViewModel_OpenAsync_LoadsCurrentProviderAndModels()
    {
        var service = new FakeLlmSettingsService();
        service.Settings.Provider = LlmProvider.OpenAi;
        service.Settings.OpenAiModelName = "gpt-4o-mini";
        service.Settings.OpenAiApiKey = "sk-open";
        service.ModelsByProvider[LlmProvider.OpenAi] = ["gpt-5", "gpt-4o-mini"];

        var viewModel = new LlmSettingsViewModel(service);

        await viewModel.OpenAsync();

        Assert.True(viewModel.IsOpen);
        Assert.True(viewModel.IsOpenAiSelected);
        Assert.Equal("sk-open", viewModel.OpenAiApiKey);
        Assert.Equal("gpt-4o-mini", viewModel.SelectedModel);
        Assert.Equal(2, viewModel.AvailableModels.Count);
    }

    [Fact]
    public async Task LlmSettingsViewModel_SwitchingToOpenAiRequiresKeyBeforeSave()
    {
        var service = new FakeLlmSettingsService();
        service.Settings.Provider = LlmProvider.Local;
        service.Settings.LocalModelName = "local-model";
        service.ModelsByProvider[LlmProvider.Local] = ["local-model"];
        service.ModelsByProvider[LlmProvider.OpenAi] = ["gpt-4o-mini"];

        var viewModel = new LlmSettingsViewModel(service);
        await viewModel.OpenAsync();

        viewModel.SelectedProviderOption = viewModel.ProviderOptions.Single(option => option.Value == LlmProvider.OpenAi);
        await viewModel.RefreshModelsAsync();

        Assert.False(viewModel.SaveCommand.CanExecute(null));
        Assert.Contains("API key", viewModel.ModelStatusText, StringComparison.OrdinalIgnoreCase);

        viewModel.OpenAiApiKey = "sk-test";
        await viewModel.RefreshModelsAsync();

        Assert.Equal("gpt-4o-mini", viewModel.SelectedModel);
        Assert.True(viewModel.SaveCommand.CanExecute(null));

        viewModel.ClearApiKey();

        Assert.False(viewModel.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task LmStudioLlmClient_IncludesResponseBodyInHttpRequestException()
    {
        var handler = new RecordingHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"error\":{\"message\":\"Unsupported value for temperature\"}}"),
            });
        var client = new LmStudioLlmClient(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await client.ListModelsAsync(
                new LlmConnectionSettings
                {
                    Provider = LlmProvider.OpenAi,
                    BaseUrl = AppSettings.OpenAiBaseUrl,
                    Model = string.Empty,
                    ApiKey = "sk-test",
                    Temperature = 0.2d,
                },
                CancellationToken.None);
        });

        Assert.Contains("Unsupported value for temperature", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LmStudioLlmClient_OpenAiRequestsEmitOpenAiCompatibleToolSchemas()
    {
        var handler = new RecordingHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: [DONE]\n\n"),
            });
        var client = new LmStudioLlmClient(new HttpClient(handler));
        var registry = new ToolRegistry();

        await foreach (var _ in client.StreamCompletionAsync(
                           new LlmRequest
                           {
                               Connection = new LlmConnectionSettings
                               {
                                   Provider = LlmProvider.OpenAi,
                                   BaseUrl = AppSettings.OpenAiBaseUrl,
                                   Model = "gpt-4o-mini",
                                   ApiKey = "sk-test",
                                   Temperature = 0.2d,
                               },
                               Messages =
                               [
                                   new LlmConversationMessage
                                   {
                                       Role = "developer",
                                       Content = "Test",
                                   },
                               ],
                               Tools = registry.GetToolDefinitions(),
                               Stream = true,
                           },
                           CancellationToken.None))
        {
        }

        var payload = await handler.Requests[0].Content!.ReadAsStringAsync(CancellationToken.None);
        using var document = JsonDocument.Parse(payload);
        var tools = document.RootElement.GetProperty("tools");
        var executeJavascript = tools.EnumerateArray()
            .Single(candidate => candidate.GetProperty("function").GetProperty("name").GetString() == "execute_javascript");
        var parameters = executeJavascript.GetProperty("function").GetProperty("parameters");
        var argumentItems = parameters
            .GetProperty("properties")
            .GetProperty("arguments")
            .GetProperty("items")
            .GetProperty("anyOf");

        Assert.True(parameters.TryGetProperty("additionalProperties", out var rootAdditionalProperties));
        Assert.False(rootAdditionalProperties.GetBoolean());
        Assert.DoesNotContain(argumentItems.EnumerateArray(), candidate => candidate.TryGetProperty("type", out var type) && type.GetString() is "object" or "array");

        var click = tools.EnumerateArray()
            .Single(candidate => candidate.GetProperty("function").GetProperty("name").GetString() == "click");
        var locator = click
            .GetProperty("function")
            .GetProperty("parameters")
            .GetProperty("properties")
            .GetProperty("locator");

        Assert.True(locator.TryGetProperty("additionalProperties", out var locatorAdditionalProperties));
        Assert.False(locatorAdditionalProperties.GetBoolean());

        var inMemoryLocator = registry.GetToolDefinitions()
            .Single(candidate => candidate.Name == "click")
            .Parameters["properties"]?["locator"]?.AsObject();
        Assert.NotNull(inMemoryLocator);
        Assert.Null(inMemoryLocator!["additionalProperties"]);
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

    private sealed class RecordingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> responder = responder;

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(Clone(request));
            return Task.FromResult(responder(request));
        }

        private static HttpRequestMessage Clone(HttpRequestMessage request)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (request.Content is not null)
            {
                clone.Content = new StringContent(request.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                foreach (var header in request.Content.Headers)
                {
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            return clone;
        }
    }
}
