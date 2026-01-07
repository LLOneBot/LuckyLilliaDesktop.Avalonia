using Avalonia.Controls;
using Avalonia.Markup.Xaml;

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
}
