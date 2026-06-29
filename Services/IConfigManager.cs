using LuckyLilliaDesktop.Models;
using System;
using System.Threading.Tasks;

namespace LuckyLilliaDesktop.Services;

/// <summary>
/// 配置管理服务接口
/// </summary>
public interface IConfigManager
{
    /// <summary>配置保存成功后触发, 参数为最新配置. 供 UI 即时响应配置变化.</summary>
    event Action<AppConfig>? ConfigSaved;

    Task<AppConfig> LoadConfigAsync();
    Task<bool> SaveConfigAsync(AppConfig config);
    T GetSetting<T>(string key, T defaultValue);
    Task SetSettingAsync<T>(string key, T value);
}
