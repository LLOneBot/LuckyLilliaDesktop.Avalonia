using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using LuckyLilliaDesktop.Services;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LuckyLilliaDesktop.Views;

public partial class LoginDialog : Window
{
    private readonly IPmhqClient? _pmhqClient;
    private readonly int _port;
    private readonly bool _isHeadlessMode;
    private List<LoginAccount> _accounts = [];
    private LoginAccount? _selectedAccount;
    private CancellationTokenSource? _pollCts;
    private CancellationTokenSource? _sseCts;
    private bool _isQRCodeMode;

    public string? LoggedInUin { get; private set; }
    public string? LoginFailedReason { get; private set; }
    public bool IsLoginFailed { get; private set; }

    public LoginDialog()
    {
        InitializeComponent();
    }

    public LoginDialog(IPmhqClient pmhqClient, int port, object? _ = null, bool isHeadlessMode = false) : this()
    {
        _pmhqClient = pmhqClient;
        _port = port;
        _isHeadlessMode = isHeadlessMode;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _ = LoadAccountsAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _pollCts?.Cancel();
        _sseCts?.Cancel();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close(false);

    private async Task LoadAccountsAsync()
    {
        if (_pmhqClient == null) return;

        ShowLoadingPanel();

        var info = await _pmhqClient.FetchSelfInfoAsync();
        if (info != null && !string.IsNullOrEmpty(info.Uin))
        {
            LoggedInUin = info.Uin;
            Close(true);
            return;
        }

        var accounts = await _pmhqClient.GetLoginListAsync();
        if (accounts != null && accounts.Count > 0)
        {
            _accounts = accounts;
            _selectedAccount = _accounts[0];
            ShowQuickLoginMode();
        }
        else
        {
            ShowQRCodeMode();
        }
    }

    private void ShowLoadingPanel()
    {
        LoadingPanel.IsVisible = true;
        QuickLoginPanel.IsVisible = false;
        QRCodePanel.IsVisible = false;
        AccountListPanel.IsVisible = false;
        TitleText.Text = "登录";
    }

    private void ShowQuickLoginMode()
    {
        _sseCts?.Cancel();
        _isQRCodeMode = false;
        TitleText.Text = "快速登录";
        LoadingPanel.IsVisible = false;
        QuickLoginPanel.IsVisible = true;
        QRCodePanel.IsVisible = false;
        AccountListPanel.IsVisible = false;

        if (_selectedAccount != null)
        {
            NicknameText.Text = _selectedAccount.NickName;
            UinText.Text = _selectedAccount.Uin;
            StatusText.IsVisible = false;
            _ = LoadAvatarAsync(_selectedAccount.FaceUrl, AvatarImage, DefaultAvatar);
        }

        SwitchAccountButton.IsVisible = _accounts.Count > 1;
        QRCodeModeButton.IsVisible = true;
        LoginButton.IsEnabled = true;
    }

    private void ShowQRCodeMode()
    {
        _sseCts?.Cancel();
        _isQRCodeMode = true;
        TitleText.Text = "扫码登录";
        LoadingPanel.IsVisible = false;
        QuickLoginPanel.IsVisible = false;
        QRCodePanel.IsVisible = true;
        AccountListPanel.IsVisible = false;

        QRCodeLoading.IsVisible = true;
        QRCodeImage.IsVisible = false;
        QRCodeTipText.Text = "正在获取二维码...";
        QuickLoginModeButton.IsVisible = _accounts.Count > 0;

        _ = StartQRCodeLoginAsync();
    }

    private void ShowAccountList()
    {
        TitleText.Text = "选择账号";
        LoadingPanel.IsVisible = false;
        QuickLoginPanel.IsVisible = false;
        QRCodePanel.IsVisible = false;
        AccountListPanel.IsVisible = true;

        BuildAccountList();
    }

    private void BuildAccountList()
    {
        var panel = new StackPanel { Spacing = 4 };
        
        foreach (var account in _accounts)
        {
            var avatarBorder = new Border
            {
                Width = 44, Height = 44,
                CornerRadius = new Avalonia.CornerRadius(22),
                ClipToBounds = true,
                Background = new SolidColorBrush(Color.Parse("#4D4D4D"))
            };

            var avatarImage = new Image { Stretch = Stretch.UniformToFill };
            avatarBorder.Child = avatarImage;
            _ = LoadAvatarAsync(account.FaceUrl, avatarImage, null);

            var infoPanel = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Avalonia.Thickness(12, 0, 0, 0)
            };
            infoPanel.Children.Add(new TextBlock
            {
                Text = account.NickName, FontSize = 14,
                Foreground = new SolidColorBrush(Color.Parse("#E0E0E0"))
            });
            infoPanel.Children.Add(new TextBlock
            {
                Text = account.Uin, FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#808080")),
                Margin = new Avalonia.Thickness(0, 2, 0, 0)
            });

            var grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto") };
            Grid.SetColumn(avatarBorder, 0);
            Grid.SetColumn(infoPanel, 1);
            grid.Children.Add(avatarBorder);
            grid.Children.Add(infoPanel);

            var arrow = new TextBlock
            {
                Text = "›", FontSize = 20,
                Foreground = new SolidColorBrush(Color.Parse("#606060")),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(arrow, 2);
            grid.Children.Add(arrow);

            var btn = new Button
            {
                Content = grid, Tag = account.Uin,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Color.Parse("#3D3D3D")),
                CornerRadius = new Avalonia.CornerRadius(8),
                Padding = new Avalonia.Thickness(12, 10),
                Margin = new Avalonia.Thickness(0, 2)
            };
            btn.Click += OnAccountItemClick;
            panel.Children.Add(btn);
        }

        AccountList.ItemsSource = null;
        AccountList.Items.Clear();
        AccountList.Items.Add(panel);
    }

    private static async Task LoadAvatarAsync(string url, Image targetImage, PathIcon? defaultIcon)
    {
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var bytes = await http.GetByteArrayAsync(url);
            using var stream = new MemoryStream(bytes);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                targetImage.Source = new Bitmap(stream);
                if (defaultIcon != null) defaultIcon.IsVisible = false;
            });
        }
        catch
        {
            if (defaultIcon != null)
                await Dispatcher.UIThread.InvokeAsync(() => defaultIcon.IsVisible = true);
        }
    }

    private async Task StartQRCodeLoginAsync()
    {
        _sseCts = new CancellationTokenSource();
        var ct = _sseCts.Token;

        Log.Information("启动 SSE 监听, port={Port}", _port);
        _ = Task.Run(() => ListenSSEAsync(ct), ct);

        await Task.Delay(500, ct);

        Log.Information("请求二维码");
        if (_pmhqClient != null)
        {
            var result = await _pmhqClient.RequestQRCodeAsync(ct);
            Log.Information("请求二维码结果: {Result}", result);
        }

        StartPollingForLogin();
    }

    private async Task ListenSSEAsync(CancellationToken ct)
    {
        var url = $"http://127.0.0.1:{_port}";
        Log.Information("[SSE] 开始连接: {Url}", url);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                Log.Information("[SSE] 发送 GET 请求...");
                
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                Log.Information("[SSE] 响应状态: {StatusCode}", response.StatusCode);
                
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                Log.Information("[SSE] 开始读取流...");

                var buffer = new StringBuilder();

                while (!ct.IsCancellationRequested)
                {
                    var chunk = new char[4096];
                    var read = await reader.ReadAsync(chunk, 0, chunk.Length);
                    if (read == 0)
                    {
                        Log.Information("[SSE] 流结束");
                        break;
                    }

                    buffer.Append(chunk, 0, read);
                    var content = buffer.ToString();

                    // 按 \n\n 分割消息
                    while (content.Contains("\n\n"))
                    {
                        var idx = content.IndexOf("\n\n", StringComparison.Ordinal);
                        var message = content[..idx];
                        content = content[(idx + 2)..];
                        buffer.Clear();
                        buffer.Append(content);

                        ProcessSSEMessage(message);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Log.Information("[SSE] 已取消");
                break;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[SSE] 连接异常: {Message}", ex.Message);
                if (!ct.IsCancellationRequested)
                {
                    await Task.Delay(1000, ct);
                    Log.Information("[SSE] 重试连接...");
                }
            }
        }
        Log.Information("[SSE] 监听结束");
    }

    private void ProcessSSEMessage(string message)
    {
        Log.Information("[SSE] 处理消息: {Message}", message);
        
        foreach (var line in message.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("data:")) continue;

            var json = trimmed[5..].Trim();
            if (string.IsNullOrEmpty(json)) continue;

            Log.Information("[SSE] 解析 JSON: {Json}", json);

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("type", out var typeElem) &&
                    typeElem.GetString() == "nodeIKernelLoginListener" &&
                    root.TryGetProperty("data", out var dataElem))
                {
                    if (dataElem.TryGetProperty("sub_type", out var subType))
                    {
                        var subTypeStr = subType.GetString();
                        Log.Information("[SSE] 事件类型: {SubType}", subTypeStr);

                        if (subTypeStr == "onQRCodeGetPicture" &&
                            dataElem.TryGetProperty("data", out var qrData) &&
                            qrData.TryGetProperty("pngBase64QrcodeData", out var base64Elem))
                        {
                            var base64 = base64Elem.GetString();
                            Log.Information("[SSE] 收到二维码, 长度={Length}", base64?.Length ?? 0);
                            if (!string.IsNullOrEmpty(base64))
                            {
                                Dispatcher.UIThread.Post(() => ShowQRCodeImage(base64));
                            }
                        }
                        else if (subTypeStr == "onQuickLoginFailed" &&
                                 dataElem.TryGetProperty("data", out var failData))
                        {
                            var errMsg = "";
                            if (failData.TryGetProperty("loginErrorInfo", out var errorInfo) &&
                                errorInfo.TryGetProperty("errMsg", out var errMsgElem))
                            {
                                errMsg = errMsgElem.GetString() ?? "登录失败";
                            }
                            
                            Log.Warning("[SSE] 快速登录失败: {ErrMsg}", errMsg);
                            Dispatcher.UIThread.Post(() => HandleLoginFailed(errMsg));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[SSE] JSON 解析失败");
            }
        }
    }

    private void HandleLoginFailed(string errMsg)
    {
        LoginFailedReason = errMsg;
        IsLoginFailed = true;
        
        _pollCts?.Cancel();
        _sseCts?.Cancel();
        
        if (_isHeadlessMode)
        {
            Close(false);
        }
        else
        {
            StatusText.Text = errMsg;
            StatusText.Foreground = Brushes.OrangeRed;
            StatusText.IsVisible = true;
            LoginButton.IsEnabled = true;
        }
    }

    private void ShowQRCodeImage(string base64)
    {
        try
        {
            if (base64.StartsWith("data:"))
            {
                var commaIdx = base64.IndexOf(',');
                if (commaIdx > 0) base64 = base64[(commaIdx + 1)..];
            }

            Log.Information("解析二维码 base64, 长度={Length}", base64.Length);
            var bytes = Convert.FromBase64String(base64);
            Log.Information("二维码图片大小: {Size} bytes", bytes.Length);
            
            using var stream = new MemoryStream(bytes);
            QRCodeImage.Source = new Bitmap(stream);
            QRCodeImage.IsVisible = true;
            QRCodeLoading.IsVisible = false;
            QRCodeTipText.Text = "请使用手机QQ扫描二维码登录";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "显示二维码失败");
            QRCodeTipText.Text = "二维码加载失败";
        }
    }

    private void StartPollingForLogin()
    {
        _pollCts?.Cancel();
        _pollCts = new CancellationTokenSource();
        var ct = _pollCts.Token;

        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested && _pmhqClient != null)
            {
                var info = await _pmhqClient.FetchSelfInfoAsync(ct);
                if (info != null && !string.IsNullOrEmpty(info.Uin))
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        LoggedInUin = info.Uin;
                        Close(true);
                    });
                    return;
                }
                try { await Task.Delay(1000, ct); }
                catch (OperationCanceledException) { break; }
            }
        }, ct);
    }

    private async void OnLoginClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedAccount == null || _pmhqClient == null) return;

        LoginButton.IsEnabled = false;
        StatusText.Text = "登录中...";
        StatusText.Foreground = Brushes.Gray;
        StatusText.IsVisible = true;

        Log.Information("[Login] 开始登录, UIN={Uin}, Port={Port}", _selectedAccount.Uin, _port);

        // 启动 SSE 监听以接收登录失败事件
        _sseCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await ListenSSEAsync(_sseCts.Token);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[SSE] Task 异常");
            }
        }, _sseCts.Token);

        var success = await _pmhqClient.QuickLoginAsync(_selectedAccount.Uin);
        Log.Information("[Login] QuickLogin 结果: {Success}", success);
        
        if (success)
        {
            StatusText.Text = "登录成功，请稍候...";
            StartPollingForLogin();
        }
        else
        {
            StatusText.Text = "登录失败，请重试";
            StatusText.Foreground = Brushes.OrangeRed;
            LoginButton.IsEnabled = true;
        }
    }

    private void OnSwitchAccountClick(object? sender, RoutedEventArgs e) => ShowAccountList();
    private void OnQRCodeModeClick(object? sender, RoutedEventArgs e) => ShowQRCodeMode();

    private void OnQuickLoginModeClick(object? sender, RoutedEventArgs e)
    {
        _sseCts?.Cancel();
        ShowQuickLoginMode();
    }

    private void OnAccountItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string uin)
        {
            _selectedAccount = _accounts.Find(a => a.Uin == uin);
            ShowQuickLoginMode();
        }
    }

    private void OnBackFromAccountList(object? sender, RoutedEventArgs e)
    {
        if (_isQRCodeMode) ShowQRCodeMode();
        else ShowQuickLoginMode();
    }
}
