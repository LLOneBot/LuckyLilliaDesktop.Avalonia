using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using LuckyLilliaDesktop.Services;

namespace LuckyLilliaDesktop.Views;

/// <summary>
/// headless 直连模式的扫码登录对话框. 数据源是 LLBot 的 IPC 登录状态流 (get_login_state),
/// 不依赖 PMHQ. 登录成功 Close(uin); 用户取消 Close(null).
/// </summary>
public partial class QRLoginDialog : Window
{
    private readonly ILLBotIpcClient? _ipc;
    private IDisposable? _subscription;
    private string? _lastQrShown;

    public string? LoggedInUin { get; private set; }

    public QRLoginDialog()
    {
        InitializeComponent();
    }

    public QRLoginDialog(ILLBotIpcClient ipc) : this()
    {
        _ipc = ipc;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (_ipc == null) return;

        // 先用当前状态初始化 (可能订阅前就已经有二维码 / 已登录)
        if (_ipc.CurrentLoginState != null)
        {
            Apply(_ipc.CurrentLoginState);
        }

        _subscription = _ipc.LoginStateStream.Subscribe(info =>
            Dispatcher.UIThread.Post(() => Apply(info)));
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _subscription?.Dispose();
        _subscription = null;
    }

    private void Apply(LoginStateInfo info)
    {
        switch (info.State)
        {
            case "logged_in":
                LoggedInUin = info.Uin;
                Close(info.Uin);
                return;
            case "need_qrcode":
                if (info.HasQrCode) ShowQrCode(info.QrcodePngBase64!);
                StatusText.Text = "请使用手机QQ扫描二维码登录";
                break;
            case "waiting_confirm":
                StatusText.Text = "已扫描，请在手机上确认登录";
                break;
            case "expired":
                StatusText.Text = "二维码已过期，正在刷新...";
                break;
            case "cancelled":
                StatusText.Text = "已取消扫码，正在刷新二维码...";
                break;
            case "initializing":
                StatusText.Text = "正在初始化...";
                break;
            default:
                StatusText.Text = info.State;
                break;
        }
    }

    private void ShowQrCode(string base64)
    {
        // 同一张二维码不重复解码 (轮询会反复推送同一张)
        if (_lastQrShown == base64) return;
        _lastQrShown = base64;

        try
        {
            var raw = base64;
            if (raw.StartsWith("data:"))
            {
                var comma = raw.IndexOf(',');
                if (comma > 0) raw = raw[(comma + 1)..];
            }

            var bytes = Convert.FromBase64String(raw);
            using var stream = new MemoryStream(bytes);
            QRImage.Source = new Bitmap(stream);
            QRImage.IsVisible = true;
            QRLoading.IsVisible = false;
        }
        catch
        {
            QRLoading.Text = "二维码加载失败";
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
