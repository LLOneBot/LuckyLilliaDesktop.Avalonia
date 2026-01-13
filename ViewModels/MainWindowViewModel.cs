using ReactiveUI;
using Avalonia;
using Avalonia.Styling;
using LuckyLilliaDesktop.Services;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;

namespace LuckyLilliaDesktop.ViewModels;

/// <summary>
/// 主窗口 ViewModel
/// </summary>
public class MainWindowViewModel : ViewModelBase
{
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly IConfigManager _configManager;

    private int _selectedIndex;
    private string _title = "LLBot";
    private bool _isDarkTheme = true;

    public string Title
    {
        get => _title;
        set => this.RaiseAndSetIfChanged(ref _title, value);
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedIndex, value);
    }

    public bool IsDarkTheme
    {
        get => _isDarkTheme;
        set => this.RaiseAndSetIfChanged(ref _isDarkTheme, value);
    }

    // 子页面 ViewModels
    public HomeViewModel HomeVM { get; }
    public LogViewModel LogVM { get; }
    public ConfigViewModel ConfigVM { get; }
    public LLBotConfigViewModel LLBotConfigVM { get; }
    public IntegrationWizardViewModel IntegrationWizardVM { get; }
    public AboutViewModel AboutVM { get; }

    // 主题切换命令
    public ReactiveCommand<Unit, Unit> ToggleThemeCommand { get; }

    public MainWindowViewModel(
        HomeViewModel homeViewModel,
        LogViewModel logViewModel,
        ConfigViewModel configViewModel,
        LLBotConfigViewModel llbotConfigViewModel,
        IntegrationWizardViewModel integrationWizardViewModel,
        AboutViewModel aboutViewModel,
        IConfigManager configManager,
        ILogger<MainWindowViewModel> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));

        HomeVM = homeViewModel ?? throw new ArgumentNullException(nameof(homeViewModel));
        LogVM = logViewModel ?? throw new ArgumentNullException(nameof(logViewModel));
        ConfigVM = configViewModel ?? throw new ArgumentNullException(nameof(configViewModel));
        LLBotConfigVM = llbotConfigViewModel ?? throw new ArgumentNullException(nameof(llbotConfigViewModel));
        IntegrationWizardVM = integrationWizardViewModel ?? throw new ArgumentNullException(nameof(integrationWizardViewModel));
        AboutVM = aboutViewModel ?? throw new ArgumentNullException(nameof(aboutViewModel));

        // 加载保存的主题设置（异步初始化）
        _ = LoadThemeSettingsAsync();

        // 监听 QQ 信息变化更新标题
        homeViewModel.WhenAnyValue(x => x.QQUin, x => x.QQNickname)
            .Where(tuple => !string.IsNullOrEmpty(tuple.Item1))
            .Select(tuple => $"LLBot - {tuple.Item2}({tuple.Item1})")
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(newTitle => Title = newTitle);

        // 主题切换命令
        ToggleThemeCommand = ReactiveCommand.Create(ToggleTheme);

        // 设置导航到日志页面的回调
        homeViewModel.NavigateToLogs = () => SelectedIndex = 1;

        // 设置导航到关于页面的回调（用于更新）
        homeViewModel.NavigateToAbout = () => SelectedIndex = 5;

        // 下载完成后刷新版本信息
        homeViewModel.OnDownloadCompleted = async () => await AboutVM.LoadVersionsAsync();

        // 更新完成后重启服务的回调
        aboutViewModel.RestartServicesCallback = async () => await HomeVM.StartServicesAsync();

        _logger.LogInformation("MainWindowViewModel 已初始化");
    }

    private async Task LoadThemeSettingsAsync()
    {
        try
        {
            // 先确保配置已加载
            await _configManager.LoadConfigAsync();

            var themeMode = _configManager.GetSetting("theme_mode", "dark");
            IsDarkTheme = themeMode == "dark";
            ApplyTheme();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载主题设置失败");
        }
    }

    private void ToggleTheme()
    {
        IsDarkTheme = !IsDarkTheme;

        ApplyTheme();

        // 保存主题设置
        _ = _configManager.SetSettingAsync("theme_mode", IsDarkTheme ? "dark" : "light");

        _logger.LogInformation("主题切换为: {Theme}", IsDarkTheme ? "深色" : "浅色");
    }

    private void ApplyTheme()
    {
        if (Application.Current != null)
        {
            Application.Current.RequestedThemeVariant = IsDarkTheme
                ? ThemeVariant.Dark
                : ThemeVariant.Light;
        }
    }
}
