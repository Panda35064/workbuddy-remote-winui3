# Changelog

本项目的所有重要变更都记录在这里。格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [2.0.0] - 2026-10-30

首个 WinUI 3 控制台版本。相比上游 [vergess3/workbuddy-remote](https://github.com/vergess3/workbuddy-remote) 的脚本形态，改为「控制台 + 桥接 + 浏览器 shim」三件套，并集中修掉远程访问的一批体验问题。

### 新增

- **WinUI 3 控制台**：概览 / 运行日志 / 设置 / 关于四页，托盘常驻、单实例、开机静默启动。
- **桥接生命周期管理**：控制台用 Job Object 拉起并绑定 Node 桥接进程，不再依赖 PowerShell / VBS 脚本。
- **访问控制**：仅本机 / 局域网两种模式，`sha256:<salt>:<hash>` 访问密码；切到仅本机时密码项自动隐藏且不启用。
- **远程文件浏览**：页内目录选择器，接口 `/bridge/fs/{roots,list,mkdir}`，支持新建文件夹。
- **WorkDaddy 集成**：设置页可配置程序地址并一键检测（手动指定 → 运行中进程 → 卸载注册表 → 常见目录）；桥接就绪后按需拉起并注入 `WBSWITCH_CDP_PORT`。网页端右下角提供全功能面板，API 走同源代理。
- **可观测性**：桥接事件日志落盘（`bridge-<port>.events.log`），浏览器 `console.error` 与未捕获异常自动上报。
- **构建信息**：概览页显示 `CI <commit> · <编译时间>` 或 `Release <版本>`。

### 修复

- **远程页面卡在加载界面**：`workbuddyDesktop` 缺少 preload 的 621 个方法，`getProductConfiguration` 等调用退化成不存在的通道。已在 shim 中补齐方法映射。
- **新建任务后不自动跳转到新会话**：远程链路上 `conversations:create` 的响应会丢失，前端拿不到 `sessionId`；且内存路由只改 location、不重渲染视图。改为 create 后按标题轮询点击左侧列表项（等价手动点击），0.5s 起步、命中即跳。
- **新建后又被拉回旧会话**：桌面端运行中的会话持续推事件覆盖当前视图。按会话 id 过滤普通事件，同时放行 `created` / `deleted` / `archived` 等生命周期事件。
- **会话页 404**：`/agent-manager/conversation/<id>` 等前端路由缺少 SPA 回退，刷新或直接访问会 404。
- **访问密码清除不掉**：密码框留空时不写盘，旧密码一直生效；改为按访问范围显隐，非本机模式强制要求密码。
- **托盘右键不弹出 / 菜单被压在最底层**：XAML `MenuFlyout` 依赖窗口前台与有效 `XamlRoot`。改用 H.NotifyIcon 的 WinUI 风格菜单（`SecondWindow` 模式），并新增「重启桥接」。
- **重启后自动弹浏览器**：启动 / 重启不再自动打开网页。
- **开机自启还留窗口**：改为 `AppWindow.Hide()`，只在托盘保留图标；拉起 WorkBuddy 后自动收起它的窗口。
- **远程目录选择无响应**：`dialog:pickFolder` / `window:selectDirectory` 通道在网页端没有实现，改为走桥接的目录浏览器。

### 变更

- 工作区/配置目录、日志路径、端口全部纳入设置页可配置，未知字段在保存时保留。
- 新增上游署名（README / 关于页 / LICENSE）与非官方声明。

### 已知问题

- 托盘菜单（`SecondWindow` 模式）的展开动画方向固定为向下，与系统托盘的向上动画相反，属第三方库限制。
- 构建产物未签名，首次运行会触发 SmartScreen 提示。
