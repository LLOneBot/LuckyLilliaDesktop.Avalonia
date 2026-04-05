using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LuckyLilliaDesktop.Views;

public enum FrameworkActionResult { Start, Reinstall, OpenDir, ViewDocs, Cancel }

public partial class FrameworkActionDialog : Window
{
    public bool AutoStartChecked => AutoStartCheckBox.IsChecked == true;

    public FrameworkActionDialog()
    {
        InitializeComponent();
    }

    public FrameworkActionDialog(string message, bool autoStartChecked, bool showAutoStart = true) : this()
    {
        MessageText.Text = message;
        AutoStartCheckBox.IsChecked = autoStartChecked;
        AutoStartCheckBox.IsVisible = showAutoStart;
    }

    private void OnStartClick(object? sender, RoutedEventArgs e) => Close(FrameworkActionResult.Start);
    private void OnReinstallClick(object? sender, RoutedEventArgs e) => Close(FrameworkActionResult.Reinstall);
    private void OnOpenDirClick(object? sender, RoutedEventArgs e) => Close(FrameworkActionResult.OpenDir);
    private void OnViewDocsClick(object? sender, RoutedEventArgs e) => Close(FrameworkActionResult.ViewDocs);
    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(FrameworkActionResult.Cancel);
}
