using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LuckyLilliaDesktop.Views;

public partial class CloseDialog : Window
{
    public CloseDialog()
    {
        InitializeComponent();
    }

    private void OnMinimizeToTray(object? sender, RoutedEventArgs e)
    {
        var result = new CloseDialogResult
        {
            MinimizeToTray = true,
            RememberChoice = RememberCheckBox.IsChecked == true
        };
        Close(result);
    }

    private void OnExit(object? sender, RoutedEventArgs e)
    {
        var result = new CloseDialogResult
        {
            MinimizeToTray = false,
            RememberChoice = RememberCheckBox.IsChecked == true
        };
        Close(result);
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}
