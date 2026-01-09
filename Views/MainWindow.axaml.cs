using Avalonia;
using Avalonia.Controls;
using LuckyLilliaDesktop.Services;
using LuckyLilliaDesktop.ViewModels;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace LuckyLilliaDesktop.Views;

public partial class MainWindow : Window
{
    private IConfigManager? _configManager;
    private ILogger<LoginDialog>? _loginDialogLogger;
    private bool _windowPositionLoaded;

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnWindowClosing;
        PositionChanged += OnPositionChanged;
        SizeChanged += OnSizeChanged;
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        await LoadWindowPositionAsync();
        await CheckMinimizeToTrayOnStartAsync();
    }

    private async Task CheckMinimizeToTrayOnStartAsync()
    {
        if (_configManager == null) return;
        
        try
        {
            var config = await _configManager.LoadConfigAsync();
            if (config.MinimizeToTrayOnStart)
            {
                await Task.Delay(100);
                MinimizeToTray();
            }
        }
        catch { }
    }

    private async Task LoadWindowPositionAsync()
    {
        if (_windowPositionLoaded || _configManager == null) return;
        _windowPositionLoaded = true;

        try
        {
            var config = await _configManager.LoadConfigAsync();
            
            // 只有位置有效时才恢复（避免负值过大的情况）
            if (config.WindowLeft.HasValue && config.WindowTop.HasValue 
                && config.WindowLeft.Value > -1000 && config.WindowTop.Value > -1000)
            {
                Position = new PixelPoint((int)config.WindowLeft.Value, (int)config.WindowTop.Value);
            }
            
            Width = config.WindowWidth > 0 ? config.WindowWidth : 900;
            Height = config.WindowHeight > 0 ? config.WindowHeight : 600;
        }
        catch { }
    }

    private void OnPositionChanged(object? sender, PixelPointEventArgs e)
    {
        ScheduleSaveWindowPosition();
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ScheduleSaveWindowPosition();
    }

    private CancellationTokenSource? _savePositionCts;
    
    private void ScheduleSaveWindowPosition()
    {
        if (_configManager == null || !_windowPositionLoaded) return;
        
        // 窗口最小化时不保存位置
        if (WindowState == WindowState.Minimized) return;
        
        // 检查位置是否有效（负值过大说明窗口在屏幕外）
        if (Position.X < -1000 || Position.Y < -1000) return;
        
        _savePositionCts?.Cancel();
        _savePositionCts = new CancellationTokenSource();
        var token = _savePositionCts.Token;
        
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(2000, token);
                if (token.IsCancellationRequested) return;
                
                await _configManager.SetSettingAsync("window_left", Position.X);
                await _configManager.SetSettingAsync("window_top", Position.Y);
                await _configManager.SetSettingAsync("window_width", (int)Width);
                await _configManager.SetSettingAsync("window_height", (int)Height);
            }
            catch (OperationCanceledException) { }
            catch { }
        }, token);
    }

    private async Task SaveWindowPositionImmediatelyAsync()
    {
        if (_configManager == null || !_windowPositionLoaded) return;
        
        _savePositionCts?.Cancel();
        
        if (WindowState == WindowState.Minimized) return;
        if (Position.X < -1000 || Position.Y < -1000) return;
        
        try
        {
            await _configManager.SetSettingAsync("window_left", Position.X);
            await _configManager.SetSettingAsync("window_top", Position.Y);
            await _configManager.SetSettingAsync("window_width", (int)Width);
            await _configManager.SetSettingAsync("window_height", (int)Height);
        }
        catch { }
    }

    /// <summary>
    /// 公开的保存窗口状态方法，供 App 调用
    /// </summary>
    public async Task SaveWindowStateAsync()
    {
        await SaveWindowPositionImmediatelyAsync();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is MainWindowViewModel vm)
        {
            var app = Application.Current as App;
            _configManager = app?.Services?.GetService(typeof(IConfigManager)) as IConfigManager;
            _loginDialogLogger = app?.Services?.GetService(typeof(ILogger<LoginDialog>)) as ILogger<LoginDialog>;
            var pmhqClient = app?.Services?.GetService(typeof(IPmhqClient)) as IPmhqClient;
            
            vm.HomeVM.ConfirmDialog = ShowConfirmDialogAsync;
            vm.HomeVM.ChoiceDialog = ShowChoiceDialogAsync;
            vm.HomeVM.ShowLoginDialog = port => ShowLoginDialogAsync(pmhqClient!, port);
            vm.HomeVM.ShowLoginDialogWithHeadless = (port, headless) => ShowLoginDialogAsync(pmhqClient!, port, headless);
            vm.HomeVM.ShowAlertDialog = ShowAlertDialogAsync;
        }
    }

    private async Task ShowAlertDialogAsync(string title, string message)
    {
        var dialog = new AlertDialog(message);
        await dialog.ShowDialog<object?>(this);
    }

    private async Task<bool> ShowConfirmDialogAsync(string title, string message)
    {
        var dialog = new ConfirmDialog(message);
        var result = await dialog.ShowDialog<bool?>(this);
        return result == true;
    }

    private async Task<int> ShowChoiceDialogAsync(string title, string message, string option1, string option2)
    {
        var dialog = new ChoiceDialog(title, message, option1, option2);
        var result = await dialog.ShowDialog<int?>(this);
        return result ?? -1;
    }

    private async Task<string?> ShowLoginDialogAsync(IPmhqClient pmhqClient, int port)
    {
        return await ShowLoginDialogAsync(pmhqClient, port, false);
    }

    private async Task<string?> ShowLoginDialogAsync(IPmhqClient pmhqClient, int port, bool isHeadlessMode)
    {
        while (true)
        {
            var dialog = new LoginDialog(pmhqClient, port, _loginDialogLogger, isHeadlessMode);
            var result = await dialog.ShowDialog<bool?>(this);
            
            if (result == true)
                return dialog.LoggedInUin;
            
            if (dialog.IsLoginFailed && isHeadlessMode)
            {
                var confirmDialog = new ConfirmDialog(dialog.LoginFailedReason ?? "登录失败");
                await confirmDialog.ShowDialog<bool?>(this);
                continue;
            }
            
            return null;
        }
    }

    private bool _isClosingHandled;
    
    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_isClosingHandled) return;
        
        var closeToTray = _configManager?.GetSetting<bool?>("close_to_tray", null);

        if (closeToTray == true)
        {
            e.Cancel = true;
            MinimizeToTray();
        }
        else if (closeToTray == false)
        {
            _isClosingHandled = true;
            e.Cancel = true;
            await ExitAsync();
        }
        else
        {
            e.Cancel = true;
            await ShowCloseDialogAsync();
        }
    }

    private async Task ExitAsync()
    {
        if (Application.Current is App app)
        {
            await app.ExitApplicationAsync();
        }
    }

    private async Task ShowCloseDialogAsync()
    {
        var dialog = new CloseDialog();
        var result = await dialog.ShowDialog<CloseDialogResult?>(this);

        if (result == null) return;

        if (result.RememberChoice && _configManager != null)
        {
            await _configManager.SetSettingAsync("close_to_tray", result.MinimizeToTray);
        }

        if (result.MinimizeToTray)
        {
            MinimizeToTray();
        }
        else
        {
            _isClosingHandled = true;
            await ExitAsync();
        }
    }

    private void MinimizeToTray()
    {
        // 隐藏窗口
        Hide();
    }

    public void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }
}

/// <summary>
/// 关闭对话框结果
/// </summary>
public class CloseDialogResult
{
    public bool MinimizeToTray { get; set; }
    public bool RememberChoice { get; set; }
}
