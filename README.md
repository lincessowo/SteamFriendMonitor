# SteamFriendMonitor — Steam 好友状态监控

图形界面 + 纯 CLI 双版本、跨平台（Windows / Linux / macOS）的 Steam 好友状态监控工具（基于 [SteamKit2](https://github.com/SteamRE/SteamKit)）：

- **隐身模式登录**：以 `Invisible` 状态登录 Steam，好友看到你是离线，但能正常接收他们的动态
- **保持连接**：通过 CM 服务器长连接（`cmp3-hkg1.steamserver.net` 这类 TCP 端点，失败自动回退 WebSocket），SteamKit 自动心跳保活，断线自动重连；登录令牌到期前**自动续期**
- **好友动态监听**：登录后做一次全量同步，之后好友的上线 / 下线 / 离开 / 忙碌 / 开始游戏 / 停止游戏 / 好友增删全部由 Steam 在 CM 连接上**实时推送**，无任何轮询
- **跨启动去重**：上次运行时已是离线的好友，本次启动状态没变化就不重复写日志
- **双份存储**：每条动态同时写入按天 txt 日志（`logs/20261003.txt`）和 SQLite 数据库（`friends.db`）
- **统计**：按 今天 / 本周 / 本月 / 本年、可按好友筛选——游戏排行、在线时长、游戏会话记录（游戏中发生了什么）
- **WebSocket 接口**（默认关闭）：实时推送给其他程序使用
- **代理支持**：HTTP / SOCKS5
- **托盘常驻 + 开机自启动**（GUI 版）

## 版本

| 版本 | 说明 | 位置 |
| --- | --- | --- |
| GUI | Windows 图形界面（托盘、统计、设置），自包含压缩单文件 ~52 MB | `publish\SteamFriendMonitor.exe` |
| CLI | 纯命令行，自包含压缩单文件 ~38-40 MB | `publish\cli\win-x64\` `linux-x64\` `linux-arm64\` `osx-x64\` `osx-arm64\` |

> 全部产物**自带 .NET 运行时**（已压缩），目标机器无需安装任何依赖，解压即用。GUI 启动时无控制台窗口。

CLI 用法：直接运行对应平台的 `SteamFriendMonitor.Cli`；首次运行生成 `config.json` 模板，或按提示输入账号密码（密码隐藏输入）。SteamGuard 验证码在控制台输入；配置了 `SharedSecret` 则全自动。

## 设置项（config.json，与界面设置一一对应，保存在 exe 旁）

```jsonc
{
  "Username": "你的Steam用户名",           // 必填
  "Password": "你的Steam密码",             // 首次登录必填，之后登录令牌会保存在本地
  "SharedSecret": "",                      // 可选：手机令牌 shared_secret（base64），填了自动过 SteamGuard
  "LogDirectory": "logs",                  // txt 日志目录（相对 config.json）
  "DatabasePath": "friends.db",            // SQLite 数据库文件
  "Protocol": "All",                       // All(默认，TCP CM 优先) / Tcp / WebSocket
  "LoginID": null,                         // Steam 会话 ID，留空自动生成
  "LogInitialSnapshot": true,              // 启动时是否记录好友当前状态快照（未变化的不再重复）
  "NotifyOnFriendOnline": true,            // 好友上线 / 进游戏时托盘气泡提醒（GUI）
  "ProxyType": "None",                     // None / Http / Socks5
  "ProxyHost": "",                         // 代理地址
  "ProxyPort": 0,                          // 代理端口
  "ProxyUsername": "",                     // 代理认证（一般留空）
  "ProxyPassword": "",
  "WsEnabled": false,                      // WebSocket 接口开关（默认关闭）
  "WsPort": 8765,                          // WS 端口
  "WsHost": "127.0.0.1",                   // WS 绑定地址（改非 127.0.0.1 需倒计时确认）
  "WsToken": ""                            // 非本机绑定时客户端必须携带的令牌（自动生成）
}
```

## WebSocket 接口

默认关闭。开启后监听 `ws://127.0.0.1:8765/`，消息为 JSON：

| 消息 | 说明 |
| --- | --- |
| `{"type":"snapshot","data":{...}}` | 连接建立时推送：自己 SteamID + 全部好友当前状态 |
| `{"type":"event","data":{...}}` | 实时动态：steamId / name / status / personaState / game / richPresence / eventType / time |
| `{"type":"status","data":{...}}` | 连接状态变化（登录成功 / 断开重连等） |
| `{"type":"heartbeat","data":{...}}` | 每 10 秒一次心跳（服务器时间、在线客户端数） |

客户端可发送文本消息 `get_snapshot` 主动拉取快照，`ping` 立即获取一次心跳。

**安全**：默认只监听 `127.0.0.1`（仅本机可连）。如执意绑定其他地址（如 `0.0.0.0`），GUI 会弹出**10 秒倒计时确认**的安全警告，CLI 会在控制台倒计时确认，且开启后自动生成访问令牌——客户端必须以 `ws://host:port/?token=xxx` 连接，否则返回 401。注意 Windows 上绑定非 127.0.0.1 地址可能需要管理员权限或 `netsh http urlacl`。

## 统计

统计窗口按 今天 / 本周 / 本月 / 本年、可再按好友筛选：

- **游戏排行**：好友都在玩什么（游戏、在玩好友数、累计时长）
- **好友在线时长**：在线总时长（所有非离线状态）及其中游戏中时长
- **游戏记录**：每段游戏会话（好友、游戏、起止、时长、最后的富状态），点选会话查看期间富状态变化明细（比分 / 地图等）

## 代理说明

- 代理通过 SteamKit2 的 `HttpClientFactory` 注入：**Steam 认证请求**和 **CM 连接**都走代理
- CM 连接走代理时自动使用 **WebSocket 协议**（HTTP 代理 = CONNECT 隧道；SOCKS5 由 .NET 原生支持），程序会自动切换并在日志中提示
- 典型场景：本机 Clash / v2ray 混合端口 → 选 HTTP，地址 `127.0.0.1`，端口 `7890`

## SQLite 数据

表 `friend_events`：

| 字段 | 说明 |
| --- | --- |
| id | 自增主键 |
| time | 本地时间 `yyyy-MM-dd HH:mm:ss` |
| epoch | Unix 时间戳（秒） |
| steam_id | 好友 SteamID64 |
| friend_name | 当时的好友昵称 |
| status | 中文状态（在线 / 游戏中（XXX）/ 离线 / 移除好友 …） |
| persona_state | EPersonaState 原始值（0 离线 1 在线 2 忙碌 3 离开 4 打盹 5 寻找交易 6 寻找组队 7 隐身） |
| game_name | 游戏名（未在游戏为 NULL），统计按此列分组 |
| rich_presence | 游戏富状态（如 CS2 的 `map=de_dust2 \| score=6 : 2`），与游戏名分开，不参与游戏统计 |
| event_type | `status` 状态变化 / `snapshot` 启动快照（跨启动去重后只记变化）/ `rich_presence` 游戏内富状态变化 / `removed` 好友被移除 |

## 日志格式

文件名：`日志目录/年月日.txt`（如 `logs/20261003.txt`），每行：

```
（14:34:56） 好友名称 76561198000000000 在线
（14:35:12） 好友名称 76561198000000000 游戏中（Dota 2）
（14:40:01） 好友名称 76561198000000000 离线
```

即 `（时间 HH:mm:ss） 好友名称 steamid 好友状态`，只在实际变化时记录：上次运行就是离线、本次启动还是离线的好友不会重复记录。

## 说明与注意

- **登录凭据**：首次密码验证成功后，refresh token + SteamGuard 设备数据保存在 exe 旁的 `session.*.token` 文件，之后启动直接用令牌登录；程序会在令牌到期前**自动续期**（Steam 会轮换令牌），长期运行无需重新验证
- **出现 AccessDenied 怎么办**：说明本地令牌已被 Steam 判定失效（改过密码 / 手机上撤销过授权 / 旧版本创建的令牌）。程序会自动清除并退回密码验证流程；若设置里没存密码，程序会明确停止并提示填写
- **与官方客户端共存**：程序使用独立的会话 ID，不会把你正在玩的 Steam 客户端顶下线
- **频率限制**：登录失败次数过多会触发 Steam 的 `RateLimitExceeded`，程序会等待 10 分钟再重试；请勿频繁重启
- **shared_secret 获取**：从手机令牌导出文件（`.maFile`，SteamDesktopAuthenticator / WinAuth / wt_CLI 等工具导出）里取 `"shared_secret": "xxxx=="` 字段的 base64 字符串
- 本工具与 Steam 好友数据交互遵循客户端协议，仅做只读监听，不会向好友发送任何消息

## 构建与发布

一键发布（GUI + 全平台 CLI，均为自包含压缩单文件）：

```powershell
powershell -ExecutionPolicy Bypass -File publish-all.ps1
```

手动命令：

```powershell
# GUI（Windows，自带运行时）
dotnet publish SteamFriendMonitor.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish

# CLI（自带运行时，发布时带 RID 以包含对应平台的 SQLite 原生库）
dotnet publish SteamFriendMonitor.Cli/SteamFriendMonitor.Cli.csproj -c Release -r linux-x64  --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish/cli/linux-x64
dotnet publish SteamFriendMonitor.Cli/SteamFriendMonitor.Cli.csproj -c Release -r linux-arm64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish/cli/linux-arm64
dotnet publish SteamFriendMonitor.Cli/SteamFriendMonitor.Cli.csproj -c Release -r osx-x64    --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish/cli/osx-x64
dotnet publish SteamFriendMonitor.Cli/SteamFriendMonitor.Cli.csproj -c Release -r osx-arm64   --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish/cli/osx-arm64
```

> 想要小体积的框架依赖多文件版（每平台 ~10 MB，需目标机器装有 .NET 10 运行时）：把 `--self-contained true` 换成 `false` 并去掉两个 `-p:` 参数即可。

Linux/macOS 上运行 CLI：`chmod +x SteamFriendMonitor.Cli && ./SteamFriendMonitor.Cli`（首次可能需在系统中放行下载的二进制）。

开发调试：`dotnet run`（GUI）/ `dotnet run --project SteamFriendMonitor.Cli`。
