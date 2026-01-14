# 进程管理规范

## PMHQ 与 QQ 进程的关系

- PMHQ 是启动器，负责启动 QQ 进程后就会自动退出
- 判断登录状态时应该使用 QQ 进程状态，而不是 PMHQ 进程状态
- 错误示例：`_processManager.GetProcessStatus("PMHQ")`
- 正确示例：`_processManager.GetProcessStatus("QQ")`

## SelfInfo 获取时序

- UIN 会先于 Nickname 获取到
- 不能将 UIN 和 Nickname 放在一起判断
- `ISelfInfoService` 提供分开的订阅流：
  - `UinStream` - UIN 获取到时推送
  - `NicknameStream` - 昵称获取到时推送
- 轮询会持续到获取到 Nickname 或 QQ 进程退出
