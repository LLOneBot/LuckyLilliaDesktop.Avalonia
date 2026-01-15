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
            var dialog = new ThreeChoiceDialog(message);
            var result = await dialog.ShowDialog<ThreeChoiceResult>(window);
            return (int)result;
        }
        return 2; // Cancel
    }
}
