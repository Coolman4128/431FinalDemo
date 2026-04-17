using System.Collections.ObjectModel;
using System.Linq;
using BrowserTesting.Core.Models;
using BrowserTesting.Desktop.Services;

namespace BrowserTesting.Desktop.ViewModels;

public sealed class LlmSettingsViewModel : ObservableObject
{
    private readonly ILlmSettingsService settingsService;
    private ProviderOptionViewModel? selectedProviderOption;
    private string? selectedModel;
    private string openAiApiKey = string.Empty;
    private string modelStatusText = "Open settings to load models.";
    private string validationMessage = string.Empty;
    private bool isOpen;
    private bool isLoadingModels;
    private bool suppressProviderRefresh;
    private string? draftLocalModelName;
    private string? draftOpenAiModelName;

    public LlmSettingsViewModel(ILlmSettingsService settingsService)
    {
        this.settingsService = settingsService;
        ProviderOptions =
        [
            new ProviderOptionViewModel(LlmProvider.Local, "Local server"),
            new ProviderOptionViewModel(LlmProvider.OpenAi, "OpenAI"),
        ];

        OpenCommand = new AsyncRelayCommand(OpenAsync);
        SaveCommand = new AsyncRelayCommand(SaveAsync, CanSave);
        RefreshModelsCommand = new AsyncRelayCommand(RefreshModelsAsync, () => IsOpen && !IsLoadingModels);
        CancelCommand = new RelayCommand(Cancel);
        ClearApiKeyCommand = new RelayCommand(ClearApiKey);
    }

    public event Action<string>? Completed;

    public ObservableCollection<ProviderOptionViewModel> ProviderOptions { get; }
    public ObservableCollection<string> AvailableModels { get; } = [];
    public AsyncRelayCommand OpenCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand RefreshModelsCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ClearApiKeyCommand { get; }

    public bool IsOpen
    {
        get => isOpen;
        private set
        {
            if (SetProperty(ref isOpen, value))
            {
                RaisePropertyChanged(nameof(IsOpenAiSelected));
                RefreshCommandState();
            }
        }
    }

    public ProviderOptionViewModel? SelectedProviderOption
    {
        get => selectedProviderOption;
        set
        {
            if (!SetProperty(ref selectedProviderOption, value))
            {
                return;
            }

            RaisePropertyChanged(nameof(IsOpenAiSelected));
            SelectedModel = CurrentDraftModelName;
            if (IsOpen && !suppressProviderRefresh)
            {
                _ = RefreshModelsAsync();
            }

            RefreshCommandState();
        }
    }

    public string? SelectedModel
    {
        get => selectedModel;
        set
        {
            if (!SetProperty(ref selectedModel, value))
            {
                return;
            }

            if (SelectedProvider == LlmProvider.OpenAi)
            {
                draftOpenAiModelName = value;
            }
            else
            {
                draftLocalModelName = value;
            }

            RefreshCommandState();
        }
    }

    public string OpenAiApiKey
    {
        get => openAiApiKey;
        set
        {
            if (!SetProperty(ref openAiApiKey, value))
            {
                return;
            }

            if (IsOpen && IsOpenAiSelected && !suppressProviderRefresh)
            {
                AvailableModels.Clear();
                ModelStatusText = "Refresh models to validate the current API key.";
                if (!string.IsNullOrWhiteSpace(draftOpenAiModelName))
                {
                    SelectedModel = draftOpenAiModelName;
                }
            }

            RefreshCommandState();
        }
    }

    public bool IsOpenAiSelected => SelectedProvider == LlmProvider.OpenAi;

    public bool IsLoadingModels
    {
        get => isLoadingModels;
        private set
        {
            if (SetProperty(ref isLoadingModels, value))
            {
                RefreshCommandState();
            }
        }
    }

    public string ModelStatusText
    {
        get => modelStatusText;
        private set => SetProperty(ref modelStatusText, value);
    }

    public string ValidationMessage
    {
        get => validationMessage;
        private set
        {
            if (SetProperty(ref validationMessage, value))
            {
                RaisePropertyChanged(nameof(HasValidationMessage));
            }
        }
    }

    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationMessage);

    private LlmProvider SelectedProvider => SelectedProviderOption?.Value ?? settingsService.Settings.Provider;

    private string? CurrentDraftModelName =>
        SelectedProvider == LlmProvider.OpenAi
            ? draftOpenAiModelName
            : draftLocalModelName;

    public async Task OpenAsync()
    {
        suppressProviderRefresh = true;
        try
        {
            draftLocalModelName = settingsService.Settings.LocalModelName;
            draftOpenAiModelName = settingsService.Settings.OpenAiModelName;
            OpenAiApiKey = settingsService.Settings.OpenAiApiKey ?? string.Empty;
            SelectedProviderOption = ProviderOptions.First(option => option.Value == settingsService.Settings.Provider);
            SelectedModel = CurrentDraftModelName;
            ValidationMessage = string.Empty;
            ModelStatusText = "Loading models...";
            IsOpen = true;
        }
        finally
        {
            suppressProviderRefresh = false;
        }

        await RefreshModelsAsync();
    }

    public void Cancel()
    {
        IsOpen = false;
        ValidationMessage = string.Empty;
        Completed?.Invoke("Settings changes discarded.");
    }

    public async Task SaveAsync()
    {
        if (!CanSave())
        {
            ValidationMessage = BuildValidationMessage();
            return;
        }

        await settingsService.SaveAsync(
            SelectedProvider,
            draftLocalModelName,
            draftOpenAiModelName,
            OpenAiApiKey,
            CancellationToken.None);

        IsOpen = false;
        ValidationMessage = string.Empty;
        Completed?.Invoke("LLM settings saved. Changes apply to new runs.");
    }

    public async Task RefreshModelsAsync()
    {
        ValidationMessage = string.Empty;
        IsLoadingModels = true;
        ModelStatusText = "Loading models...";

        try
        {
            if (SelectedProvider == LlmProvider.OpenAi && string.IsNullOrWhiteSpace(OpenAiApiKey))
            {
                AvailableModels.Clear();
                ModelStatusText = "Enter an OpenAI API key to load models.";
                RefreshCommandState();
                return;
            }

            var models = await settingsService.ListModelsAsync(SelectedProvider, OpenAiApiKey, CancellationToken.None);
            ReplaceModels(models);

            if (AvailableModels.Count == 0)
            {
                SelectedModel = null;
                ModelStatusText = "No models were returned by the selected provider.";
                RefreshCommandState();
                return;
            }

            var desiredModel = CurrentDraftModelName;
            SelectedModel = AvailableModels.Contains(desiredModel, StringComparer.Ordinal)
                ? desiredModel
                : AvailableModels[0];
            ModelStatusText = $"Loaded {AvailableModels.Count} model(s).";
        }
        catch (Exception ex)
        {
            AvailableModels.Clear();
            SelectedModel = null;
            ModelStatusText = $"Unable to load models: {ex.Message}";
        }
        finally
        {
            IsLoadingModels = false;
            RefreshCommandState();
        }
    }

    public void ClearApiKey()
    {
        OpenAiApiKey = string.Empty;
        if (IsOpenAiSelected)
        {
            AvailableModels.Clear();
            SelectedModel = null;
            ModelStatusText = "OpenAI API key cleared.";
        }

        RefreshCommandState();
    }

    private void ReplaceModels(IEnumerable<string> models)
    {
        AvailableModels.Clear();
        foreach (var model in models)
        {
            AvailableModels.Add(model);
        }
    }

    private bool CanSave() => string.IsNullOrWhiteSpace(BuildValidationMessage());

    private string BuildValidationMessage()
    {
        if (!IsOpen)
        {
            return "Open settings before saving.";
        }

        if (IsLoadingModels)
        {
            return "Wait for model loading to finish.";
        }

        if (SelectedProvider == LlmProvider.OpenAi && string.IsNullOrWhiteSpace(OpenAiApiKey))
        {
            return "Enter an OpenAI API key before saving OpenAI settings.";
        }

        if (string.IsNullOrWhiteSpace(SelectedModel))
        {
            return "Select a model before saving.";
        }

        if (!AvailableModels.Contains(SelectedModel, StringComparer.Ordinal))
        {
            return "Refresh models and choose a valid model before saving.";
        }

        return string.Empty;
    }

    private void RefreshCommandState()
    {
        SaveCommand.RaiseCanExecuteChanged();
        RefreshModelsCommand.RaiseCanExecuteChanged();
    }
}

public sealed class ProviderOptionViewModel(LlmProvider value, string label)
{
    public LlmProvider Value { get; } = value;
    public string Label { get; } = label;
    public override string ToString() => Label;
}
