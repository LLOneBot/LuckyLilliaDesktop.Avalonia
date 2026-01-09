using System.Text.Json.Serialization;

namespace LuckyLilliaDesktop.Models;

/// <summary>
/// 应用配置 - 与 Python 版本的 config_manager.py 保持一致
/// </summary>
public class AppConfig
{
    // 路径配置
    [JsonPropertyName("qq_path")]
    public string QQPath { get; set; } = string.Empty;

    [JsonPropertyName("pmhq_path")]
    public string PmhqPath { get; set; } = "bin/pmhq/pmhq-win-x64.exe";

    [JsonPropertyName("llbot_path")]
    public string LLBotPath { get; set; } = "bin/llbot/llbot.js";

    [JsonPropertyName("node_path")]
    public string NodePath { get; set; } = "bin/llbot/node.exe";

    // PMHQ 配置
    [JsonPropertyName("pmhq_port")]
    public int PmhqPort { get; set; } = 11451;

    // 启动选项
    [JsonPropertyName("auto_login_qq")]
    public string AutoLoginQQ { get; set; } = string.Empty;

    [JsonPropertyName("auto_start_bot")]
    public bool AutoStartBot { get; set; } = false;

    [JsonPropertyName("headless")]
    public bool Headless { get; set; } = false;

    [JsonPropertyName("minimize_to_tray_on_start")]
    public bool MinimizeToTrayOnStart { get; set; } = false;

    [JsonPropertyName("startup_command_enabled")]
    public bool StartupCommandEnabled { get; set; } = false;

    [JsonPropertyName("startup_command")]
    public string StartupCommand { get; set; } = string.Empty;

    // 日志设置
    [JsonPropertyName("log_save_enabled")]
    public bool LogSaveEnabled { get; set; } = true;

    [JsonPropertyName("log_retention_seconds")]
    public int LogRetentionSeconds { get; set; } = 604800; // 默认 7 天

    // 关闭行为
    [JsonPropertyName("close_to_tray")]
    public bool? CloseToTray { get; set; } = null;

    // 窗口设置
    [JsonPropertyName("theme_mode")]
    public string ThemeMode { get; set; } = "dark";

    [JsonPropertyName("window_width")]
    public double WindowWidth { get; set; } = 1200.0;

    [JsonPropertyName("window_height")]
    public double WindowHeight { get; set; } = 800.0;

    [JsonPropertyName("window_left")]
    public double? WindowLeft { get; set; } = null;

    [JsonPropertyName("window_top")]
    public double? WindowTop { get; set; } = null;

    // 兼容性属性 - 只读取不写入
    [JsonPropertyName("auto_login")]
    public bool AutoLogin { get; set; } = true;

    /// <summary>
    /// 默认配置
    /// </summary>
    public static AppConfig Default => new();
}
