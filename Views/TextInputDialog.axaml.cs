using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LuckyLilliaDesktop.Views;

public partial class TextInputDialog : Window
{
    public TextInputDialog()
    {
        InitializeComponent();
    }

    public TextInputDialog(string message, string watermark = "") : this()
    {
        MessageText.Text = message;
        InputBox.Watermark = watermark;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var text = InputBox.Text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            InputBox.Focus();
            return;
        }
        Close(text);
    }
}
