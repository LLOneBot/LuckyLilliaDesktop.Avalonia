# LLBot IPC 协议 (Desktop &lt;-&gt; LLBot)

headless 模式下 Desktop 不启动 PMHQ, LLBot 走直连协议 (direct protocol) 自己连 QQ. Desktop 通过一条
Windows 命名管道向 LLBot 轮询**登录状态**: 登录中拿二维码 + 扫码进度, 登录后拿 uin / nickname.

**LLBot 是 server, Desktop 是 client.** Desktop 周期性轮询 LLBot.
**仅 Windows**, 由 `Services/LLBotIpcClient.cs` 实现.

## 握手

1. Desktop 启动 LLBot 之前生成管道名 `luckylillia-llbot-{desktop_pid}-{guid}` (不含 `\\.\pipe\` 前缀).
2. 通过环境变量 `LL_IPC_PIPE` 把名字塞给 LLBot 子进程.
3. LLBot 在自己启动早期 `net.createServer().listen('\\\\.\\pipe\\' + name)`.
4. Desktop 的 `LLBotIpcClient.RunAsync` 在 LLBot 启动后开始 `ConnectAsync` 重连循环 (1.5s 超时, 失败后 1s 重试), 直到 LLBot listen 上.
5. 连上后 Desktop 进入轮询循环, 每隔一段时间发一次 `get_login_state` 请求.

## 帧格式

JSON Lines:
- UTF-8 无 BOM
- 每条消息一行, `\n` 分隔
- 行内 JSON 不能含裸 `\n`

消息字段统一 snake_case.

## 消息

只有一个 method: `get_login_state`. 它覆盖登录全过程 (未登录返回二维码 + 状态, 已登录返回 uin/nickname).

```jsonc
// Desktop -> LLBot
{"type":"request","id":"1","method":"get_login_state"}

// LLBot -> Desktop: 需要扫码 (带二维码)
{"type":"response","id":"1","data":{"state":"need_qrcode","qrcode_png_base64":"data:image/png;base64,iVBOR..."}}

// LLBot -> Desktop: 已扫码, 等手机确认
{"type":"response","id":"1","data":{"state":"waiting_confirm"}}

// LLBot -> Desktop: 已登录
{"type":"response","id":"1","data":{"state":"logged_in","uin":"10001","nickname":"foo"}}

// LLBot -> Desktop: 出错
{"type":"response","id":"1","error":"..."}
```

字段约束:
- `type`: `request` / `response`.
- `id`: 字符串, request/response 配对. Desktop 用单调递增整数转字符串.
- `method`: 目前只有 `get_login_state`. 未识别的 method 回 `{"type":"response","id":"...","error":"unknown method"}`.
- `data.state`: 字符串, 见下表.
- `data.qrcode_png_base64`: 字符串, `state=need_qrcode` 时必有. 格式是 `data:image/png;base64,<...>` (LLBot 的 `pngBase64QrcodeData` 直接放进来即可). Desktop 会自动剥掉 `data:` 前缀.
- `data.uin` / `data.nickname`: 字符串, `state=logged_in` 时给. uin 用字符串避免大整数精度问题.

### state 取值 (LLBot 内部 → IPC)

| IPC `state` | LLBot 情形 (`QrCodeState`) | Desktop 行为 |
|-------------|---------------------------|-------------|
| `initializing` | 正在 connect / loadSession | 显示"正在初始化", 继续轮询 |
| `need_qrcode` | 有二维码待扫 (`WaitingForScan=48`) | 显示二维码, 提示扫码 |
| `waiting_confirm` | 已扫码, 等手机确认 (`WaitingForConfirm=53`) | 提示"请在手机上确认" |
| `logged_in` | `qq/online` 已触发 (含 session 快速登录) | 关闭二维码对话框, UI 显示 uin/nickname |
| `expired` | 二维码过期 (`Expired=17`) | 提示"已过期, 刷新中" — 见下方刷新约定 |
| `cancelled` | 用户取消 (`Cancelled=54`) | 提示"已取消, 刷新中" — 见下方刷新约定 |

**二维码刷新约定**: LLBot 的 `pollQrCode` 在 `Expired` / `Cancelled` 后会停止轮询、二维码失效.
为了让 Desktop 的二维码界面能自愈, **LLBot 在进入 `expired` / `cancelled` 后应自动重新 `fetchQrCode()`**, 拿到新二维码后把 `state` 切回 `need_qrcode` 并更新 `qrcode_png_base64`. Desktop 不会主动请求刷新 (协议无 refresh method), 完全靠 LLBot 自愈 + Desktop 轮询拿到新值.

**快速登录**: 若 LLBot 用已存的 session 直接登录成功, 第一次 `get_login_state` 就返回 `logged_in`. Desktop 检测到首个状态即 `logged_in` 时**不弹**二维码对话框, 直接进入运行态.

## 轮询节奏

`LLBotIpcClient` 内部:
- 连接超时: 1500 ms
- 请求超时: 3000 ms
- 登录中 (state != logged_in): 1 秒 / 次 (二维码状态要及时)
- 已登录 (state == logged_in): 5 秒 / 次
- 断开重连退避: 1 秒

`state=logged_in` 时 Desktop 把 uin/nickname 喂给 `SelfInfoStream` (兼容现有 UI 订阅); 其余状态推给 `LoginStateStream` (二维码对话框消费).

## LLBot 端接入 (Node.js 18+, Windows)

```js
// llbot-ipc.js
const net = require('node:net');

const pipeName = process.env.LL_IPC_PIPE;
if (!pipeName || process.platform !== 'win32') {
  // 不是从 Desktop 起的, 或非 Windows: 跳过 IPC
  module.exports = { setLoginState: () => {} };
  return;
}

// 当前登录状态缓存, 由业务侧 setLoginState 更新, IPC handler 直接返回它
let loginState = { state: 'initializing', qrcode_png_base64: null, uin: '', nickname: '' };

const server = net.createServer((socket) => {
  socket.setEncoding('utf8');
  let buffer = '';
  socket.on('data', (chunk) => {
    buffer += chunk;
    let idx;
    while ((idx = buffer.indexOf('\n')) >= 0) {
      const line = buffer.slice(0, idx);
      buffer = buffer.slice(idx + 1);
      if (line.trim()) handleLine(socket, line);
    }
  });
  socket.on('error', () => { /* Desktop 退出会触发, 忽略 */ });
});

function handleLine(socket, line) {
  let msg;
  try { msg = JSON.parse(line); } catch { return; }
  if (msg.type !== 'request' || !msg.id || !msg.method) return;

  let response;
  if (msg.method === 'get_login_state') {
    response = { type: 'response', id: msg.id, data: loginState };
  } else {
    response = { type: 'response', id: msg.id, error: `unknown method: ${msg.method}` };
  }
  if (!socket.destroyed) socket.write(JSON.stringify(response) + '\n');
}

server.listen(`\\\\.\\pipe\\${pipeName}`, () => {
  console.log(`[LL_IPC] listening on \\\\.\\pipe\\${pipeName}`);
});

module.exports = {
  // 业务侧在登录各阶段调用, 局部更新缓存
  setLoginState(patch) { loginState = { ...loginState, ...patch }; },
};
```

在直连登录流程 (`src/main/qqProtocol/base.ts` 的 `initDirectClient` 一带) 各阶段更新状态:

```js
const ipc = require('./llbot-ipc');

ipc.setLoginState({ state: 'initializing' });

// 走扫码: fetchQrCode() 拿到二维码后
ipc.setLoginState({ state: 'need_qrcode', qrcode_png_base64: pngBase64QrcodeData });

// pollQrCode 状态变化
//   WaitingForConfirm(53):
ipc.setLoginState({ state: 'waiting_confirm' });
//   Expired(17) / Cancelled(54): 先报状态, 然后重新 fetchQrCode -> 再切回 need_qrcode
ipc.setLoginState({ state: 'expired' });
// ...重新 fetchQrCode()...
ipc.setLoginState({ state: 'need_qrcode', qrcode_png_base64: newPngBase64 });

// 登录成功 (qq/online):
ipc.setLoginState({ state: 'logged_in', uin: String(uin), nickname: nick, qrcode_png_base64: null });
```

## 错误 / 边界

- LLBot 应尽早 `listen`; Desktop 1.5s connect 超时 + 1s 重试退避, LLBot 启动慢点也能等到.
- Desktop 启动 LLBot 后, `HomeViewModel.StartHeadlessServicesAsync` 调 `WaitFirstLoginStateAsync` 等首个非 `initializing` 状态 (最多 15s). 若 15s 内拿不到 (LLBot 没实现 `get_login_state`), Desktop 记 warning 并跳过登录界面直接进运行态 — 即旧版 LLBot 仍能启动, 只是没有二维码界面.
- Desktop 重启 / 切有头无头时管道名会变, LLBot 仍监听旧名字 — 重启 LLBot 即可.
- 非 Windows: Desktop 不启动 IPC, 不注入 `LL_IPC_PIPE`, LLBot 跳过. headless 在非 Windows 没有扫码界面, 需看 LLBot 日志/终端登录.
- 命名管道 ACL 走当前用户; 管道名带 Desktop 的 pid + guid, 同机多开不冲突.

## 改动入口

- `Services/LLBotIpcClient.cs` -- 客户端 + 轮询 `get_login_state` + `LoginStateInfo` / `LoginStateStream` / `SelfInfoStream`
- `Services/ProcessManager.cs` `StartLLBotAsync(.., ipcPipeName)` -- 注入 `LL_IPC_PIPE` 环境变量
- `Views/QRLoginDialog.axaml(.cs)` -- 精简二维码登录对话框, 订阅 `LoginStateStream`
- `ViewModels/HomeViewModel.cs` -- `StartHeadlessServicesAsync` 等首个登录状态决定是否弹二维码; 订阅 `SelfInfoStream` 喂 UI
- `Views/MainWindow.axaml.cs` -- `ShowQRLoginDialogAsync` 注入 hook (从 DI 取 `ILLBotIpcClient` new `QRLoginDialog`)
- `App.axaml.cs` -- DI 注册 `ILLBotIpcClient` 为 singleton
