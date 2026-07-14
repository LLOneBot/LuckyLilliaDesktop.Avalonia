# app_settings.json 持久化约定

`app_settings.json` 由 `ConfigManager` 读写，但**有多个 ViewModel / View 各自维护其中不同的字段**。因为
`ConfigManager.SaveConfigAsync` 是**整文件覆盖写**（`JsonSerializer.Serialize(config)` + `File.WriteAllTextAsync`，
不做任何 merge），任何持有部分字段的写入方如果 `new AppConfig { ... }` 只填自己那几项再存回，就会把**别的写入方
维护的字段冲成默认值**。

## 字段归属

| 字段 | JSON key | 维护方 |
|------|----------|--------|
| 路径 / 启动选项 / 代理 / 日志 / 关闭行为 | `qq_path` `pmhq_path` ... `headless` `http_proxy` `close_to_tray` 等 | `ConfigViewModel`（系统配置页） |
| 框架跟随启动 | `auto_start_frameworks` | `IntegrationWizardViewModel`（对接向导 / 框架操作对话框） |
| 关闭窗口"记住选择" | `close_to_tray` | `MainWindow.OnWindowClosing` 关闭对话框（走 `SetSettingAsync`，只改单 key，安全） |
| 各类中间态 | `window_left` `window_top` 等 | 其他零散写入 |

注意 `close_to_tray` 有**两个**写入方：系统配置页的下拉框（read-modify-write 整体存回）和关闭对话框的"记住选择"
（`SetSettingAsync("close_to_tray", ...)` 单 key 合并写）。两者写的是同一个 key，最后写入者生效；配置页每次 load 会读到最新值。

## 硬性约定：整体存回必须 read-modify-write

任何要通过 `ConfigManager.SaveConfigAsync(config)` 整体存回的写入方，**必须先 `LoadConfigAsync()` 拿到现有配置对象，
再只覆盖自己拥有的字段**，绝不能 `new AppConfig { ... }` 从零构造。`LoadConfigAsync()` 返回的是 `ConfigManager` 的
缓存实例，直接改字段再存回即可，这样本方不涉及的字段（含日后新增的）都原样保留。

- 正例：`ConfigViewModel.SaveConfigAsync` —— `var config = await _configManager.LoadConfigAsync(); config.QQPath = ...;`
- 正例：`IntegrationWizardViewModel` 切换 `auto_start_frameworks` —— load → 改 List → save
- 反例（已修复的 bug）：`ConfigViewModel.SaveConfigAsync` 曾 `new AppConfig { ...本页字段... }`，导致每次保存系统配置
  都把 `auto_start_frameworks` 冲成 `[]`、`close_to_tray` 冲成 `null`（框架跟随启动 + 关闭行为设置丢失）。

只改**单个 key** 时用 `ConfigManager.SetSettingAsync(key, value)` —— 它基于 `_rawJson` 合并写，天然安全，不受此约定约束。

## 三态字段 close_to_tray

`AppConfig.CloseToTray` 是 `bool?`，三态语义（见 `MainWindow.axaml.cs:OnWindowClosing`）：

- `null` —— 每次关闭都弹 `CloseDialog` 询问
- `true` —— 收进托盘
- `false` —— 直接退出

系统配置页用三选项 ComboBox 表达，`ConfigViewModel.CloseToTrayIndex` 负责 `int`(0/1/2) ↔ `bool?`(null/true/false) 映射。
