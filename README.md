<div align="center">

<img src="docs/logo.png" width="128" alt="WorkBuddy Remote">

# WorkBuddy Remote

**把桌面版 WorkBuddy 变成浏览器里的工作台**

本地数据留存 ｜ 局域网 / 内网可用 ｜ 完整对话 UI ｜ 开箱即用

<a href="https://github.com/Panda35064/workbuddy-remote-winui3/releases/latest"><img src="https://img.shields.io/github/v/release/Panda35064/workbuddy-remote-winui3?color=76bad9&label=%E6%9C%80%E6%96%B0%E7%89%88%E6%9C%AC"></a>
<a href="https://dotnet.microsoft.com/download/dotnet/10.0"><img src="https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white"></a>
<a href="https://nodejs.org/"><img src="https://img.shields.io/badge/Node.js-20+-339933?logo=node.js&logoColor=white"></a>
<img src="https://img.shields.io/badge/Windows-10%20%2F%2011-0078D4?logo=windows11&logoColor=white">
<a href="LICENSE"><img src="https://img.shields.io/github/license/Panda35064/workbuddy-remote-winui3?color=76bad9"></a>

<a href="https://github.com/Panda35064/workbuddy-remote-winui3/releases">下载</a> ｜
<a href="https://github.com/Panda35064/workbuddy-remote-winui3/issues">问题提交</a> ｜
<a href="https://github.com/Panda35064/workbuddy-remote-winui3/blob/main/README.md">文档</a>

</div>

> [!NOTE]
> 官方已经推出自己的 WorkBuddy Remote / 网页版 WorkBuddy：<https://www.codebuddy.cn/agents>。想要快速上手、少折腾的用户，优先推荐直接使用官方 WorkBuddy Remote。
>
> 如果你更在意**本地数据留存**、**局域网 / 内网部署**、**可控的 API 配置**，或者希望**自定义界面和桥接逻辑**，本项目仍然适合继续使用。

**WorkBuddy Remote** 把本机的桌面版 WorkBuddy 桥接成浏览器里的网页工作台：

1. 从桌面版 WorkBuddy 的 `resources/app.asar` 读取原版 `renderer/index.html` 和静态资源；
2. 在网页里注入一个轻量 `buddyAPI` shim，自动补齐浏览器里缺失的 **600+ 个原生方法**；
3. 通过一条浏览器 WebSocket 把 `buddyAPI` 调用转发到桌面 WorkBuddy 主窗口的 CDP 目标。

浏览器打开的是官方 WebUI 资源，桥接层只负责补齐浏览器里缺少的原生能力。

## ✨ 主要功能

1. 🖥️ **WinUI 3 控制台**：概览 / 运行日志 / 设置三页式，托盘常驻、开机自启、单实例、崩溃自动重启。
2. 🌉 **一键桥接**：自动探测 WorkBuddy 安装位置，Job Object 绑定生命周期，不留孤儿进程。
3. 🌐 **完整对话 UI**：新建任务、发送消息、自动跳转会话，远程与桌面体验一致。
4. 🔒 **访问控制**：仅本机 / 局域网两种模式，`sha256:<salt>:<hash>` 访问密码，随时可清除。
5. 📂 **文件管理器**：远程选择工作空间 / 目录（常用位置、此电脑、新建文件夹）。
6. 🧩 **WorkDaddy 集成**：右下角全功能面板，API 走同源代理，令牌不出服务端。
7. 🩺 **可观测性**：桥接事件日志落盘、浏览器 `console.error` 自动上报，远程问题可直接定位。
8. 🛡️ **隐私开关**：可拦截模型配置读取，把接口地址与 API Key 换成占位值再返回浏览器。

## 📦 下载与安装

Release 里有三个包：

| 包 | 说明 |
|---|---|
| `WorkBuddyRemote-<版本>-setup.exe` | **推荐**。自包含 .NET 10 运行时 + Windows App SDK + Node，装完即用 |
| `WorkBuddyRemote-<版本>-slim-setup.exe` | 精简版，需要系统已装 .NET 10 桌面运行时与 Windows App Runtime |
| `WorkBuddyRemote-<版本>-portable.zip` | 便携版，解压到固定目录后再运行（配置写在程序目录，见 `portable.flag`） |

> [!WARNING]
> 便携版**请先解压再运行**。直接从压缩包里双击 exe，Windows 会把整包解压到临时目录并顺手打开那个文件夹，而且每次运行都会重来一遍。

安装版装到 `%LOCALAPPDATA%\Programs\WorkBuddyRemote`，**免管理员**。`checksums.txt` 里有全部产物的 SHA256。

> [!IMPORTANT]
> 本项目**不做代码签名**。首次运行可能触发 SmartScreen「Windows 已保护你的电脑」，点「更多信息 → 仍要运行」即可；不放心可以自己核对 SHA256 或从源码构建。

## 🚀 快速开始

1. **安装并打开控制台**，它会自动探测 WorkBuddy（运行中的进程 → 注册表卸载项 → 常见安装目录）。
2. 点「**启动**」，桥接就绪后浏览器地址显示在概览页，默认 `http://127.0.0.1:8780/agent-manager/`。
3. 手机 / 其他设备访问时，在设置里切「局域网 / 端口映射」并设置访问密码。

其他行为：

- **关闭窗口默认收进托盘**（可在设置里关），双击托盘图标唤回；右键菜单可启动 / 停止桥接或退出。
- **单实例**：再次双击 exe 只会把已有窗口置前。
- **端口占用自动顺延**（8780 → 8781 …）并写回配置，界面上显示实际端口。
- 首次从 v1 升级时，旧 `workbuddy-remote.config.json` 里的端口和开关会被导入；指不到的路径会被丢弃。

## ⚙️ 配置

安装版：`%APPDATA%\WorkBuddyRemote\config.json` ｜ 便携版：程序目录 `config.json`（exe 旁有 `portable.flag` 时）

| 配置项 | 默认 | 说明 |
|---|---|---|
| `bridgePort` | `8780` | 桥接 HTTP 端口 |
| `cdpPort` | `9333` | WorkBuddy 远程调试端口 |
| `listenHost` | `127.0.0.1` | 仅本机 / `0.0.0.0` 局域网 |
| `passwordHash` | 空 | 访问密码（局域网模式必填，仅本机模式不启用） |
| `closeToTray` | `true` | 关闭窗口收进托盘 |
| `hideWorkBuddyWindowAfterStart` | `false` | 桥接连上后收起桌面窗口 |
| `killWorkBuddyProcessesBeforeStart` | `false` | 启动前结束后台 WorkBuddy 进程 |
| `autoRestartWorkBuddy` | `true` | 自动重启缺调试端口的 WorkBuddy |
| `startWorkDaddy` | `false` | 拉起 WorkDaddy（AGPL-3.0，见文末声明） |
| `keepBridgeOnExit` | `false` | 控制台退出后保留桥接进程 |
| `enableFileManager` | `true` | 网页端文件浏览器 |
| `enableRestart` | `true` | 网页端「重启 WorkBuddy」按钮 |
| `maskBridgeModelSecrets` | `false` | 服务端拦截模型密钥，返回占位值 |

写配置用「临时文件 + 原子替换」，未知字段保留，缺失字段回落默认值。

## 🌐 网页端能力

- **完整对话体验**：新建任务、发送消息、自动跳转会话、会话列表与状态实时同步。
- **选择工作空间 / 目录**：`dialog:pickFolder`、`window:selectDirectory` 被拦成网页内的文件浏览器，接口在 `/bridge/fs/{roots,list,mkdir}`。
- **设置**：远程时不再开服务器上的新窗口，改成页内浮层 iframe。
- **WorkDaddy 全功能面板**：面板复用它的 `inject.js` 拼装结果，API 走同源代理 `/workdaddy/api/*`，令牌由服务端补上、不暴露给浏览器；切换账号后自动刷新。
- **查看所有产物**：`getSessionArtifacts` 等接口超时放宽到 120s，避免产物多的会话加载失败。

## 🔒 局域网访问

设置页选「局域网 / 端口映射」并设置密码，保存时生成 `sha256:<salt>:<hash>` 写进 `passwordHash`；切回「仅本机」时密码**立即失效**（不传给桥接），再切回来自动恢复。

也可以命令行生成：

```powershell
node .\scripts\workbuddy-password-hash.mjs "你的密码"
```

之后手机访问 `http://<本机IP>:8780/agent-manager/`；要从外网用，用端口映射把 `8780` 映射出去即可，桥接层不关心域名和协议。

## 🧱 架构

```text
bridge/            桥接内核（Node，唯一的运行时依赖是 ws）
  main.mjs         入口
  bridge/          CDP 连接、buddyAPI 转发、事件流
  server/          HTTP / WebSocket 服务、认证、文件浏览、WorkDaddy 代理
  web/             浏览器端 shim（preload 方法映射、路由兜底、错误上报）
  workspace/       工作空间接口
apps/console/      控制台（.NET 10 + WinUI 3）
installer/         Inno Setup 打包脚本
scripts/           开发与 CI 用脚本（PS1 不再是启动入口）
.github/workflows/ ci.yml + release.yml
```

## 🔖 适配的 WorkBuddy 版本

当前版本适配 **WorkBuddy 5.9.2**（同一套桥接逻辑在 5.9.0 – 5.9.2 上验证过）：CDP 连接、`renderer` 资源读取、shim 注入都实测可用。

控制台「关于」页同时显示**适配版本**和**本机检测到的版本**；本机版本超出验证区间会标注「未验证」。

### WorkBuddy 5.9 的启动方式（重要）

从 5.9.0 开始，WorkBuddy 不再接受 `--remote-debugging-port` / `--user-data-dir` 这类命令行开关，改成读环境变量：

```text
WORKBUDDY_REMOTE_DEBUGGING_PORT=9333
```

主进程自己 `appendSwitch("remote-debugging-port", ...)` 并设置 `remote-allow-origins`。控制台只设置这个环境变量拉起 WorkBuddy，好处是自动更新 / 自重启后依然生效（命令行参数会丢，环境变量不会）。

代价是：**已经在运行的 WorkBuddy 无法事后打开调试端口**。点一次「重启 WorkBuddy」即可（会中断当前对话，会话记录保留）。控制台发现「在跑但没端口」会直接提示需要重启，而不是干等超时。

## ❓ 常见问题

**标签页图标不显示？**
`/favicon.ico` 返回 WorkBuddy 自带的 `resources/icon.ico`，登录页也会注入图标声明，且这几个路径不要求登录。仍显示旧图标是浏览器缓存：Ctrl+F5 或无痕窗口。

**提示「WorkBuddy 正在运行，但没有开启调试端口」？**
见上面 5.9 的启动方式，点一次「重启 WorkBuddy」。

**控制台启动后界面没起来 / 一闪而过？**
看 `%APPDATA%\WorkBuddyRemote\config.json` 与 `output\runtime\temp\bridge-<port>.log`。安装版日志目录在安装目录下的 `output\runtime\temp`。

**远程访问想排查问题？**
`output\runtime\temp\bridge-<port>.events.log` 记录了每条通道调用与失败原因；网页端的 `console.error` 与未捕获异常也会自动上报到这份日志里。

## 🛠️ 从源码构建

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

## ✅ 检查与冒烟

```powershell
npm run check
.\scripts\smoke.ps1          # 起桥接 → /healthz → /workdaddy/status → 停
```

CI：push / PR 跑语法检查、桥接冒烟与 `dotnet build`；打 `v*` tag 自动出三个包并附 `checksums.txt`。

## 🔌 进程和连接

刷新网页只重建浏览器到桥接服务的 WebSocket，不会新建 WorkBuddy 进程。桥接进程随控制台退出而结束（`keepBridgeOnExit` 关时），Job Object 保证不会留下孤儿进程。桌面 WorkBuddy 仍负责原生会话、任务、内存和账号状态。

## 🙏 致谢

- **[vergess3/workbuddy-remote](https://github.com/vergess3/workbuddy-remote)** —— 本项目的上游项目，作者 **vergess3**。WebUI 复用方案、`buddyAPI` shim 与 CDP 桥接的核心实现来自这里；本项目在其基础上重构为 WinUI 3 控制台形态并持续修复远程体验问题。
- **[WorkBuddy](https://www.codebuddy.cn/)** —— 本项目不包含它的任何二进制，界面资源来自本机安装的官方客户端，桥接层只补齐浏览器里缺失的原生能力。
- **[WorkDaddy](https://github.com/babygoton/WorkDaddy)** —— 可选的账号切换插件，采用 AGPL-3.0，默认关闭、由用户主动开启。
- 以及所有提交 Issue、反馈问题的使用者。

> 本项目为**非官方**第三方工具，与 WorkBuddy 官方团队无关。

## 📄 第三方声明

- WorkDaddy 采用 **AGPL-3.0**。本项目不打包它的任何文件、默认关闭、由用户主动开启；开启后桥接会在运行时读取本机已安装的 WorkDaddy 资源并经由本项目 HTTP 服务对外提供，按 AGPL §13 网络条款，源码请见 <https://github.com/babygoton/WorkDaddy>。
- WorkBuddy 的界面资源来自本机安装的官方客户端，本仓库不包含任何 WorkBuddy 二进制。

## 📄 License

MIT，见 [LICENSE](LICENSE)。

<div align="center">

如果这个项目对你有帮助，欢迎点一个 Star ⭐

</div>
