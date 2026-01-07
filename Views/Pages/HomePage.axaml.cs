using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using LuckyLilliaDesktop.ViewModels;
using System.Collections.Specialized;

namespace LuckyLilliaDesktop.Views.Pages;

public partial class HomePage : UserControl
{
    private ScrollViewer? _recentLogsScrollViewer;

    public HomePage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _recentLogsScrollViewer = this.FindControl<ScrollViewer>("RecentLogsScrollViewer");
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is HomeViewModel vm)
        {
            vm.RecentLogs.CollectionChanged += OnRecentLogsChanged;
        }
    }

    private void OnRecentLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            _recentLogsScrollViewer?.ScrollToEnd();
        }
    }
}
