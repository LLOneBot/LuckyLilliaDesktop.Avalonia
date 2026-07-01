using Microsoft.Extensions.Logging;
using System;
using System.Reactive.Subjects;
using LuckyLilliaDesktop.Utils;

namespace LuckyLilliaDesktop.Services;

public interface ISelfInfoService
{
    IObservable<string> UinStream { get; }
    IObservable<string> NicknameStream { get; }
    string? CurrentUin { get; }
    string? CurrentNickname { get; }
    void Stop();
}

/// <summary>
/// QQ 账号信息 (uin / 昵称) 的统一来源: LLBot IPC (SelfInfoStream).
/// PMHQ 已不再提供 getSelfInfo, 所以这里只是订阅 IPC 的 self_info, 转发成 Uin / Nickname
/// 两条流并缓存当前值 -- 让配置页 / 集成向导等消费者通过 ISelfInfoService 统一拿 uin,
/// 不必各自去接 IPC。有头 / 无头都走这条路 (IPC server 两种模式都启动)。
/// </summary>
public class SelfInfoService : ISelfInfoService, IDisposable
{
    private readonly ILogger<SelfInfoService> _logger;
    private readonly IDisposable _ipcSubscription;
    private readonly Subject<string> _uinSubject = new();
    private readonly Subject<string> _nicknameSubject = new();
    private string? _cachedUin;
    private string? _cachedNickname;

    public IObservable<string> UinStream => _uinSubject;
    public IObservable<string> NicknameStream => _nicknameSubject;
    public string? CurrentUin => _cachedUin;
    public string? CurrentNickname => _cachedNickname;

    public SelfInfoService(ILogger<SelfInfoService> logger, ILLBotIpcClient llbotIpc)
    {
        _logger = logger;
        // 单例, 构造即订阅 -> 早于任何登录, 不会错过 IPC 首次推送; 缓存供 transient VM 激活时直接读 CurrentUin.
        _ipcSubscription = llbotIpc.SelfInfoStream.Subscribe(info => OnIpcSelfInfo(info.Uin, info.Nickname));
    }

    private void OnIpcSelfInfo(string uin, string nickname)
    {
        // 空 uin = LLBot 退出 / 重置 (IPC StopAsync -> ResetCache 会推空): 清缓存并通知下游 HasUin -> false
        if (string.IsNullOrEmpty(uin))
        {
            if (_cachedUin != null) { _cachedUin = null; _uinSubject.OnNext(string.Empty); }
            if (_cachedNickname != null) { _cachedNickname = null; _nicknameSubject.OnNext(string.Empty); }
            return;
        }

        if (!AccountInfoHelper.IsValidQQUin(uin))
        {
            _logger.LogWarning("忽略无效 UIN (LLBot IPC): {Uin}", uin);
            return;
        }

        if (_cachedUin != uin)
        {
            _cachedUin = uin;
            _logger.LogInformation("UIN (LLBot IPC): {Uin}", uin);
            _uinSubject.OnNext(uin);
        }
        if (!string.IsNullOrEmpty(nickname) && _cachedNickname != nickname)
        {
            _cachedNickname = nickname;
            _nicknameSubject.OnNext(nickname);
        }
    }

    // IPC 生命周期由 HomeViewModel 管 (StartAsync / StopAsync), 这里不需要主动停什么.
    public void Stop() { }

    public void Dispose()
    {
        _ipcSubscription.Dispose();
        _uinSubject.Dispose();
        _nicknameSubject.Dispose();
        GC.SuppressFinalize(this);
    }
}
