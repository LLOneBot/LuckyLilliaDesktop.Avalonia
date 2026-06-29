# LLBot IPC 协议 (Desktop &lt;-&gt; LLBot)

无头模式下 Desktop 不启动 PMHQ, 取不到 selfInfo. 走一条 Windows 命名管道从 LLBot 拿 UIN / 昵称.

**LLBot 是 server, Desktop 是 client.** Desktop 周期性轮询 LLBot.
**仅 Windows**, 由 `Services/LLBotIpcClient.cs` 实现.

## 握手

1. Desktop 启动 LLBot 之前生成管道名 `luckylillia-llbot-{desktop_pid}-{guid}` (不含 `\\.\pipe\` 前缀).
2. 通过环境变量 `LL_IPC_PIPE` 把名字塞给 LLBot 子进程.
3. LLBot 在自己启动早期 `net.createServer().listen('\\\\.\\pipe\\' + name)`.
4. Desktop 这边的 `LLBotIpcClient.RunAsync` 在 LLBot 启动后开始 `ConnectAsync` 重连循环 (1.5s 超时, 失败后 1s 重试), 直到 LLBot listen 上.
5. 连上后 Desktop 进入轮询循环, 每隔一段时间发一次 `get_self_info` 请求.

LLBot 应当在收到自己关心的所有 message 后再 `listen`, 否则 Desktop 拿到的可能是 listen 时 LLBot 还没初始化好的状态. Desktop 端容忍空字段, 会继续轮询.

## 帧格式

JSON Lines:
- UTF-8 无 BOM
- 每条消息一行, `\n` 分隔
- 行内 JSON 不能含裸 `\n`

消息字段统一 snake_case.

## 消息

```jsonc
// Desktop -> LLBot: 请求一次 self_info
{"type":"request","id":"1","method":"get_self_info"}

// LLBot -> Desktop: 回应 (成功)
{"type":"response","id":"1","data":{"uin":"10001","nickname":"foo"}}

// LLBot -> Desktop: 回应 (失败 / 还没登录)
{"type":"response","id":"1","error":"not logged in"}
```

字段约束:
- `type`: 当前只有 `request` / `response` 两种.
- `id`: 字符串, request/response 配对. Desktop 端用单调递增整数转字符串.
- `method`: 目前只有 `get_self_info`. 未识别的 method 应当回 `{"type":"response","id":"...","error":"unknown method"}`.
- `data.uin`: 字符串 (整数 QQ 号也以字符串发, 避免大整数精度问题). 没登录时可省略或返回空字符串.
- `data.nickname`: 字符串.

Desktop 端容忍空字符串字段, 但 **请勿** 返回非 `response` 类型 / 不匹配 id 的消息夹在中间 — 当前实现会容忍未匹配的行继续读, 但这会让本次请求超时变长.

## 轮询节奏

`LLBotIpcClient` 内部:
- 连接超时: 1500 ms
- 请求超时: 3000 ms
- 没有完整 uin+nickname 时: 1 秒 / 次
- 已经拿到 uin 和 nickname: 5 秒 / 次
- 断开重连退避: 1 秒

LLBot 切号 / 换昵称会自然反映 — Desktop 每 5 秒还会再问一次.

## LLBot 端接入 (Node.js 18+, Windows)

```js
// llbot-ipc.js
const net = require('node:net');

const pipeName = process.env.LL_IPC_PIPE;
if (!pipeName || process.platform !== 'win32') {
  // 不是从 Desktop 起的, 或非 Windows: 直接跳过 IPC
  module.exports = { setSelfInfoProvider: () => {} };
  return;
}

let provider = () => ({ uin: '', nickname: '' });

const server = net.createServer((socket) => {
  socket.setEncoding('utf8');
  let buffer = '';

  socket.on('data', (chunk) => {
    buffer += chunk;
    let idx;
    while ((idx = buffer.indexOf('\n')) >= 0) {
      const line = buffer.slice(0, idx);
      buffer = buffer.slice(idx + 1);
      if (!line.trim()) continue;
      handleLine(socket, line);
    }
  });

  socket.on('error', () => { /* Desktop 退出会触发, 忽略 */ });
});

async function handleLine(socket, line) {
  let msg;
  try { msg = JSON.parse(line); } catch { return; }
  if (msg.type !== 'request' || !msg.id || !msg.method) return;

  let response;
  if (msg.method === 'get_self_info') {
    try {
      const info = await provider();
      response = { type: 'response', id: msg.id, data: { uin: String(info?.uin ?? ''), nickname: String(info?.nickname ?? '') } };
    } catch (e) {
      response = { type: 'response', id: msg.id, error: String(e?.message ?? e) };
    }
  } else {
    response = { type: 'response', id: msg.id, error: `unknown method: ${msg.method}` };
  }
  if (!socket.destroyed) {
    socket.write(JSON.stringify(response) + '\n');
  }
}

server.listen(`\\\\.\\pipe\\${pipeName}`, () => {
  console.log(`[LL_IPC] listening on \\\\.\\pipe\\${pipeName}`);
});

module.exports = {
  // LLBot 内部用: 注册一个返回当前 self info 的回调 (可以是 async)
  setSelfInfoProvider(fn) { provider = fn; },
};
```

业务入口接上:

```js
const ipc = require('./llbot-ipc');

ipc.setSelfInfoProvider(async () => ({
  uin: String(bot.self?.uin ?? ''),
  nickname: bot.self?.nickname ?? '',
}));
```

## 错误 / 边界

- LLBot 必须**先 listen 再启动 Desktop 后续逻辑** ... 反过来说, Desktop 这边 1.5s connect 超时 + 1s 重试退避, LLBot 启动慢一点也能等到 (实测 Node 冷启 + listen 也就几百毫秒).
- Desktop 重启 / 切换有头无头模式时管道名会变, LLBot 的 server 仍在监听旧名字, Desktop 会连不上. 这种场景目前不处理 -- 重启 LLBot 即可.
- 非 Windows 平台: Desktop 不启动 IPC 客户端, 不注入 `LL_IPC_PIPE`, LLBot 端 `pipeName` 取不到, 自动跳过.
- 命名管道 ACL 默认走当前用户, 同用户的其他进程也能看到, 但管道名带了 Desktop 自己的 pid + guid, 不会被外部猜中. 同机多开 Desktop 不冲突.

## 改动入口

- `Services/LLBotIpcClient.cs` -- 客户端 + 轮询 + JSON 协议
- `Services/ProcessManager.cs` `StartLLBotAsync(.., ipcPipeName)` -- 注入 `LL_IPC_PIPE` 环境变量
- `ViewModels/HomeViewModel.cs` -- 订阅 `SelfInfoStream` 喂 UI; `StartHeadlessServicesAsync` 拿 pipe 名传给 ProcessManager; `StopAllServicesAsync` 停 IPC
- `App.axaml.cs` -- DI 注册 `ILLBotIpcClient` 为 singleton
