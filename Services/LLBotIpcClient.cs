using LuckyLilliaDesktop.Utils;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace LuckyLilliaDesktop.Services;

/// <summary>
/// LLBot 登录状态. State 取值由 LLBot 决定, Desktop 不强枚举, 约定:
///   initializing    - 连接 / 加载 session 中
///   need_qrcode     - 有二维码待扫 (QrCodeState.WaitingForScan)
///   waiting_confirm - 已扫码, 等待手机确认 (QrCodeState.WaitingForConfirm)
///   logged_in       - 已登录 (含 session 快速登录), 带 uin/nickname
///   expired         - 二维码过期
///   cancelled       - 用户取消
/// </summary>
public sealed record LoginStateInfo(string State, string? QrcodePngBase64, string Uin, string Nickname)
{
    public bool IsLoggedIn => State == "logged_in";
    public bool HasQrCode => !string.IsNullOrEmpty(QrcodePngBase64);
}

/// <summary>
/// LLBot IPC 客户端 (跨平台). LLBot 是 server, Desktop 是 client.
///
/// 流程:
///   1. Desktop 生成连接标识, 通过 env LL_IPC_PIPE 传给 LLBot:
///        - Windows: 管道名 luckylillia-llbot-{pid}-{guid} (不含 \\.\pipe\ 前缀)
///        - macOS/Linux: Unix Domain Socket 绝对路径 $TMPDIR/luckylillia-llbot-{pid}-{guid}.sock
///   2. LLBot 用 net.createServer().listen(path) 监听 (Node.js 会按传入路径自动选管道 / UDS).
///   3. Desktop 启 LLBot 后调用 StartAsync(): 重连循环 + 长连接内轮询 get_login_state.
///
/// 协议: JSON Lines, UTF-8, '\n' 分隔.
///   Desktop -&gt; LLBot: {"type":"request","id":"1","method":"get_login_state"}
///   LLBot   -&gt; Desktop:
///     {"type":"response","id":"1","data":{"state":"need_qrcode","qrcode_png_base64":"data:image/png;base64,..."}}
///     {"type":"response","id":"1","data":{"state":"logged_in","uin":"...","nickname":"..."}}
///     {"type":"response","id":"1","error":"..."}
/// </summary>
public interface ILLBotIpcClient
{
    /// <summary>已经协商好的连接标识 (Windows: 管道名; Unix: socket 路径), 没启动时为 null.</summary>
    string? PipeName { get; }
    /// <summary>self_info 流 (uin, nickname). 登录成功后推送, 兼容 HomeViewModel 现有订阅.</summary>
    IObservable<(string Uin, string Nickname)> SelfInfoStream { get; }
    /// <summary>登录状态流. 每次轮询都推送, 供二维码登录对话框消费.</summary>
    IObservable<LoginStateInfo> LoginStateStream { get; }
    /// <summary>当前 LLBot 是否在线 (已经连上一次, 还没断).</summary>
    bool IsConnected { get; }
    string? CurrentUin { get; }
    string? CurrentNickname { get; }
    /// <summary>最近一次拿到的登录状态, 没有则 null. 供新订阅者立即读取当前值.</summary>
    LoginStateInfo? CurrentLoginState { get; }

    /// <summary>生成一个连接标识, 同时开始连接循环. 返回值供调用方塞到 LLBot 的 env LL_IPC_PIPE.</summary>
    Task<string> StartAsync(CancellationToken ct = default);
    /// <summary>停止连接 + 轮询.</summary>
    Task StopAsync();
}

public sealed class LLBotIpcClient : ILLBotIpcClient, IDisposable
{
    private readonly ILogger<LLBotIpcClient> _logger;
    private readonly Subject<(string Uin, string Nickname)> _selfInfoSubject = new();
    private readonly Subject<LoginStateInfo> _loginStateSubject = new();

    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private string? _pipeName;
    private volatile bool _connected;
    private string? _cachedUin;
    private string? _cachedNickname;
    private LoginStateInfo? _currentLoginState;
    private long _nextId;

    // 轮询间隔: 登录中频繁 (二维码状态要及时), 登录成功后拉长.
    private static readonly TimeSpan FastInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SlowInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(1);
    private const int ConnectTimeoutMs = 1500;
    private const int RequestTimeoutMs = 3000;

    public LLBotIpcClient(ILogger<LLBotIpcClient> logger)
    {
        _logger = logger;
    }

    public string? PipeName => _pipeName;
    public IObservable<(string Uin, string Nickname)> SelfInfoStream => _selfInfoSubject;
    public IObservable<LoginStateInfo> LoginStateStream => _loginStateSubject;
    public bool IsConnected => _connected;
    public string? CurrentUin => _cachedUin;
    public string? CurrentNickname => _cachedNickname;
    public LoginStateInfo? CurrentLoginState => _currentLoginState;

    public Task<string> StartAsync(CancellationToken ct = default)
    {
        if (!PlatformHelper.IsWindows && !PlatformHelper.IsMacOS && !PlatformHelper.IsLinux)
        {
            throw new PlatformNotSupportedException("LLBotIpcClient 仅支持 Windows / macOS / Linux");
        }

        if (_pipeName != null)
        {
            return Task.FromResult(_pipeName);
        }

        // Windows: 短名, LLBot 侧拼 \\.\pipe\; Unix: 直接给绝对路径的 socket 文件.
        // 用 /tmp 而不是 Path.GetTempPath(): macOS 上 TMPDIR 是 /var/folders/xx/yy/T/ (66 字符),
        // 加文件名超过 sockaddr_un.sun_path 104 字节上限. /tmp -> /private/tmp 是符号链接, 但内核
        // bind/connect 时保存原始字符串不 resolve, 所以短路径可用. guid 缩到 8 位省字符.
        var shortGuid = Guid.NewGuid().ToString("N").Substring(0, 8);
        var id = $"luckylillia-llbot-{Environment.ProcessId}-{shortGuid}";
        _pipeName = PlatformHelper.IsWindows ? id : $"/tmp/{id}.sock";

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _runTask = Task.Run(() => RunAsync(_pipeName, token), token);
        _logger.LogInformation("LLBot IPC 客户端已启动, 等待连接: {Pipe}", _pipeName);
        return Task.FromResult(_pipeName);
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        _cts = null;
        _pipeName = null;

        try { cts?.Cancel(); } catch { }
        if (_runTask != null)
        {
            try { await _runTask; } catch { }
            _runTask = null;
        }
        cts?.Dispose();

        ResetCache();
        _connected = false;
    }

    private void ResetCache()
    {
        _currentLoginState = null;
        if (_cachedUin != null || _cachedNickname != null)
        {
            _cachedUin = null;
            _cachedNickname = null;
            _selfInfoSubject.OnNext((string.Empty, string.Empty));
        }
    }

    private async Task RunAsync(string pipeName, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Stream? stream = null;
            Socket? socket = null;
            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(ConnectTimeoutMs);

                if (PlatformHelper.IsWindows)
                {
                    var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    await pipe.ConnectAsync(connectCts.Token);
                    stream = pipe;
                }
                else
                {
                    // Unix Domain Socket. LLBot 侧 fs.existsSync(socket) 才会 listen, 所以
                    // socket 文件在 LLBot 起来前不存在, ConnectAsync 会抛 SocketException, 走安静重试.
                    socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(pipeName), connectCts.Token);
                    stream = new NetworkStream(socket, ownsSocket: true);
                    socket = null; // NetworkStream 接管所有权, dispose stream 会关 socket
                }

                _connected = true;
                _logger.LogInformation("LLBot IPC 已连接");

                await PollLoopAsync(stream, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (TimeoutException) { /* connect 超时, 安静重试 */ }
            catch (OperationCanceledException) { /* connect 超时 (CancellationToken 路径), 安静重试 */ }
            catch (IOException) { /* LLBot 还没 listen / 断开, 安静重试 */ }
            catch (SocketException) { /* UDS: 文件不存在 / 拒绝连接, 安静重试 */ }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LLBot IPC 连接异常, 1 秒后重试");
            }
            finally
            {
                _connected = false;
                if (stream != null)
                {
                    try { await stream.DisposeAsync(); } catch { }
                }
                socket?.Dispose();
            }

            if (ct.IsCancellationRequested) break;
            try { await Task.Delay(ReconnectDelay, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PollLoopAsync(Stream stream, CancellationToken ct)
    {
        var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)) { NewLine = "\n", AutoFlush = false };
        var reader = new StreamReader(stream, new System.Text.UTF8Encoding(false));

        // 循环退出靠异常 (IOException / SocketException / EOF 触发的 IOException) — 让 RunAsync 重连.
        // NamedPipeClientStream.IsConnected 在 Stream 基类上没有等价, UDS 侧断开只能靠读写抛错感知.
        while (!ct.IsCancellationRequested)
        {
            var info = await RequestLoginStateOnceAsync(writer, reader, ct);
            if (info != null)
            {
                _currentLoginState = info;
                _loginStateSubject.OnNext(info);
                if (info.IsLoggedIn)
                {
                    ApplySelfInfo(info.Uin, info.Nickname);
                }
            }

            // 登录成功后用慢节奏, 登录中 (二维码状态) 用快节奏
            var loggedIn = info?.IsLoggedIn ?? false;
            try { await Task.Delay(loggedIn ? SlowInterval : FastInterval, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task<LoginStateInfo?> RequestLoginStateOnceAsync(
        StreamWriter writer, StreamReader reader, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId).ToString();
        var payload = JsonSerializer.Serialize(new IpcRequest("request", id, "get_login_state"), IpcJsonContext.Default.IpcRequest);

        try
        {
            using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            reqCts.CancelAfter(RequestTimeoutMs);
            var token = reqCts.Token;

            await writer.WriteLineAsync(payload.AsMemory(), token);
            await writer.FlushAsync(token);

            while (!token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(token);
                if (line == null)
                {
                    // EOF: 对端关闭, 走 RunAsync 重连
                    throw new IOException("LLBot IPC 连接已关闭 (EOF)");
                }
                if (string.IsNullOrWhiteSpace(line)) continue;

                var parsed = TryParseResponse(line, id, out var data);
                if (parsed == ParseResult.MatchedSuccess) return data;
                if (parsed == ParseResult.MatchedError) return null;
                // 收到非匹配行, 继续读
            }
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 请求超时 (RequestTimeoutMs) — 保持连接, 下轮再试
            return null;
        }
        // OperationCanceledException (外层 ct 触发) 和 IOException/SocketException 交给 RunAsync 处理
    }

    private enum ParseResult { NotMatched, MatchedSuccess, MatchedError }

    private static ParseResult TryParseResponse(string line, string expectedId, out LoginStateInfo? data)
    {
        data = null;
        try
        {
            var response = JsonSerializer.Deserialize(line, IpcJsonContext.Default.IpcResponse);
            if (response?.Type != "response" || response.Id != expectedId)
                return ParseResult.NotMatched;
            if (!string.IsNullOrEmpty(response.Error) || response.Data == null)
                return ParseResult.MatchedError;

            data = new LoginStateInfo(
                response.Data.State ?? string.Empty,
                response.Data.QrcodePngBase64,
                response.Data.Uin ?? string.Empty,
                response.Data.Nickname ?? string.Empty);
            return ParseResult.MatchedSuccess;
        }
        catch (JsonException)
        {
            return ParseResult.NotMatched;
        }
    }

    private void ApplySelfInfo(string uin, string nickname)
    {
        if (!string.IsNullOrEmpty(uin) && !AccountInfoHelper.IsValidQQUin(uin))
        {
            _logger.LogWarning("忽略无效 LLBot IPC UIN: {Uin}", uin);
            return;
        }

        var changed = false;
        if (!string.IsNullOrEmpty(uin) && _cachedUin != uin)
        {
            _cachedUin = uin;
            changed = true;
        }
        if (!string.IsNullOrEmpty(nickname) && _cachedNickname != nickname)
        {
            _cachedNickname = nickname;
            changed = true;
        }
        if (changed)
        {
            _selfInfoSubject.OnNext((_cachedUin ?? "", _cachedNickname ?? ""));
        }
    }

    public void Dispose()
    {
        try { StopAsync().GetAwaiter().GetResult(); } catch { }
        _selfInfoSubject.Dispose();
        _loginStateSubject.Dispose();
        GC.SuppressFinalize(this);
    }
}

public sealed record IpcRequest(string Type, string Id, string Method);

public sealed record IpcResponse(string Type, string Id, IpcLoginData? Data, string? Error);

public sealed record IpcLoginData(string? State, string? QrcodePngBase64, string? Uin, string? Nickname);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(IpcRequest))]
[JsonSerializable(typeof(IpcResponse))]
internal partial class IpcJsonContext : JsonSerializerContext;
