# WorkBuddy Remote

> 官方已经推出自己的 WorkBuddy Remote / 网页版 WorkBuddy：<https://www.codebuddy.cn/agents>。想要快速上手、少折腾的用户，优先推荐直接使用官方 WorkBuddy Remote。
>
> 如果你更在意本地数据留存、局域网/内网部署、可控的 API 配置，或者希望自定义界面和桥接逻辑，本项目仍然适合继续使用。

把本机的桌面版 WorkBuddy 通过 CDP 桥接映射成浏览器里的网页工作台：

1. 从桌面版 WorkBuddy 的 `resources/app.asar` 读取原版 `renderer/index.html` 和静态资源。
2. 在网页里注入一个轻量 `buddyAPI` shim。
3. 通过一条浏览器 WebSocket 把 `buddyAPI` 调用转发到桌面 WorkBuddy 主窗口的 CDP 目标。

浏览器打开的是官方 WebUI 资源，桥接层只负责补齐浏览器里缺少的原生能力。

当前版本 **2.0.0**：控制台（.NET 10 + WinUI 3）直接拉起 Node 桥接进程，用 Job Object 绑定生命周期，不再经过 PowerShell / VBS。

## 目录

```text
bridge/            桥接内核（Node，唯一的运行时依赖是 ws）
  main.mjs         入口
  bridge/          CDP 连接与 buddyAPI 转发
  server/          HTTP / WebSocket 服务、认证、文件浏览、WorkDaddy 代理
  web/             浏览器端 shim
  workspace/       工作空间接口
apps/console/      控制台（.NET 10 + WinUI 3）
installer/         Inno Setup 打包脚本
scripts/           开发与 CI 用脚本（PS1 不再是启动入口）
.github/workflows/ ci.yml + release.yml
```

## 下载与安装

Release 里有三个包：

| 包 | 说明 |
|---|---|
| `WorkBuddyRemote-<版本>-setup.exe` | **推荐**。自包含 .NET 10 运行时 + Windows App SDK + Node，装完即用 |
| `WorkBuddyRemote-<版本>-slim-setup.exe` | 精简版，需要系统已装 .NET 10 桌面运行时与 Windows App Runtime |
| `WorkBuddyRemote-<版本>-portable.zip` | 便携版，解压到固定目录后再运行（配置写在程序目录，见 `portable.flag`） |

> 便携版**请先解压再运行**。直接从压缩包里双击 exe，Windows 会把整包解压到临时目录并顺手打开那个文件夹（看起来就像"启动时弹出资源管理器"），而且每次运行都会重来一遍。

安装版装到 `%LOCALAPPDATA%\Programs\WorkBuddyRemote`，**免管理员**。`checksums.txt` 里有全部产物的 SHA256。

> 本项目**不做代码签名**。首次运行可能触发 SmartScreen「Windows 已保护你的电脑」，点「更多信息 → 仍要运行」即可；不放心可以自己核对 SHA256 或从源码构建。

## 首次运行

- 自动探测 WorkBuddy：运行中的进程 → 注册表卸载项 → 常见安装目录；探到的路径会写进配置。
- 如果旧版（v1）的 `workbuddy-remote.config.json` 还在项目目录，会导入端口和开关；指不到的路径会被丢弃。
- 选「局域网 / 端口映射」时必须设访问密码，否则不允许保存。
- **关闭窗口默认收进托盘**（可在设置里关），双击托盘图标唤回；右键菜单可启动/停止桥接或退出。
- 单实例：再次双击 exe 只会把已有窗口置前，不会起第二个进程、第二个托盘图标。
- 桥接端口被占用时自动顺延（8780 → 8781 …）并写回配置，界面上显示实际端口。

## 从源码构建

需要 **.NET 10 SDK**、**Node.js 20+**；打包还需要 **Inno Setup 6**。

```powershell
git clone https://github.com/Panda35064/workbuddy-remote-winui3.git
cd workbuddy-remote-winui3

npm ci
npm run check
dotnet build apps/console/WorkBuddyRemote.WinUI.csproj -c Release -p:Platform=x64
```

开发时直接跑控制台：

```powershell
.\apps\console\bin\x64\Release\net10.0-windows10.0.19041.0\WorkBuddyRemoteWinUI.exe
```

出安装包 / 便携包（产物在 `dist\`，含 `checksums.txt`）：

```powershell
.\scripts\build-release.ps1              # 双包 + 便携 zip
.\scripts\build-release.ps1 -SkipInstaller   # 只出便携 zip
```

> 打包坑：`dotnet publish` 出来的目录可能缺 `.xbf` 和 `WorkBuddyRemoteWinUI.pri`，直接打包会启动即崩。`build-release.ps1` 会校验并从 `bin\` 补齐，缺了宁可报错也不出包。

## 配置

安装版：`%APPDATA%\WorkBuddyRemote\config.json`
便携版：程序目录 `config.json`（exe 旁边有 `portable.flag` 时）

```json
{
  "workbuddyExePath": "",
  "workbuddyUserDataDir": "",
  "cdpPort": 9333,
  "bridgePort": 8780,
  "listenHost": "127.0.0.1",
  "passwordHash": "",
  "closeToTray": true,
  "keepBridgeOnExit": false,
  "startWorkDaddy": false,
  "hideWorkBuddyWindowAfterStart": false,
  "killWorkBuddyProcessesBeforeStart": false,
  "autoRestartWorkBuddy": true,
  "enableFileManager": true,
  "enableRestart": true,
  "maskBridgeModelSecrets": false
}
```

写配置用「临时文件 + 原子替换」，未知字段保留，缺失字段回落默认值。

- `hideWorkBuddyWindowAfterStart`：桥接连上后请求隐藏桌面窗口，进程与托盘保留，浏览器页面继续可用。
- `maskBridgeModelSecrets`：在服务端拦截模型配置读取，把接口地址与 API Key 换成占位值再返回浏览器。
- `keepBridgeOnExit`：控制台退出后保留桥接进程（默认关）。下次启动会通过 `bridge-<port>.pid` 接管并停掉旧的。
- `startWorkDaddy`：默认关。开启后由桥接探测/拉起 WorkDaddy（AGPL-3.0，见文末声明）。

## 适配的 WorkBuddy 版本

当前版本适配 **WorkBuddy 5.9.2**（同一套桥接逻辑在 5.9.0 – 5.9.2 上验证过）：CDP 连接、`renderer` 资源读取、shim 注入都实测可用。

控制台「关于」页会同时显示**适配版本**和**本机检测到的版本**；如果本机版本超出验证区间，会标注「未验证」，这时建议先看 [常见问题](#常见问题) 再升级。

## WorkBuddy 5.9 的启动方式（重要）

从 5.9.0 开始，WorkBuddy 不再接受 `--remote-debugging-port` / `--user-data-dir` 这类命令行开关，改成读环境变量：

```text
WORKBUDDY_REMOTE_DEBUGGING_PORT=9333
```

主进程自己 `appendSwitch("remote-debugging-port", ...)` 并设置 `remote-allow-origins`。控制台只设置这个环境变量拉起 WorkBuddy，好处是自动更新 / 自重启后依然生效（命令行参数会丢，环境变量不会）。

代价是：**已经在运行的 WorkBuddy 无法事后打开调试端口**。点一次「重启 WorkBuddy」即可（会中断当前对话，会话记录保留）。控制台发现「在跑但没端口」会直接提示需要重启，而不是干等超时。

## 局域网访问

设置页选「局域网 / 端口映射」并设置密码，保存时会生成 `sha256:<salt>:<hash>` 写进 `passwordHash`。

也可以命令行生成：

```powershell
node .\scripts\workbuddy-password-hash.mjs "你的密码"
```

非 localhost 监听必须设置密码。之后手机访问 `http://<本机IP>:8780/agent-manager/`；要从外网用，就用端口映射把 `8780` 映射出去，桥接层不关心域名和协议。

## 网页端能力（远程也能用）

- **选择工作空间 / 目录**：`dialog:pickFolder`、`window:selectDirectory` 被拦成网页内的文件浏览器：常用位置 / 此电脑、路径栏、上一级 / 刷新、列表、双击进入、新建文件夹。接口在 `/bridge/fs/{roots,list,mkdir}`，受 `enableFileManager` 控制。
- **设置**：远程时不再开服务器上的新窗口，改成页内浮层 iframe（`?windowAppId=settings`）。
- **WorkDaddy 全功能面板**：装了 [WorkDaddy](https://github.com/babygoton/WorkDaddy) 后网页右下角出现它的按钮，面板直接复用它的 `inject.js` 拼装结果，API 走同源代理 `/workdaddy/api/*`，令牌由服务端补上，不暴露给浏览器。切换账号后网页端会自动刷新。
- **查看所有产物**：`getSessionArtifacts` 等接口超时放宽到 120s，避免产物多的会话加载失败。

## 常见问题

**标签页图标不显示？**
`/favicon.ico` 返回 WorkBuddy 自带的 `resources/icon.ico`，登录页也会注入图标声明，且这几个路径不要求登录。仍显示旧图标是浏览器缓存：Ctrl+F5 或无痕窗口。

**提示「WorkBuddy 正在运行，但没有开启调试端口」？**
见上面 5.9 的启动方式，点一次「重启 WorkBuddy」。

**控制台启动后界面没起来 / 一闪而过？**
看 `%APPDATA%\WorkBuddyRemote\config.json` 与 `output\runtime\temp\bridge-<port>.log`。安装版日志目录在安装目录下的 `output\runtime\temp`。

## 进程和连接

刷新网页只重建浏览器到桥接服务的 WebSocket，不会新建 WorkBuddy 进程。桥接进程随控制台退出而结束（`keepBridgeOnExit` 关时），Job Object 保证不会留下孤儿进程。桌面 WorkBuddy 仍负责原生会话、任务、内存和账号状态。

## 检查与冒烟

```powershell
npm run check
.\scripts\smoke.ps1          # 起桥接 → /healthz → /workdaddy/status → 停
```

CI：push / PR 跑语法检查、桥接冒烟与 `dotnet build`；打 `v*` tag 自动出三个包并附 `checksums.txt`。

## 第三方声明

- WorkDaddy 采用 **AGPL-3.0**。本项目不打包它的任何文件、默认关闭、由用户主动开启；开启后桥接会在运行时读取本机已安装的 WorkDaddy 资源并经由本项目 HTTP 服务对外提供，按 AGPL §13 网络条款，源码请见 <https://github.com/babygoton/WorkDaddy>。
- WorkBuddy 的界面资源来自本机安装的官方客户端，本仓库不包含任何 WorkBuddy 二进制。

## License

MIT，见 [LICENSE](LICENSE)。
