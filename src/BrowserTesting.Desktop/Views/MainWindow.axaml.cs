using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace BrowserTesting.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow() =>
        AvaloniaXamlLoader.Load(this);
}
