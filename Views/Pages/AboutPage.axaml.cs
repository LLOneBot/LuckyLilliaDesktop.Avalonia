using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using LuckyLilliaDesktop.Utils;

namespace LuckyLilliaDesktop.Views.Pages;

public partial class AboutPage : UserControl
{
    public AboutPage()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTree;
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        var appIconImage = this.FindControl<Image>("AppIconImage");
        if (appIconImage is null) return;

        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        appIconImage.Source = BitmapLoader.DecodeAssetToWidth(
            "avares://LuckyLilliaDesktop/Assets/Icons/icon.png",
            56,
            scaling);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
