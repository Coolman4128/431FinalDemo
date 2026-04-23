using System.Collections.Specialized;
using System.ComponentModel;
using BrowserTesting.Desktop.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;

namespace BrowserTesting.Desktop.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? viewModel;
    private ScrollViewer? timelineScrollViewer;
    private TextBox? composerTextBox;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);

        timelineScrollViewer = this.FindControl<ScrollViewer>("TimelineScrollViewer");
        composerTextBox = this.FindControl<TextBox>("ComposerTextBox");

        if (composerTextBox is not null)
        {
            composerTextBox.TextChanged += ComposerTextBox_OnTextChanged;
            composerTextBox.PropertyChanged += ComposerTextBox_OnPropertyChanged;
            composerTextBox.KeyDown += ComposerTextBox_OnKeyDown;
        }

        DataContextChanged += MainWindow_OnDataContextChanged;
    }

    private void MainWindow_OnDataContextChanged(object? sender, EventArgs e)
    {
        if (viewModel is not null)
        {
            viewModel.Timeline.CollectionChanged -= Timeline_OnCollectionChanged;
            viewModel.PropertyChanged -= ViewModel_OnPropertyChanged;
        }

        viewModel = DataContext as MainWindowViewModel;
        if (viewModel is not null)
        {
            viewModel.Timeline.CollectionChanged += Timeline_OnCollectionChanged;
            viewModel.PropertyChanged += ViewModel_OnPropertyChanged;
            ScheduleScrollToEnd();
        }
    }

    private void Timeline_OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        ScheduleScrollToEnd();

    private void ViewModel_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.IsTimelineVisible))
        {
            ScheduleScrollToEnd();
        }
    }

    private void ComposerTextBox_OnTextChanged(object? sender, TextChangedEventArgs e) =>
        UpdateComposerHeight();

    private void ComposerTextBox_OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property.Name == nameof(Bounds))
        {
            UpdateComposerHeight();
        }
    }

    private void ComposerTextBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (viewModel is null || composerTextBox is null || e.Key != Key.Enter)
        {
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            return;
        }

        if (!viewModel.SendCommand.CanExecute(null))
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;
        viewModel.SendCommand.Execute(null);
    }

    private void ScheduleScrollToEnd()
    {
        if (timelineScrollViewer is null || viewModel?.IsTimelineVisible != true)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () => timelineScrollViewer.Offset = new Vector(timelineScrollViewer.Offset.X, double.MaxValue),
            DispatcherPriority.Background);
    }

    private void UpdateComposerHeight()
    {
        if (composerTextBox is null || composerTextBox.Bounds.Width <= 0)
        {
            return;
        }

        var padding = composerTextBox.Padding;
        var availableWidth = Math.Max(80, composerTextBox.Bounds.Width - padding.Left - padding.Right - 2);
        var text = string.IsNullOrWhiteSpace(composerTextBox.Text) ? " " : $"{composerTextBox.Text} ";

        using var textLayout = new TextLayout(
            text,
            new Typeface(composerTextBox.FontFamily, composerTextBox.FontStyle, composerTextBox.FontWeight),
            composerTextBox.FontSize,
            Brushes.Transparent,
            TextAlignment.Left,
            TextWrapping.Wrap,
            TextTrimming.None,
            null,
            composerTextBox.FlowDirection,
            availableWidth);

        var targetHeight = textLayout.Height + padding.Top + padding.Bottom + 8;
        composerTextBox.Height = Math.Clamp(targetHeight, 48, 220);
    }
}
