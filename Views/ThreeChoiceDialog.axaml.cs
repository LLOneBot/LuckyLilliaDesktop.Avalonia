using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LuckyLilliaDesktop.Views;

public enum ThreeChoiceResult { Primary, Secondary, Cancel }

public partial class ThreeChoiceDialog : Window
{
    public ThreeChoiceDialog()
    {
        InitializeComponent();
    }

    public ThreeChoiceDialog(string message, string primaryText = "启动", string secondaryText = "重新安装") : this()
    {
        MessageText.Text = message;
        PrimaryButton.Content = primaryText;
        SecondaryButton.Content = secondaryText;
    }

    private void OnPrimaryClick(object? sender, RoutedEventArgs e) => Close(ThreeChoiceResult.Primary);
    private void OnSecondaryClick(object? sender, RoutedEventArgs e) => Close(ThreeChoiceResult.Secondary);
    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(ThreeChoiceResult.Cancel);
}
