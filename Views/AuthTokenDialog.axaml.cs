using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace LuckyLilliaDesktop.Views;

public partial class AuthTokenDialog : Window
{
    private const string AuthTokenUrl = "https://s.luckylillia.com";

    public AuthTokenDialog()
    {
        InitializeComponent();
    }

    private void OnLinkClick(object? sender, PointerPressedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(AuthTokenUrl) { UseShellExecute = true });
        }
        catch
        {
            // 打开浏览器失败时静默忽略，用户可手动复制链接
        }
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var text = InputBox.Text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            ErrorText.Text = "Auth Token 不能为空";
            ErrorText.IsVisible = true;
            InputBox.Focus();
            return;
        }
        Close(text);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}
