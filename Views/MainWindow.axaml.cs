using Avalonia;
using Avalonia.Controls;
using LuckyLilliaDesktop.Services;
using LuckyLilliaDesktop.ViewModels;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace LuckyLilliaDesktop.Views;

public partial class MainWindow : Window
{
    private IConfigManager? _configManager;
    private IProcessManager? _processManager;
    private bool _forceClose;
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
                
                await _configManager.SetSettingAsync("window_left", (double)Position.X);
                await _configManager.SetSettingAsync("window_top", (double)Position.Y);
                await _configManager.SetSettingAsync("window_width", Width);
                await _configManager.SetSettingAsync("window_height", Height);
            }
            catch (OperationCanceledException) { }
            catch { }
        }, token);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is MainWindowViewModel vm)
        {
            var app = Application.Current as App;
            _configManager = app?.Services?.GetService(typeof(IConfigManager)) as IConfigManager;
            _processManager = app?.Services?.GetService(typeof(IProcessManager)) as IProcessManager;
            
            vm.HomeVM.ConfirmDialog = ShowConfirmDialogAsync;
            vm.HomeVM.ChoiceDialog = ShowChoiceDialogAsync;
        }
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

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_forceClose)
        {
            // 强制关闭，执行清理
            await CleanupAndCloseAsync();
            return;
        }

        // 检查是否有保存的关闭行为
        var closeToTray = _configManager?.GetSetting<bool?>("close_to_tray", null);

        if (closeToTray == true)
        {
            // 最小化到托盘
            e.Cancel = true;
            MinimizeToTray();
        }
        else if (closeToTray == false)
        {
            // 直接退出
            await CleanupAndCloseAsync();
        }
        else
        {
            // 首次关闭，显示对话框
            e.Cancel = true;
            await ShowCloseDialogAsync();
        }
    }

    private async Task ShowCloseDialogAsync()
    {
        var dialog = new CloseDialog();
        var result = await dialog.ShowDialog<CloseDialogResult?>(this);

        if (result == null)
        {
            // 用户取消
            return;
        }

        // 保存用户选择
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
            _forceClose = true;
            Close();
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

    private async Task CleanupAndCloseAsync()
    {
        try
        {
            // 停止所有进程
            if (_processManager != null)
            {
                await _processManager.StopAllAsync();
            }
        }
        catch
        {
            // 忽略清理错误
        }
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
