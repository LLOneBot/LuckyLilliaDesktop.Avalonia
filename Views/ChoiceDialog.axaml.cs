using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace LuckyLilliaDesktop.Views;

public partial class ChoiceDialog : Window
{
    private TextBlock TitleText = null!;
    private TextBlock MessageText = null!;
    private Button Option1Button = null!;
    private Button Option2Button = null!;

    public ChoiceDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        TitleText = this.FindControl<TextBlock>("TitleText")!;
        MessageText = this.FindControl<TextBlock>("MessageText")!;
        Option1Button = this.FindControl<Button>("Option1Button")!;
        Option2Button = this.FindControl<Button>("Option2Button")!;
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
