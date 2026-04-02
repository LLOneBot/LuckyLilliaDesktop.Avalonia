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

- **ViewModels** extend `ViewModelBase` (which extends ReactiveUI's `ReactiveObject`). Properties use `[Reactive]` attribute (via ReactiveUI.Fody) or `RaiseAndSetIfChanged`. Commands use `ReactiveCommand`.
- **Views** are Avalonia AXAML files. Pages live under `Views/Pages/`, dialogs under `Views/Dialogs/`.
- **Services** are interface-based, all registered as **singletons** in DI. ViewModels are **transient**.

### Dependency Injection

Configured in `App.axaml.cs:ConfigureServices()` using `Microsoft.Extensions.DependencyInjection`. The `IServiceProvider` is stored as `App.Services` and accessed by ViewModels to resolve dependencies.

### Key Service Responsibilities

| Service | Role |
|---------|------|
| `ProcessManager` | Start/stop/monitor PMHQ and LLBot processes. Uses Windows Job Objects for subprocess cleanup. |
| `ResourceMonitor` | Real-time CPU/memory monitoring via `IObservable<T>` streams. |
| `PmhqClient` | HTTP client to local PMHQ API (QR login, account info, device info). |
| `ConfigManager` | JSON config I/O (`app_settings.json`) with caching and raw JSON node preservation. |
| `*InstallService` (7 services) | Each handles installation of a specific bot framework (Koishi, AstrBot, Zhenxun, DDBot, Yunzai, ZeroBotPlugin, OpenClaw). |
| `PythonHelper` | Python/uv runtime management for Python-based bot frameworks. |
| `DownloadService` | File downloads with progress tracking and multi-mirror npm registry support. |

### Platform-Specific Behavior

Cross-platform logic uses `PlatformHelper` (in `Utils/`). Key differences:
- **Windows**: Software rendering forced (`Win32RenderingMode.Software`), Windows Job Objects for process tree cleanup, Task Scheduler for startup, `System.Management` for WMI queries.
- **macOS**: `.app` bundle handling in `Program.cs`, App Translocation detection/recovery, `~/Library/Application Support/LuckyLilliaDesktop` as working directory when installed to `/Applications`.
- Working directory is set in `Program.cs` before the Avalonia app starts — this is critical because all relative paths (config, logs, binaries) depend on it.

### Configuration

`app_settings.json` stores runtime configuration. The `ConfigManager` preserves unknown JSON properties when writing back (via `JsonNode`), so config files remain forward-compatible.

### Logging

Serilog with console + file sinks. Log files are per-session (`logs/yyyyMMdd_HHmmss.log`), capped at 10MB with auto-roll, and cleaned up on startup (7-day retention, max 50 files).

## Release Process

Tags matching `v*` trigger the GitHub Actions workflow (`.github/workflows/release.yml`):
- Windows: `dotnet publish -c Release -r win-x64` → single-file exe → zip + npm package
- macOS: `build-macos-app.sh` → `.app` bundle → tar.gz + npm package
- Both publish to GitHub Releases (draft) and npm registry with provenance

## Testing

Tests use xUnit and live in `LuckyLilliaDesktop.Tests/` (separate project, not in the solution file). Currently focused on string manipulation/config replacement correctness for bot framework installation services.
