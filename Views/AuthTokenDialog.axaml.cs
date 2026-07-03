using System;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using LuckyLilliaDesktop.Services;

namespace LuckyLilliaDesktop.Views;

public partial class AuthTokenDialog : Window
{
    private const string AuthTokenUrl = "https://auth.luckylillia.com";

    private readonly IAuthTokenValidator? _validator;
    private bool _validating;
    // 上一次验证因网络/服务器问题无法判定 (Inconclusive): 再点一次"确定"即跳过验证放行。
    private bool _skipValidationConfirmed;

    // 无参构造: 供 axaml 设计器 / 未注入 validator 时回退 (不验证, 保持旧行为直接放行)。
    public AuthTokenDialog() : this(null)
    {
    }

    public AuthTokenDialog(IAuthTokenValidator? validator)
    {
        _validator = validator;
        InitializeComponent();

        // 用户改了 token: 撤销"跳过验证"确认并清掉旧错误, 让下一次点击重新走验证。
        InputBox.TextChanged += (_, _) =>
        {
            _skipValidationConfirmed = false;
            ErrorText.IsVisible = false;
        };
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

    private async void OnOkClick(object? sender, RoutedEventArgs e)
    {
        if (_validating)
        {
            return;
        }

        var text = InputBox.Text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            ShowError("Auth Token 不能为空");
            InputBox.Focus();
            return;
        }

        // 无 validator (设计器 / 未注入), 或用户已确认跳过验证: 直接放行。
        if (_validator == null || _skipValidationConfirmed)
        {
            Close(text);
            return;
        }

        SetValidating(true);
        AuthTokenValidationResult result;
        try
        {
            result = await _validator.ValidateAsync(text);
        }
        catch (Exception)
        {
            // validator 内部已兜底所有网络异常, 这里仅防御性归为"无法判定"。
            result = new AuthTokenValidationResult(
                AuthTokenValidationStatus.Inconclusive, "验证过程出错");
        }
        finally
        {
            SetValidating(false);
        }

        switch (result.Status)
        {
            case AuthTokenValidationStatus.Valid:
                Close(text);
                break;

            case AuthTokenValidationStatus.Invalid:
                _skipValidationConfirmed = false;
                ShowError(result.Message ?? "Auth Token 无效");
                InputBox.Focus();
                break;

            case AuthTokenValidationStatus.Inconclusive:
                // 宽松策略: 网络/服务器问题不判死 token, 让用户再点一次"确定"跳过验证继续。
                _skipValidationConfirmed = true;
                ShowError((result.Message ?? "无法验证 Auth Token")
                    + "\n再次点击“确定”将跳过验证继续。");
                break;
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    private void SetValidating(bool on)
    {
        _validating = on;
        OkButton.IsEnabled = !on;
        CancelButton.IsEnabled = !on;
        InputBox.IsEnabled = !on;
        OkButton.Content = on ? "验证中..." : "确定";
        if (on)
        {
            ErrorText.IsVisible = false;
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }
}
