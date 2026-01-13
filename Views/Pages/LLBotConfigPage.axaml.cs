using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using LuckyLilliaDesktop.ViewModels;

namespace LuckyLilliaDesktop.Views.Pages;

public partial class LLBotConfigPage : UserControl
{
    public LLBotConfigPage()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        
        if (change.Property == IsVisibleProperty && change.NewValue is true)
        {
            if (DataContext is LLBotConfigViewModel vm)
            {
                _ = vm.OnPageEnterAsync();
            }
        }
    }
}
