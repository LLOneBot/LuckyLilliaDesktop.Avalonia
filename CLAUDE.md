# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Lucky Lillia Desktop (幸运莉莉娅桌面端) is a cross-platform desktop control panel for LLBot (QQ robot framework). Built with Avalonia UI + ReactiveUI on .NET 8.0, it manages the lifecycle of PMHQ (QQ protocol handler), LLBot, and various bot frameworks (Koishi, AstrBot, Zhenxun, etc.).

The codebase is primarily in Chinese (comments, UI strings, log messages).

## Build & Run Commands

```bash
# Restore and build
dotnet restore
dotnet build

# Run in development
dotnet run

# Publish for Windows (single-file self-contained exe)
dotnet publish -c Release -r win-x64

# Publish for macOS
./build-macos-app.sh arm64

# Run tests (test project is standalone, not referenced by main .sln)
dotnet test LuckyLilliaDesktop.Tests/LuckyLilliaDesktop.Tests.csproj

# Run a single test
dotnet test LuckyLilliaDesktop.Tests/LuckyLilliaDesktop.Tests.csproj --filter "FullyQualifiedName~TestMethodName"
```

## Architecture

### MVVM with Reactive Extensions

- **ViewModels** extend `ViewModelBase` (which extends ReactiveUI's `ReactiveObject`). Properties use `[Reactive]` attribute (via ReactiveUI.Fody, configured in `FodyWeavers.xml`) or `RaiseAndSetIfChanged`. Commands use `ReactiveCommand`.
- **Views** are Avalonia AXAML files. Pages live under `Views/Pages/`, dialogs under `Views/Dialogs/`.
- **Services** are interface-based, all registered as **singletons** in DI. ViewModels are **transient**.

### Dependency Injection

Configured in `App.axaml.cs:ConfigureServices()` using `Microsoft.Extensions.DependencyInjection`. The `IServiceProvider` is stored as `App.Services` and accessed by ViewModels to resolve dependencies.

### Navigation

Tab-based navigation via `MainWindowViewModel.SelectedIndex` (Home → Log → Config → LLBotConfig → IntegrationWizard → About). ViewModels receive navigation callbacks as `Action` delegates (e.g., `HomeViewModel.NavigateToLogs` sets `SelectedIndex = 1`). Dialogs are injected into ViewModels as `Func<T>` delegates for loose coupling.

### Key Service Responsibilities

| Service | Role |
|---------|------|
| `ProcessManager` | Start/stop/monitor PMHQ and LLBot processes. Uses Windows Job Objects for subprocess cleanup. |
| `ResourceMonitor` | Real-time CPU/memory monitoring via `IObservable<T>` streams. |
| `PmhqClient` | HTTP client to local PMHQ API (QR login, account info, device info, SSE events). |
| `ConfigManager` | JSON config I/O (`app_settings.json`) with caching and raw JSON node preservation. |
| `*InstallService` (7 services) | Each handles installation of a specific bot framework (Koishi, AstrBot, Zhenxun, DDBot, Yunzai, ZeroBotPlugin, OpenClaw). |
| `PythonHelper` | Python/uv runtime management for Python-based bot frameworks. |
| `DownloadService` | File downloads with progress tracking and multi-mirror npm registry support (mirrors rotate on failure). |
| `SelfInfoService` | Polls PMHQ for QQ account info; provides separate `UinStream` and `NicknameStream` observables. |

### Process Management Rules

- **Non-headless startup order: PMHQ (which launches QQ) → LLBot.** Desktop does **not** launch QQ itself. Before starting PMHQ, `StartPmhqAsync` writes QQ's absolute path into `<pmhq>/pmhq_config.json` → `qq_path` (`UpdatePmhqConfigAsync`); PMHQ then launches QQ from that path. Because QQ is spawned by PMHQ — which lives inside Desktop's Job Object — QQ inherits into the same Job Object and follows Desktop's lifecycle: closing/killing Desktop tears QQ down too (intended; see [doc/job-object.md](doc/job-object.md)). Caveat: this relies on PMHQ **not** breaking QQ away from the job; if a PMHQ build spawns QQ with `CREATE_BREAKAWAY_FROM_JOB`, QQ would outlive Desktop — verify against the PMHQ in use. Desktop does **not** require admin (`app.manifest` is `asInvoker`); QQ runs at the same integrity level, so PMHQ can inject without admin.
- **PMHQ finds-or-launches QQ; it is not passed `--pid`.** `StartPmhqAsync` launches PMHQ with **no** `--pid` — PMHQ runs its own `find_existing_qq_pid()`: if a QQ that loaded `wrapper.node` is already running it attaches, otherwise it launches QQ from `qq_path` (see above). PMHQ exits 0 right after spawning QQ; the HTTP API is then served by the QQ process (`StartPmhqAsync` treats exit-code 0 as success, not failure). Desktop no longer pre-starts QQ or waits for it — the old `StartQQAsync` / `WaitForQQProcessAsync` are gone. Still check QQ status via `GetProcessStatus("QQ")` / `FetchQQPidAsync`, not PMHQ.
- **auth_token feeds both PMHQ and LLBot.** `EnsureAuthTokenAsync` returns the token read from `<llbot>/data/auth_token.txt` (prompting via `AuthTokenDialog` + writing the file when missing). Non-headless passes the same value to PMHQ as `--auth-token` (PMHQ requires it — missing → it exits immediately); LLBot always reads the file itself. The headless path also calls `EnsureAuthTokenAsync` (to ensure the file exists for LLBot) but has no PMHQ to pass it to.
- **LLBot PMHQ mode** — non-headless, `StartLLBotAsync` passes arg `--pmhq-port=<port>` (LLBot connects to PMHQ over HTTP/WS; the arg's presence alone switches LLBot into PMHQ mode — no env var involved). Applied only when `PmhqPort` is set; headless leaves `PmhqPort` null, so the arg is absent and LLBot runs its direct-protocol mode.
- **macOS is headless-only** — `AppConfig.Headless`'s getter is `PlatformHelper.IsMacOS || _headless`, so on macOS it is **always** true regardless of the stored config value; `ConfigPage` disables the toggle via `ConfigViewModel.IsHeadlessForced` and shows a note. The non-headless path relies on Windows-only mechanisms (PMHQ injecting QQ, Job Objects), none of which work on macOS. The getter is the single choke point, so every `config.Headless` reader — startup dispatch, `ProcessManager`, config serialization — sees `true` with no per-site platform check. Windows/Linux fall through to `_headless` (default true, user-toggleable) and are unaffected. macOS uses the same headless flow as Windows headless: `HeadlessLoginDialog` (session quick-login list + QR) driven by LLBot IPC over Unix Domain Socket.
- **Headless mode bypasses PMHQ** — when `config.Headless` is true, `HomeViewModel.StartAllServicesAsync` dispatches to `StartHeadlessServicesAsync`, which skips QQ + PMHQ entirely and launches only LLBot (LLBot handles the QQ protocol itself, direct-protocol mode). No PMHQ port, no `PmhqClient` polling. Login state + UIN / nickname come from LLBot over the IPC channel (see below).
- **Headless scan-to-login** — after starting LLBot, `StartHeadlessServicesAsync` calls `WaitFirstLoginStateAsync` (polls `ILLBotIpcClient.CurrentLoginState`, ≤15s). If the first definite state is `logged_in` (LLBot session quick-login), it proceeds with no dialog. Otherwise it shows `HeadlessLoginDialog` (all platforms), which subscribes `LoginStateStream`, renders the QR PNG, advances through `waiting_confirm`, and closes returning the uin on `logged_in`. User cancel → stop LLBot + IPC and abort. If IPC never reports a state (old LLBot without `get_login_state`), it logs a warning and proceeds without the dialog.
- **LLBot IPC (cross-platform)** — Desktop generates a connection ID and passes it to LLBot via env `LL_IPC_PIPE`. **Windows**: named pipe (`luckylillia-llbot-{pid}-{guid}`, LLBot prepends `\\.\pipe\`, Desktop uses `NamedPipeClientStream`). **macOS/Linux**: Unix Domain Socket (`$TMPDIR/luckylillia-llbot-{pid}-{guid}.sock`, LLBot `net.createServer().listen(path)` after `fs.unlinkSync` cleanup, Desktop uses `Socket(AddressFamily.Unix)` + `UnixDomainSocketEndPoint` + `NetworkStream`). **LLBot is the server, Desktop is the client and polls.** JSON Lines, UTF-8, `\n`-delimited. Single method `get_login_state` covers the whole lifecycle: returns `{state, qrcode_png_base64}` while logging in, `{state:"logged_in", uin, nickname}` once online (states: `initializing`/`need_qrcode`/`waiting_confirm`/`logged_in`/`expired`/`cancelled`). `ILLBotIpcClient` exposes `LoginStateStream` (for `HeadlessLoginDialog`) and `SelfInfoStream` (fed on `logged_in`); polls 1s while logging in, 5s after. LLBot must auto-refresh the QR on `expired`/`cancelled` (no refresh method in the protocol). See [doc/llbot-ipc.md](doc/llbot-ipc.md).
- **SelfInfo timing** — UIN arrives before Nickname. `ISelfInfoService` exposes them as separate observable streams (`UinStream`, `NicknameStream`). Never gate logic on both being available simultaneously. Headless mode does not populate these streams.
- **Windows cleanup** — Windows Job Objects ensure the entire process tree is terminated. On macOS/Linux, relies on parent-child signal propagation.

### Platform-Specific Behavior

Cross-platform logic uses `PlatformHelper` (in `Utils/`). Key differences:
- **Windows**: Software rendering forced (`Win32RenderingMode.Software`), Windows Job Objects for process tree cleanup, Task Scheduler for startup, `System.Management` for WMI queries.
- **macOS**: `.app` bundle handling in `Program.cs`, App Translocation detection/recovery, `~/Library/Application Support/LuckyLilliaDesktop` as working directory when installed to `/Applications`.
- Working directory is set in `Program.cs` before the Avalonia app starts — this is critical because all relative paths (config, logs, binaries) depend on it.

### UI Theme Rules

All UI must support both light and dark themes. **Never hardcode colors** — use `DynamicResource` bindings:

| Purpose | Resource Key |
|---------|-------------|
| Primary background | `SystemControlBackgroundAltHighBrush` |
| Secondary background | `SystemControlBackgroundBaseLowBrush` |
| Card background | `CardBackground` |
| Primary text | `SystemControlForegroundBaseHighBrush` or `TextPrimary` |
| Secondary text | `SystemControlForegroundBaseMediumBrush` or `TextSecondary` |
| Border | `SystemControlForegroundBaseLowBrush` or `BorderColor` |
| Accent/primary button | `PrimaryBrush` |
| Danger action | `DangerColor` |

Exceptions: brand colors (e.g. `#6C7BFF`), QR code background (must be white), and other functionally-fixed colors are acceptable with justification. Always verify contrast in both themes.

### Configuration

`app_settings.json` stores runtime configuration with snake_case JSON property names. The `ConfigManager` preserves unknown JSON properties when writing back (via `JsonNode`), so config files remain forward-compatible.

`IConfigManager.ConfigSaved` (event, fired after a successful `SaveConfigAsync`) lets VMs react to config changes immediately. E.g. `HomeViewModel` subscribes to flip `IsHeadless` so the control panel hides/shows the QQ resource card the moment headless is toggled in `ConfigPage` — no restart or tab-switch needed.

### Logging

Serilog with console + file sinks. Log files are per-session (`logs/yyyyMMdd_HHmmss.log`), capped at 10MB with auto-roll, and cleaned up on startup (7-day retention, max 50 files).

## Topic Docs

| Doc | When to read |
|-----|--------------|
| [doc/config-persistence.md](doc/config-persistence.md) | 任何写 `app_settings.json` 的代码（`ConfigViewModel` / `IntegrationWizardViewModel` 等）：整体存回必须 read-modify-write，别覆盖其他 VM 维护的字段；`close_to_tray` 三态语义。 |
| [doc/job-object.md](doc/job-object.md) | Touching Windows Job Object subprocess cleanup in `ProcessManager`. |
| [doc/koishi.md](doc/koishi.md) | Working on `KoishiInstallService` / Koishi auto-install flow. |
| [doc/llbot-ipc.md](doc/llbot-ipc.md) | Anything touching the LLBot IPC (named pipe on Windows / Unix Domain Socket on macOS+Linux): message schema, env var, `ILLBotIpcClient` surface, LLBot Node.js side glue. |

## Release Process

Tags matching `v*` trigger the GitHub Actions workflow (`.github/workflows/release.yml`):
- Windows: `dotnet publish -c Release -r win-x64` → single-file exe → zip + npm package
- macOS: `build-macos-app.sh` → `.app` bundle → tar.gz + npm package — **temporarily disabled** (job commented out in the workflow; re-enable instructions are in its comment header)
- Both publish to GitHub Releases (**draft** — must be published manually on the Releases page) and npm registry with provenance

npm publish gotchas:
- The npm steps require the `NPM_TOKEN` repo secret (`HAS_NPM_TOKEN` gate). If it is missing, they are **silently skipped** — the run still shows success. If a release run succeeded but the version never appeared on npm, check this first.
- Manual `workflow_dispatch` runs never publish npm or create a release (`event_name == 'push'` gate; `ref_name` would be `main`, not the tag). Only a `v*` tag push publishes.
- To re-publish a version whose npm steps were skipped: delete the remote tag and push it again (`git push origin :refs/tags/vX.Y.Z && git push origin vX.Y.Z`) — same commit is fine, the re-push re-fires the workflow.

## Testing

Tests use xUnit and live in `LuckyLilliaDesktop.Tests/` (separate project, not in the solution file). Currently focused on string manipulation/config replacement correctness for bot framework installation services.
