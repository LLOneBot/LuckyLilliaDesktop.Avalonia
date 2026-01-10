using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using LuckyLilliaDesktop.ViewModels;
using System;
using System.Linq;

namespace LuckyLilliaDesktop.Views.Pages;

public partial class LogPage : UserControl
{
    private ListBox? _logListBox;
    private bool _scrollPending;

    public LogPage()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _logListBox = this.FindControl<ListBox>("LogListBox");
        
        if (_logListBox != null)
        {
            _logListBox.AddHandler(PointerPressedEvent, OnListBoxPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        }
        
        if (DataContext is LogViewModel vm)
        {
            vm.ScrollToBottomRequested += OnScrollToBottomRequested;
            vm.ClearSelectionRequested += OnClearSelectionRequested;
        }
        
        // 页面加载时自动滚动到底部
        Avalonia.Threading.Dispatcher.UIThread.Post(DoScrollToBottom, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    protected override void OnUnloaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is LogViewModel vm)
        {
            vm.ScrollToBottomRequested -= OnScrollToBottomRequested;
            vm.ClearSelectionRequested -= OnClearSelectionRequested;
        }
        base.OnUnloaded(e);
    }

    private void OnListBoxPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_logListBox == null) return;
        
        var point = e.GetCurrentPoint(_logListBox);
        if (point.Properties.IsLeftButtonPressed)
        {
            var item = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>();
            if (item != null)
            {
                item.IsSelected = !item.IsSelected;
                e.Handled = true;
                UpdateSelection();
            }
        }
    }

    private void UpdateSelection()
    {
        if (DataContext is LogViewModel vm && _logListBox != null)
        {
            vm.SelectedLogEntries.Clear();
            foreach (var item in _logListBox.Selection.SelectedItems.OfType<LogEntryViewModel>())
            {
                vm.SelectedLogEntries.Add(item);
            }
        }
    }

    private void OnScrollToBottomRequested()
    {
        // 防抖：如果已有滚动请求待处理，跳过
        if (_scrollPending) return;
        _scrollPending = true;
        
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            DoScrollToBottom();
            _scrollPending = false;
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void DoScrollToBottom()
    {
        try
        {
            if (_logListBox?.ItemCount > 0)
            {
                _logListBox.ScrollIntoView(_logListBox.ItemCount - 1);
            }
        }
        catch (Exception)
        {
            // 忽略滚动异常
        }
    }

    private void OnClearSelectionRequested()
    {
        _logListBox?.Selection.Clear();
        UpdateSelection();
    }
}
