# WorkBuddy Remote New

> 官方已经推出自己的 WorkBuddy Remote / 网页版 WorkBuddy：<https://www.codebuddy.cn/agents>。想要快速上手、少折腾的用户，优先推荐直接使用官方 WorkBuddy Remote。
>
> 如果你更在意本地数据留存、局域网/内网部署、可控的 API 配置，或者希望自定义界面和桥接逻辑，本项目仍然适合继续使用。

这是重构后的 WorkBuddy 远程桥接项目。由于官方前端已经完成重构，本项目也从原来较重的 relay/native 镜像方案，改成直接复用官方 WebUI 的形式：

1. 从桌面版 WorkBuddy 的 `resources/app.asar` 读取原版 `renderer/index.html` 和静态资源。
2. 在网页里注入一个轻量 `buddyAPI` shim。
3. 通过一条浏览器 WebSocket 把 `buddyAPI` 调用转发到桌面 WorkBuddy 主窗口的 CDP 目标。

和旧版相比，新的实现不再维护独立 light/native 前端、VS Code IPC 镜像层、动态 MessagePort relay 和大量窗口清理逻辑。浏览器打开的是官方 WebUI 资源，桥接层只负责补齐浏览器里缺少的原生能力，因此项目体积更小，启动路径更短，页面打开速度也明显更快。

## 目录

- `src/asar.mjs`: 最小 ASAR 读取器。
- `src/bridge/runtime.mjs`: CDP 连接和 `buddyAPI` 转发。
- `src/server/bridge-server.mjs`: HTTP 静态资源服务和 `/bridge/ws`。
- `src/web/workbuddy-native.mjs`: 浏览器端 `buddyAPI` shim。
- `tools/start-workbuddy-remote.ps1`: Windows 启动脚本。
- `tools/workbuddy-password-hash.mjs`: 生成访问密码哈希。

## 安装

需要 Node.js 18+，并且本机已经安装桌面版 WorkBuddy。

```powershell
cd C:\Users\Public\workbuddy-remote-new
npm install
```

## 本机启动

双击根目录的：

```text
WorkBuddyRemote.exe
```

这是推荐方式。它是一个原生控制台窗口，可以直接看状态、启停服务和改配置：

- 顶部状态徽标 + 卡片：桥接服务、桌面端（CDP）、监听模式是否就绪，2.5 秒自动刷新。
- 访问地址一行可点击复制；开启局域网监听后会同时显示手机可用的局域网地址。
- 按钮：`启动` / `重启桥接` / `停止` / `打开页面` / `日志`，第二行是 `设置` / `重启 WorkBuddy`。
- `设置` 是图形化配置窗口：端口、仅本机 / 局域网、访问密码、WorkBuddy 路径与各功能开关，保存后写回 `workbuddy-remote.config.json`。
- `重启 WorkBuddy` 会关闭并重新拉起桌面端，让新版 WorkBuddy 带上调试端口（会中断当前对话，需确认）。
- `日志` 打开 `output\runtime\temp`，里面有 bridge 事件日志和控制台自己的 `launcher.log`。
- 底部 `开机自动启动` 会在启动目录创建快捷方式，开机时静默拉起桥接服务（不弹浏览器）。
- 关闭窗口只是收进托盘，桥接服务继续运行；要真正退出控制台，右键托盘图标选择「退出控制台」。

它内部调用的仍然是 `tools/start-workbuddy-remote.ps1`，所以行为与下面两种方式完全一致。

旧方式（保留兼容）：

```text
start-workbuddy-remote.vbs
```

它会隐藏 PowerShell 窗口，自动补装 Node 依赖，启动 WorkBuddy 和轻量 bridge，并在就绪后打开浏览器。

也可以手动用 PowerShell 启动：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\start-workbuddy-remote.ps1 -ListenHost 127.0.0.1
```

停止桥接服务（不影响桌面端 WorkBuddy）：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\stop-workbuddy-remote.ps1
```

启动后访问：

```text
http://127.0.0.1:8780/agent-manager/
```

如果 `9333` CDP 端口已经有 WorkBuddy 在监听，启动脚本会复用现有 WorkBuddy；否则才会启动新的 WorkBuddy。

## WorkBuddy 5.9 的启动方式（重要）

从 5.9.0 开始，WorkBuddy 不再接受 `--remote-debugging-port` / `--user-data-dir` 这类命令行开关，也不会把它们传给 Chromium。它改成读取环境变量：

```text
WORKBUDDY_REMOTE_DEBUGGING_PORT=9333
```

主进程会自己 `appendSwitch("remote-debugging-port", ...)` 并同时设置 `remote-allow-origins`。本项目已经改为只设置这个环境变量，好处是自动更新 / 自重启后依然生效（命令行参数会丢，环境变量不会）。

代价是：**已经在运行的 WorkBuddy 无法事后打开调试端口**。升级到 5.9 后需要重启一次桌面端，控制台里点 `重启 WorkBuddy` 即可（会中断当前对话，会话记录保留）。之后无论是控制台启动还是你自己打开 WorkBuddy，只要是从控制台/开机自启拉起的，都会带上调试端口。

如果启动脚本发现 WorkBuddy 正在运行但端口没开，会立刻以退出码 42 返回，控制台会弹窗询问是否重启，而不是干等 30 秒超时。

## 局域网访问

推荐直接用控制台的 `设置` → `局域网 / 端口映射（允许外部访问）`，填两次密码后保存。保存时会自动生成 `sha256:<salt>:<hash>` 并写进配置的 `passwordHash` 字段，不需要手动算。

也可以命令行生成哈希：

```powershell
node .\tools\workbuddy-password-hash.mjs "你的密码"
```

再启动：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\start-workbuddy-remote.ps1 -ListenHost 0.0.0.0 -PasswordHash "sha256:..."
```

非 localhost 监听必须设置 `passwordHash`（配置）、`-PasswordHash` 或环境变量 `WORKBUDDY_REMOTE_PASSWORD_HASH`。

开启局域网后，本机和手机都可以访问 `http://<本机IP>:8780/agent-manager/`。如果要从外网访问，用 UU 远程之类的端口映射把 `8780` 映射出去，再打开映射后的地址即可；桥接层不关心域名和协议，页面里的资源路径都是相对于根路径的。

## 网页端能力（远程也能用）

这几处以前会把操作甩到服务器本机，现在都在网页里完成：

- **选择工作空间 / 选择目录**：`dialog:pickFolder`、`window:selectDirectory` 被拦截，改为网页里的**文件浏览器对话框**：左侧「常用位置 / 此电脑」、路径栏（可直接输入回车跳转）、上一级 / 刷新、名称 / 修改日期 / 类型 / 大小 列表、双击进目录、单击选中、新建文件夹，以及「选择文件夹 / 取消」。可以浏览服务端任意盘符和目录，不再局限于预设的工作空间。接口在 `/bridge/fs/{roots,list,mkdir}`，只在 `enableFileManager` 打开时可用。
- **设置**：桌面端点设置会打开独立窗口，远程时那个窗口开在服务器上。网页端改成页内浮层 iframe 加载同一套设置页（`?windowAppId=settings`），功能与桌面一致，Esc / 点遮罩 / 右上角「关闭」可退出。
- **WorkDaddy 全功能面板**：装了 [WorkDaddy](https://github.com/babygoton/WorkDaddy) 后，网页右下角会出现它的机器人按钮，点开就是**与桌面端完全一致的面板**：账号（切换 / 备注 / 删除 / 登录新账号 / 导入导出）、主题、会话、模型、增强、自动化、电脑、关于。
  - 面板不是重写的：桥接按 WorkDaddy daemon 的拼装规则（`markdown-preview.js` + `toast-runtime.js` + `workbuddy-compat.js` + `inject.js`，含 `automation-picker.js`）构建出 `/workdaddy/panel.js`，只把 `__WBS_API__` 改写成同源前缀 `/workdaddy`，其余占位符（版本 / profile / caps / logo / platform）按 `workbuddy-cn` 填充。
  - 浏览器访问不到 `127.0.0.1:47832`，所以 API 走同源代理：`/workdaddy/api/*` → 本地 daemon，令牌由服务端从 `%APPDATA%\WorkDaddy\.api-token` 补上，不暴露给浏览器。端口自动探测 47832–47835，可用 `WORKDADDY_API_PORT` 覆盖。
  - 切换账号成功后，daemon 只会重载桌面端 renderer；网页端由 shim 监听 `/workdaddy/api/switch` 的成功响应，3.5 秒后自动刷新，避免“切了还显示旧账号”。
  - 未安装 / 未运行 WorkDaddy 时，按钮和面板都不会出现。
- **启动器一并拉起 WorkDaddy**：`start-workbuddy-remote.ps1` 会在桥接就绪后检查 WorkDaddy，没运行就用 `WorkDaddyLauncher.exe` 启动（控制台 `设置` 里可关掉）；同时通过 `WBSWITCH_CDP_PORT` 把它的 CDP 端口对齐到本项目的 `cdpPort`，这样它才能注入面板、并在切换账号后让客户端重载。检测到端口不一致会自动重启一次 daemon。
- **查看所有产物**：`getSessionArtifacts` 等接口的超时从 35s 放宽到 120s（`invokeDesktopHost` 也按通道放宽到 120s 并重试一次），避免产物多的会话在远程加载时直接报失败。


## 配置

`workbuddy-remote.config.json`:

```json
{
  "workbuddyExePath": "",
  "workbuddyUserDataDir": "",
  "runtimeRootDir": "output/runtime",
  "cdpPort": 9333,
  "bridgePort": 8780,
  "listenHost": "127.0.0.1",
  "passwordHash": "",
  "killWorkBuddyProcessesBeforeStart": false,
  "hideWorkBuddyWindowAfterStart": true,
  "maskBridgeModelSecrets": true,
  "enableFileManager": true,
  "enableRestart": true
}
```

`workbuddyExePath` 留空时会自动查找常见安装路径。

`hideWorkBuddyWindowAfterStart` 设为 `true` 时，bridge 连接到 WorkBuddy 后会自动请求隐藏桌面窗口；WorkBuddy 进程和托盘仍保留，浏览器页面继续可用。

`maskBridgeModelSecrets` 设为 `true` 时，bridge 会先在服务端拦截模型配置读取结果，把接口地址 / Base URL 和 API Key 替换成占位值，再返回给浏览器；页面上也会继续把对应输入框显示为圆点。

## 常见问题

**端口映射/局域网访问时，浏览器标签页的图标不显示？**

之前有两个原因，现在都已修复：

1. 桥接开密码保护后，浏览器请求 `/favicon.ico` 时还没有登录 Cookie，被 401 JSON 拦下；而页面里又没有 `<link rel="icon">`，浏览器不会在登录后重试，于是标签页一直是默认图标。
2. `/favicon.ico` 原来返回的是 PNG 内容（`Content-Type: image/png`），部分浏览器和映射代理不认。

现在 `/favicon.ico` 直接返回 WorkBuddy 自带的 `resources/icon.ico`（`image/x-icon`），另外提供 `/favicon.png` 与 `/apple-touch-icon.png`，页面和登录页都会注入图标声明，这几个路径不要求登录。

如果仍然显示旧图标，是浏览器缓存了之前的失败结果：强制刷新（Ctrl+F5）或换个无痕窗口即可。

**提示"WorkBuddy 正在运行，但没有开启调试端口"？**

见上面「WorkBuddy 5.9 的启动方式」，点 `重启 WorkBuddy` 一次即可。

## 进程和连接

刷新网页只会断开并重建浏览器到桥接服务的 WebSocket，不会创建新的 WorkBuddy 进程。浏览器 WebSocket 断开时，服务会释放该页面注册的事件订阅，避免刷新后订阅堆积。

桌面 WorkBuddy 仍然负责原生会话、任务、内存和账号状态。这个项目只做远程页面和调用转发，不再包含原来那套 C/native 内存管理代码。

## 检查

```powershell
npm run check
```

## 重新构建控制台 exe

源代码在 `launcher/`（.NET 8 WinForms），改完 UI 后执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\build-launcher.ps1
```

产物会覆盖根目录的 `WorkBuddyRemote.exe`。需要 .NET 8 SDK；运行 exe 需要 .NET 8 Desktop Runtime。

加 `-SelfContained` 可以编译成自带运行时的单文件（体积约 140 MB，换机器时无需装运行时）。

开发时可以用离屏渲染检查界面：

```powershell
.\WorkBuddyRemote.exe --render=ui-preview.png
.\WorkBuddyRemote.exe --render-settings=settings-preview.png
```

## 控制台（WinUI 3 版）

控制台已迁移到 **WinUI 3**（Windows App SDK 1.8），界面全部使用官方控件库（[microsoft-ui-xaml](https://github.com/microsoft/microsoft-ui-xaml)）的标准组件，不做自绘：`NavigationView` 导航外壳、`InfoBar` 状态条、`SettingsCard` 式设置项、`NumberBox` / `ToggleSwitch` / `ComboBox` / `PasswordBox`、`ProgressBar`，并启用 Mica 背景与自定义标题栏。源码在 `launcher-winui/`。

```powershell
cd .\launcher-winui
dotnet build -c Release -p:Platform=x64
```

产物在 `launcher-winui\bin\x64\Release\net8.0-windows10.0.19041.0\`，运行时**要把整个目录一起复制**（XAML 编译产物 `.xbf` 和一堆 WinUI DLL 缺一不可，不能只拿 exe）。已部署到仓库根的 `console\`，双击 `WorkBuddyRemote-WinUI.vbs` 启动。

界面结构：

- **导航**：`概览` / `运行日志` / `关于`，底部是 NavigationView 内置的齿轮 `设置` 与 `打开网页`
- **概览**：状态 `InfoBar`（运行中 / 启动中 / 已停止）、访问与 WorkBuddy 两张信息卡片、操作按钮（启动 / 重启桥接 / 停止 / 打开页面 / 重启 WorkBuddy / 日志目录）、最近日志预览
- **运行日志**：等宽日志框 + 打开目录 / 复制 / 清空
- **设置**：SettingsCard 行式布局（访问范围、桥接端口、CDP 端口、访问密码、主程序与数据目录、各项功能开关），保存后用 `InfoBar` 反馈，错误就地提示
- **关于**：应用图标、版本、项目 / 配置 / 日志路径与快捷入口
- **图标**：沿用旧启动器 `launcher/assets/app.ico`（exe 图标、窗口图标、关于页与标题栏共用）

行为细节：

- 开机自动启动写入启动文件夹的 `WorkBuddy Remote 控制台.lnk`（带 `--autostart`）；识别到旧版 `WorkBuddy Remote.lnk` 时视为已开启，保存设置时自动迁移，避免开机拉起两个控制台
- `--autostart` 时窗口最小化到任务栏；手动启动若继承了“最小化”状态会自动还原
- 暂未做托盘图标：静默启动后从任务栏恢复窗口

> 老版本 `WorkBuddyRemote.exe`（WinForms）仍然保留可用，`tools/build-launcher.ps1` 依旧构建它。需要 .NET 8 SDK；NuGet 无全局源时，`launcher-winui/NuGet.config` 已固定 nuget.org。
