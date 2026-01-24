using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;

namespace LuckyLilliaDesktop.Views;

public partial class AutoCloseAlertDialog : Window
{
    private readonly DispatcherTimer _timer;
    private readonly int _delaySeconds;
    private bool _actionTriggered;

    public Action? OnDelayElapsed { get; set; }

    public AutoCloseAlertDialog()
    {
        InitializeComponent();
        _delaySeconds = 3;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_delaySeconds) };
        _timer.Tick += OnTimerTick;
    }

    public AutoCloseAlertDialog(string message, int seconds = 3) : this()
    {
        MessageText.Text = message;
        _timer.Interval = TimeSpan.FromSeconds(seconds);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _timer.Start();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (!_actionTriggered)
        {
            _actionTriggered = true;
            OnDelayElapsed?.Invoke();
        }
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        _timer.Stop();
        
        // 用户点击确定时也应该执行回调
        if (!_actionTriggered)
        {
            _actionTriggered = true;
            OnDelayElapsed?.Invoke();
        }
        
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}
