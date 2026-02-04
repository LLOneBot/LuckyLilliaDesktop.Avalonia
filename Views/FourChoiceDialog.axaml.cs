using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LuckyLilliaDesktop.Views;

public enum FourChoiceResult { Primary, Secondary, Tertiary, Cancel }

public partial class FourChoiceDialog : Window
{
    public FourChoiceDialog()
    {
        InitializeComponent();
    }

    public FourChoiceDialog(string message, string primaryText = "启动", string secondaryText = "重新安装", string tertiaryText = "查看文档") : this()
    {
        MessageText.Text = message;
        PrimaryButton.Content = primaryText;
        SecondaryButton.Content = secondaryText;
        TertiaryButton.Content = tertiaryText;
    }

    private void OnPrimaryClick(object? sender, RoutedEventArgs e) => Close(FourChoiceResult.Primary);
    private void OnSecondaryClick(object? sender, RoutedEventArgs e) => Close(FourChoiceResult.Secondary);
    private void OnTertiaryClick(object? sender, RoutedEventArgs e) => Close(FourChoiceResult.Tertiary);
    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(FourChoiceResult.Cancel);
}
