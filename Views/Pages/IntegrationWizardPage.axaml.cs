using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using LuckyLilliaDesktop.ViewModels;
using System;
using System.Threading.Tasks;

namespace LuckyLilliaDesktop.Views.Pages;

public partial class IntegrationWizardPage : UserControl
{
    public IntegrationWizardPage()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is IntegrationWizardViewModel vm)
        {
            vm.ConfirmInstallCallback = ShowConfirmDialogAsync;
            vm.ShowAlertCallback = ShowAlertDialogAsync;
            vm.ShowAutoCloseAlertCallback = ShowAutoCloseAlertDialogAsync;
            vm.ThreeChoiceCallback = ShowThreeChoiceDialogAsync;
            vm.FourChoiceCallback = ShowFourChoiceDialogAsync;
            vm.OnPageEnter();
        }
    }

    private async Task<bool> ShowConfirmDialogAsync(string title, string message)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is Window window)
        {
            var dialog = new ConfirmDialog(message);
            var result = await dialog.ShowDialog<bool?>(window);
            return result == true;
        }
        return false;
    }

    private async Task ShowAlertDialogAsync(string title, string message)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is Window window)
        {
            var dialog = new AlertDialog(message);
            await dialog.ShowDialog(window);
        }
    }

    private async Task ShowAutoCloseAlertDialogAsync(string title, string message, int seconds, Action onDelayElapsed)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is Window window)
        {
            var dialog = new AutoCloseAlertDialog(message, seconds) { OnDelayElapsed = onDelayElapsed };
            await dialog.ShowDialog(window);
        }
    }

    private async Task<int> ShowThreeChoiceDialogAsync(string title, string message)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is Window window)
        {
            // 根据消息内容判断是安装场景还是已安装场景
            var isInstallScenario = message.Contains("是否下载并自动安装配置");
            var dialog = isInstallScenario 
                ? new ThreeChoiceDialog(message, "安装", "查看文档")
                : new ThreeChoiceDialog(message);
            var result = await dialog.ShowDialog<ThreeChoiceResult>(window);
            return (int)result;
        }
        return 2; // Cancel
    }

    private async Task<int> ShowFourChoiceDialogAsync(string title, string message)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is Window window)
        {
            var dialog = new FourChoiceDialog(message);
            var result = await dialog.ShowDialog<FourChoiceResult>(window);
            return (int)result;
        }
        return 3; // Cancel
    }
}
