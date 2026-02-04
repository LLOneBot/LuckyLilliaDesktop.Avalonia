using Avalonia.Controls;

namespace LuckyLilliaDesktop.Views;

public partial class LoadingDialog : Window
{
    public LoadingDialog()
    {
        InitializeComponent();
    }

    public LoadingDialog(string message) : this()
    {
        MessageText.Text = message;
    }

    public void UpdateMessage(string message)
    {
        MessageText.Text = message;
    }
}
