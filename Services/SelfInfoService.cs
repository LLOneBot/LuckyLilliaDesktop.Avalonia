using Microsoft.Extensions.Logging;
using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;

namespace LuckyLilliaDesktop.Services;

public interface ISelfInfoService
{
    IObservable<string> UinStream { get; }
    IObservable<string> NicknameStream { get; }
    string? CurrentUin { get; }
    string? CurrentNickname { get; }
    void Stop();
}

public class SelfInfoService : ISelfInfoService, IDisposable
{
    private readonly ILogger<SelfInfoService> _logger;
    private readonly IResourceMonitor _resourceMonitor;
    private readonly IPmhqClient _pmhqClient;
    private readonly Subject<string> _uinSubject = new();
    private readonly Subject<string> _nicknameSubject = new();
    private readonly IDisposable _pidSubscription;
    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private string? _cachedUin;
    private string? _cachedNickname;

    public IObservable<string> UinStream => _uinSubject;
    public IObservable<string> NicknameStream => _nicknameSubject;
    public string? CurrentUin => _cachedUin;
    public string? CurrentNickname => _cachedNickname;

    public SelfInfoService(
        ILogger<SelfInfoService> logger,
        IResourceMonitor resourceMonitor,
        IPmhqClient pmhqClient)
    {
        _logger = logger;
        _resourceMonitor = resourceMonitor;
        _pmhqClient = pmhqClient;

        _pidSubscription = _resourceMonitor.QQPidStream
            .ObserveOn(System.Reactive.Concurrency.TaskPoolScheduler.Default)
            .Subscribe(OnQQPidChanged);
    }

    private void OnQQPidChanged(int? pid)
    {
        if (pid.HasValue && pid.Value > 0)
        {
            StartPolling();
        }
        else
        {
            StopPolling();
            Reset();
        }
    }

    private void StartPolling()
    {
        if (_pollTask != null && !_pollTask.IsCompleted)
            return;

        _cts = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollLoopAsync(_cts.Token), _cts.Token);
        _logger.LogDebug("SelfInfo 轮询已启动");
    }

    private void StopPolling()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _logger.LogDebug("SelfInfo 轮询已停止");
    }

    public void Stop() => StopPolling();

    private void Reset()
    {
        if (_cachedUin != null)
        {
            _cachedUin = null;
            _uinSubject.OnNext(string.Empty);
        }
        if (_cachedNickname != null)
        {
            _cachedNickname = null;
            _nicknameSubject.OnNext(string.Empty);
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (!string.IsNullOrEmpty(_cachedNickname))
                {
                    await Task.Delay(5000, ct);
                    continue;
                }

                await TryFetchSelfInfoAsync();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SelfInfo 轮询出错");
        }
    }

    private async Task TryFetchSelfInfoAsync()
    {
        if (!_pmhqClient.HasPort)
            return;

        try
        {
            var selfInfo = await _pmhqClient.FetchSelfInfoAsync();
            if (selfInfo == null)
                return;

            if (!string.IsNullOrEmpty(selfInfo.Uin) && _cachedUin != selfInfo.Uin)
            {
                _cachedUin = selfInfo.Uin;
                _logger.LogInformation("获取到 UIN: {Uin}", selfInfo.Uin);
                _uinSubject.OnNext(selfInfo.Uin);
            }

            if (!string.IsNullOrEmpty(selfInfo.Nickname) && _cachedNickname != selfInfo.Nickname)
            {
                _cachedNickname = selfInfo.Nickname;
                _logger.LogInformation("获取到昵称: {Nickname}", selfInfo.Nickname);
                _nicknameSubject.OnNext(selfInfo.Nickname);
            }
        }
        catch { }
    }

    public void Dispose()
    {
        StopPolling();
        _pidSubscription.Dispose();
        _uinSubject.Dispose();
        _nicknameSubject.Dispose();
        GC.SuppressFinalize(this);
    }
}
