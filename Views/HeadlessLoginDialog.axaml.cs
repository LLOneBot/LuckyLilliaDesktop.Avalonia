using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using LuckyLilliaDesktop.Services;

namespace LuckyLilliaDesktop.Views;

/// <summary>
/// 无头模式登录框 (一个框搞定): 快速登录账号列表 (扫 session 文件) + 扫码登录。
/// 选账号 -&gt; onStart(uin) 启动 LLBot 走快速登录; 选扫码 -&gt; onStart(null) 启动 LLBot 后框内显示二维码。
/// 数据/状态全程走 LLBot IPC (LoginStateStream)。登录成功 Close(uin), 取消 Close(null)。
/// </summary>
public partial class HeadlessLoginDialog : Window
{
    private readonly ILLBotIpcClient? _ipc;
    private readonly List<LoginAccount> _accounts;
    private readonly Func<string?, Task<bool>>? _onStart;
    private IDisposable? _subscription;
    private string? _lastQrShown;
    private bool _started;

    public string? LoggedInUin { get; private set; }

    public HeadlessLoginDialog()
    {
        InitializeComponent();
        _accounts = new List<LoginAccount>();
    }

    public HeadlessLoginDialog(ILLBotIpcClient ipc, List<LoginAccount> accounts, Func<string?, Task<bool>> onStart) : this()
    {
        _ipc = ipc;
        _accounts = accounts ?? new List<LoginAccount>();
        _onStart = onStart;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // 没有 session 账号 -> 直接走扫码登录 (不显示空账号列表)
        if (_accounts.Count == 0)
        {
            _ = BeginLoginAsync(null);
            return;
        }
        AccountList.ItemsSource = _accounts;
        ShowPanel(AccountPanel);
        foreach (var acc in _accounts) _ = LoadAvatarAsync(acc);
    }

    // 用 QQ 公开头像 URL 异步下载头像, 完成后通过 LoginAccount.Avatar 通知 UI
    private static async Task LoadAvatarAsync(LoginAccount acc)
    {
        if (string.IsNullOrEmpty(acc.FaceUrl)) return;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var bytes = await http.GetByteArrayAsync(acc.FaceUrl);
            using var stream = new MemoryStream(bytes);
            var bmp = new Bitmap(stream);
            await Dispatcher.UIThread.InvokeAsync(() => acc.Avatar = bmp);
        }
        catch
        {
            // 头像加载失败保持占位背景
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _subscription?.Dispose();
        _subscription = null;
    }

    private void OnAccountClick(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is Control c && c.Tag is string uin)
        {
            _ = BeginLoginAsync(uin);
        }
    }

    private void OnScanClick(object? sender, Avalonia.Input.PointerPressedEventArgs e) => _ = BeginLoginAsync(null);

    private async Task BeginLoginAsync(string? uin)
    {
        if (_started || _onStart == null || _ipc == null) return;
        _started = true;

        // 选账号 -> 先进"登录中"; 选扫码 -> 直接进二维码面板。
        // 注意: 快速登录若 session 过期, LLBot 会自动转扫码, IPC 会推 need_qrcode, 届时切到二维码面板。
        if (uin != null)
        {
            WaitingText.Text = $"正在登录 {uin} ...";
            ShowPanel(WaitingPanel);
        }
        else
        {
            ShowPanel(QRPanel);
        }

        // 先订阅再启动, 不漏状态
        _subscription = _ipc.LoginStateStream.Subscribe(info =>
            Dispatcher.UIThread.Post(() => Apply(info)));

        var ok = await _onStart(uin);
        if (!ok)
        {
            WaitingText.Text = "启动失败，请检查日志";
            ShowPanel(WaitingPanel);
        }
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
                ShowPanel(QRPanel);
                if (info.HasQrCode) ShowQrCode(info.QrcodePngBase64!);
                QRTip.Text = "请使用手机QQ扫描二维码登录";
                break;
            case "waiting_confirm":
                ShowPanel(QRPanel);
                QRTip.Text = "已扫描，请在手机上确认登录";
                break;
            case "expired":
                ShowPanel(QRPanel);
                QRTip.Text = "二维码已过期，正在刷新...";
                break;
            case "cancelled":
                ShowPanel(QRPanel);
                QRTip.Text = "已取消扫码，正在刷新二维码...";
                break;
            // initializing 等其它状态保持当前面板 (登录中 / 二维码加载)
        }
    }

    private void ShowQrCode(string base64)
    {
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

    private void ShowPanel(Control panel)
    {
        AccountPanel.IsVisible = ReferenceEquals(panel, AccountPanel);
        WaitingPanel.IsVisible = ReferenceEquals(panel, WaitingPanel);
        QRPanel.IsVisible = ReferenceEquals(panel, QRPanel);
        // 扫码登录文字仅在账号列表时显示 (登录中/二维码阶段不需要)
        ScanText.IsVisible = ReferenceEquals(panel, AccountPanel);
    }

    private void OnCancelClick(object? sender, Avalonia.Input.PointerPressedEventArgs e) => Close(null);
}
