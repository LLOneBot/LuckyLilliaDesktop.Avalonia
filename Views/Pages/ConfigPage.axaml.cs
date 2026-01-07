using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace LuckyLilliaDesktop.Views.Pages;

public partial class ConfigPage : UserControl
{
    public ConfigPage()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
