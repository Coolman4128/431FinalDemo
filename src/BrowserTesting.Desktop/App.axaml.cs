using BrowserTesting.Desktop.Models;
using BrowserTesting.Desktop.Services;
using BrowserTesting.Desktop.ViewModels;
using BrowserTesting.Desktop.Views;
using BrowserTesting.Desktop.Classes;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using System.Runtime.Versioning;

namespace BrowserTesting.Desktop;

[SupportedOSPlatform("windows")]
public partial class App : Application
{
    public override void Initialize() =>
        AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = AppSettings.CreateDefault(AppContext.BaseDirectory);
            settings.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
            var repository = new SqliteChatRepository(settings);
            var browserSessionManager = new BrowserSessionManager(settings);
            var llmClient = new LmStudioLlmClient(new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(10),
            });
            var orchestrator = new ChatOrchestrator(
                repository,
                llmClient,
                browserSessionManager,
                settings);

            var mainWindow = new MainWindow();
            mainWindow.DataContext = new MainWindowViewModel(orchestrator, mainWindow.SaveTextAsync, settings, llmClient);
            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
