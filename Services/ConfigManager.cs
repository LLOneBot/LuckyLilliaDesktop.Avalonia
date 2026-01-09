using LuckyLilliaDesktop.Models;
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace LuckyLilliaDesktop.Services;

public class ConfigManager : IConfigManager
{
    private const string ConfigPath = "app_settings.json";
    private AppConfig? _cachedConfig;
    private JsonObject? _rawJson;
    private readonly ILogger<ConfigManager> _logger;
    private readonly object _lock = new();

    public ConfigManager(ILogger<ConfigManager> logger)
    {
        _logger = logger;
    }

    public async Task<AppConfig> LoadConfigAsync()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                _logger.LogInformation("配置文件不存在，使用默认配置");
                _cachedConfig = AppConfig.Default;
                _rawJson = new JsonObject();
                return _cachedConfig;
            }

            var json = await File.ReadAllTextAsync(ConfigPath);
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                WriteIndented = true
            };

            _cachedConfig = JsonSerializer.Deserialize<AppConfig>(json, options) ?? AppConfig.Default;
            _rawJson = JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
            _logger.LogInformation("配置加载成功");
            return _cachedConfig;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载配置失败，使用默认配置");
            _cachedConfig = AppConfig.Default;
            _rawJson = new JsonObject();
            return _cachedConfig;
        }
    }

    public async Task<bool> SaveConfigAsync(AppConfig config)
    {
        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                WriteIndented = true
            };

            var json = JsonSerializer.Serialize(config, options);
            await File.WriteAllTextAsync(ConfigPath, json);

            _cachedConfig = config;
            _rawJson = JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
            _logger.LogInformation("配置保存成功");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存配置失败");
            return false;
        }
    }

    public T GetSetting<T>(string key, T defaultValue)
    {
        lock (_lock)
        {
            if (_rawJson == null) return defaultValue;
            
            try
            {
                if (_rawJson.TryGetPropertyValue(key, out var node) && node != null)
                {
                    var value = node.Deserialize<T>() ?? defaultValue;
                    _logger.LogDebug("读取设置: {Key}", key);
                    return value;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读取设置失败: {Key}", key);
            }
            
            return defaultValue;
        }
    }

    public async Task SetSettingAsync<T>(string key, T value)
    {
        try
        {
            string json;
            lock (_lock)
            {
                _rawJson ??= new JsonObject();
                _rawJson[key] = JsonSerializer.SerializeToNode(value);
                json = JsonSerializer.Serialize(_rawJson, new JsonSerializerOptions { WriteIndented = true });
            }
            await File.WriteAllTextAsync(ConfigPath, json);
            _logger.LogInformation("设置已保存: {Key} = {Value}", key, value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存设置失败: {Key}", key);
        }
    }
}
