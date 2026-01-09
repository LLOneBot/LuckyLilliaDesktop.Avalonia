using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LuckyLilliaDesktop.Views;

public partial class AlertDialog : Window
{
    public AlertDialog()
    {
        InitializeComponent();
    }

    public AlertDialog(string message) : this()
    {
        MessageText.Text = message;
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e) => Close();
}
