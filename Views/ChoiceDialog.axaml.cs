using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LuckyLilliaDesktop.Views;

public partial class ChoiceDialog : Window
{
    public ChoiceDialog()
    {
        InitializeComponent();
    }

    public ChoiceDialog(string title, string message, string option1, string option2) : this()
    {
        TitleText.Text = title;
        MessageText.Text = message;
        Option1Button.Content = option1;
        Option2Button.Content = option2;
    }

    private void OnOption1Click(object? sender, RoutedEventArgs e)
    {
        Close(0);
    }

    private void OnOption2Click(object? sender, RoutedEventArgs e)
    {
        Close(1);
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close(-1);
    }
}
