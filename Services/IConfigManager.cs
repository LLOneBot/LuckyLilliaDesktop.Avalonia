using LuckyLilliaDesktop.Models;
using System.Threading.Tasks;

namespace LuckyLilliaDesktop.Services;

/// <summary>
/// 配置管理服务接口
/// </summary>
public interface IConfigManager
{
    Task<AppConfig> LoadConfigAsync();
    Task<bool> SaveConfigAsync(AppConfig config);
    T GetSetting<T>(string key, T defaultValue);
    Task SetSettingAsync<T>(string key, T value);
}
