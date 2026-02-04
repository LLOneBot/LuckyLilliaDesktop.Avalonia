# Job Object 进程自动清理机制

## 问题背景

当 GUI 程序被任务管理器强制终止时，无法捕获到这个事件，导致子进程（PMHQ、LLBot、QQ 等）会残留在系统中。

## 解决方案

### 1. Job Objects 自动清理（主要方案）

使用 Windows Job Objects 机制，将主进程和所有子进程关联到一个 Job 中。当主进程被杀死时，操作系统会自动终止 Job 中的所有进程。

### 2. 完全等待机制（停止按钮）

点击停止按钮时，确保所有进程完全退出后才返回：
- 使用 `taskkill /F /T` 强制终止进程树
- 等待 `taskkill` 命令完成
- 轮询检查进程是否真正退出（最多等待 10 秒）
- 超时后记录警告但不阻塞

## 实现原理

### Job Object 自动清理

#### 1. Job Object 是什么？

Job Object 是 Windows 提供的进程组管理机制，可以：
- 将多个进程作为一个整体管理
- 设置资源限制（CPU、内存等）
- 自动清理：当 Job 关闭时，自动终止所有关联的进程

#### 2. 关键设置

```csharp
// 设置标志：JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
// 当 Job Object 句柄关闭时，自动杀死所有进程
info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
```

#### 3. 工作流程

1. **程序启动时**：
   - 创建 Job Object
   - 设置 `KILL_ON_JOB_CLOSE` 标志
   - 将当前进程（主进程）加入 Job

2. **启动子进程时**：
   - 子进程会自动继承父进程的 Job
   - 所有子进程都在同一个 Job 中

3. **程序退出时**：
   - 正常退出：调用 `Dispose()` 清理 Job，所有子进程被终止
   - 异常退出：操作系统自动清理 Job，所有子进程被终止
   - 被任务管理器杀死：操作系统自动清理 Job，所有子进程被终止

### 完全等待机制

#### 停止流程

```csharp
public async Task StopAllAsync(int? qqPid = null)
{
    // 1. 停止 LLBot（完全等待）
    await StopLLBotAsync();
    
    // 2. 停止 PMHQ（完全等待）
    await StopPmhqAsync();
    
    // 3. 停止 QQ（完全等待）
    if (qqPid.HasValue)
    {
        await KillProcessTreeAsync(qqPid.Value);
    }
}
```

#### KillProcessTreeAsync 实现

```csharp
private static async Task KillProcessTreeAsync(int pid, int timeoutMs = 10000)
{
    // 1. 使用 taskkill 强制终止进程树
    var killProcess = Process.Start("taskkill", $"/F /T /PID {pid}");
    
    // 2. 等待 taskkill 命令完成
    await killProcess.WaitForExitAsync();
    
    // 3. 轮询检查进程是否真正退出
    await WaitForProcessExitAsync(pid, timeoutMs);
}
```

#### 等待验证

```csharp
private static async Task WaitForProcessExitAsync(int pid, int timeoutMs = 10000)
{
    var sw = Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < timeoutMs)
    {
        try
        {
            Process.GetProcessById(pid);  // 如果进程存在，不抛异常
            await Task.Delay(200);        // 等待 200ms 后重试
        }
        catch (ArgumentException)
        {
            return;  // 进程已退出
        }
    }
    // 超时后返回，不阻塞
}
```

## 优势

### Job Object 方案
1. **无需捕获退出事件**：操作系统级别的保证
2. **覆盖所有退出场景**：
   - 正常关闭
   - 崩溃
   - 任务管理器强制结束
   - 系统关机/重启
3. **自动继承**：子进程的子进程也会被管理
4. **零性能开销**：由操作系统内核管理

### 完全等待机制
1. **确保进程完全退出**：不会出现"僵尸进程"
2. **用户体验好**：停止按钮点击后，确保所有进程都已清理
3. **超时保护**：最多等待 10 秒，避免无限阻塞
4. **双重保险**：
   - 先用 `taskkill /F /T` 强制终止
   - 再轮询验证进程是否真正退出

## 改进对比

### 之前的问题

```csharp
// ❌ 问题 1：taskkill 启动后立即返回，不等待完成
Process.Start("taskkill", $"/F /T /PID {pid}");

// ❌ 问题 2：只等待 2 秒，可能进程还没退出
process.WaitForExit(2000);

// ❌ 问题 3：没有验证进程是否真正退出
```

### 现在的实现

```csharp
// ✅ 改进 1：等待 taskkill 命令完成
var killProcess = Process.Start("taskkill", ...);
await killProcess.WaitForExitAsync();

// ✅ 改进 2：轮询验证进程退出，最多等待 10 秒
await WaitForProcessExitAsync(pid, 10000);

// ✅ 改进 3：通过 Process.GetProcessById 验证进程是否存在
try {
    Process.GetProcessById(pid);  // 存在则继续等待
} catch (ArgumentException) {
    return;  // 不存在则已退出
}
```

## 测试方法

1. 启动程序
2. 在程序中启动一些子进程
3. 使用任务管理器强制结束主进程
4. 检查子进程是否已自动清理

## 注意事项

1. **仅 Windows 支持**：Job Objects 是 Windows 特有功能
2. **嵌套限制**：如果程序本身已在某个 Job 中运行，可能无法创建新 Job
3. **调试器影响**：在调试器中运行时，Job Object 可能不生效

## 代码位置

- 实现：`Services/ProcessManager.cs`
- 测试：`test_job_object.ps1`

## 参考资料

- [Job Objects (Windows)](https://docs.microsoft.com/en-us/windows/win32/procthread/job-objects)
- [JOBOBJECT_EXTENDED_LIMIT_INFORMATION](https://docs.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_extended_limit_information)
