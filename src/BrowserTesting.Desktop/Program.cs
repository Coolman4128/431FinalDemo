using Avalonia;
using System.Runtime.Versioning;

namespace BrowserTesting.Desktop;

[SupportedOSPlatform("windows")]
internal sealed class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
