# WorkBuddy Remote v2 迁移交接文档

> 写于 2026-10-24。给下一个会话用：读完这一份就能直接开工，不需要翻聊天记录。
> 文档自身会随 Phase 0 的提交进入 v1-final，并随仓库迁移到新仓。

---

## 0. 怎么用这份文档

1. 先读 §1（已定决策）和 §6（已知坑），不要再重新讨论已经定下的事。
2. 按 §4 的阶段顺序执行，每个阶段结束先跑该阶段的验收命令，通过再进下一阶段。
3. §9 是开工前必须先验证的开放项，任何一条不成立就停下来报告，不要硬推。
4. 遇到与文档冲突的现实情况（版本号、路径、API 变化），以现实为准，但要在本文件末尾的「执行记录」里写明偏差。

---

## 1. 已定决策（锁定，不再讨论）

| # | 决策 | 结论 |
|---|---|---|
| 1 | 重构范围 | **保留 Node 内核**。只做启动链 / 进程模型 / 打包 / 仓库工程化。不换语言、不重写注入与 CDP 逻辑 |
| 2 | 大文件 | v2 **不拆** `workbuddy-native.mjs`(4839) / `runtime.mjs`(2434) / `bridge-server.mjs`(2110)，只做定点改动（抽出 `bridge/host/`）。拆分留到 v2.1 |
| 3 | 仓库 | 新仓 `github.com/Panda35064/workbuddy-remote-winui3` = 唯一主仓，**带完整历史**；旧仓 `vergess3/workbuddy-remote` 归档只读 |
| 4 | 分支 | 先整理 v1 提交 → 打 `v1-final` → 新仓开 `v2` 分支开发 → 稳定后合 `main` |
| 5 | 工作目录 | 新目录 `D:\Program Files\Tools\workbuddy-remote-v2`；旧目录保持不动，作为 v1 参照 |
| 6 | 进程模型 | 控制台 exe 直接 `spawn` node 子进程，Job Object 绑定生命周期。PS1 **降级**为开发/CI 用，不再是对外入口 |
| 7 | 启动方式 | 删除 VBS。exe 自己解析项目根、读配置、拉起桥接 |
| 8 | 打包 | Inno Setup EXE，装到 `%LOCALAPPDATA%`（免管理员）+ 便携 zip；**双包**：自包含版（默认）/ 精简版 |
| 9 | 签名 | **不签名**。Release 说明写清 SmartScreen 提示与 SHA256 |
| 10 | 更新 | 应用内检查 GitHub `releases/latest`，只提示 + 跳转下载，**不静默替换** |
| 11 | CI | GitHub Actions：push/PR 跑构建+冒烟；`v*` tag 自动发 Release（双包 + SHA256） |
| 12 | 版本 | 从 **2.0.0** 起；`package.json` 是唯一版本源，CI 注入 csproj 与安装包 |
| 13 | 平台 | Windows x64，Win10 19041+ |
| 14 | 工具链 | 升 **.NET 10**（.NET 8 于 2026-11-10 终止支持）；Windows App SDK 先留 1.8，暂不升 2.x |
| 15 | 配置 | 安装版 `%APPDATA%\WorkBuddyRemote\config.json`；便携版检测 `portable.flag` 用同目录 |
| 16 | 首次运行 | 自动探测 WorkBuddy + 旧配置可导入 + 仅局域网访问时强制密码 |
| 17 | 端口冲突 | 桥接端口自动顺延并写回配置；CDP 端口只在确认非目标 WorkBuddy 占用时顺延 |
| 18 | 托盘 | **Win32 `Shell_NotifyIcon` P/Invoke**（不用 H.NotifyIcon）。点 X 默认收进托盘，设置可改；双击托盘唤回窗口 |
| 19 | 单实例 | 命名 Mutex；第二次启动唤回已有窗口并置前，自身退出 |
| 20 | WorkDaddy | 只保留接口，设置项**默认关**；不打包其任何文件；未检测到就显示项目链接（AGPL-3.0，见 §6.7） |
| 21 | 诊断 | 一键导出 zip：日志 + 脱敏配置 + 系统信息 |
| 22 | 目录结构 | `bridge/` + `apps/console/` + `installer/` + `scripts/` + `.github/workflows/`，根目录只留 README/LICENSE/package.json/config.example |
| 23 | 删除 | `launcher/`（WinForms）、两个 VBS、`tools/build-launcher.ps1` |

### 非目标（v2 不做）

- 不拆三个大文件、不换技术栈、不改注入逻辑
- 不做 MSIX、不做代码签名、不做静默自动更新
- 不做 arm64、不做多语言、不做遥测

---

## 2. 现状事实（2026-10-24 只读核对）

### 仓库
- 远端：`https://v4.gh-proxy.org/https://github.com/vergess3/workbuddy-remote.git`（MIT，公开）
- 当前分支：`fix/workbuddy-5.8-webui`，HEAD `89a7b68`，比 `origin/main` 领先 1 个提交，无 upstream
- 受控文件 **22 个**，提交 20 条（其中 5 组是 Fix/Revert 成对提交）
- 未提交：**9 改 + 1397/−322**，另有 **9 个未跟踪条目**
- **严重问题：`launcher/` 与 `launcher-winui/` 全部源码都不在 git 里**（`git ls-files launcher` 为空）
- `.workbuddy/` 既未跟踪也未忽略（本文件所在目录），**不要提交**，加入 `.gitignore`
- 构建产物已忽略：`console/`、`launcher*/bin|obj|publish`、`output/`、根 `WorkBuddyRemote.exe`

### 代码规模
| 文件 | 行数 |
|---|---:|
| `src/web/workbuddy-native.mjs` | 4839 |
| `src/bridge/runtime.mjs` | 2434 |
| `src/server/bridge-server.mjs` | 2110 |
| `src/workspace/service.mjs` | 561 |
| 其余 10 个 `.mjs` | 合计 ~1400 |
| `launcher/*.cs`（WinForms，将删除） | 2355 |
| `launcher-winui/*.cs|*.xaml` | 1637 |
| `tools/start-workbuddy-remote.ps1` | 798 |

### 入口与进程（四层）
```
VBS (24 行)
  └─ PowerShell start-workbuddy-remote.ps1 (798 行, 21 函数, 11 阶段)
       ├─ 启动 WorkBuddy.exe（等 CDP 端口）
       ├─ Start-Process node src/main.mjs
       └─ 启动 WorkDaddy（可选）
控制台 exe（WinUI3）──也是调用同一个 PS1
```
- `src/main.mjs` 只做三件事：进程异常守卫 → 解析参数 → `BridgeRuntime` + `startBridgeServer()` + `warmup()`
- Node 依赖：**仅 `ws`**（纯 JS），但**依赖系统安装 Node**（找不到就 throw）；本机 Node v24.17.0，`node.exe` 88 MB
- 端口：桥接 8780、CDP 9333、WorkDaddy API 47832–47835
- 退出码契约：`0` 就绪 / `42` 需重启 WorkBuddy / `43` CDP 超时

### 质量门禁
- `npm run check` = 12 条 `node --check`（**只是语法检查**）
- 无测试、无 CI、无 lint、无类型检查、无 husky
- 版本号三处不一致：package.json 1.0.0 / WinUI 1.1.0 / WinForms 1.0.0

### 打包
- 无安装器、无 MSIX、无签名、无自动更新
- WinUI 目前手工 `dotnet build` 后整目录复制到 `console/`（78 MB / 109 文件）
- `dotnet publish` 出的目录**缺 `.xbf`**，会启动即崩 —— 只能部署 build 输出整目录
- 本机只有 .NET SDK **8.0.422**；无 Inno/NSIS；证书只有 VMware 内部证书（**不可用于分发**）

### 已有成果（直接复用，不要重写）
- 控制台 UI：NavigationView 外壳（概览 / 运行日志 / 关于 + 内置齿轮设置）、`InfoBar` 状态、`SettingsCard` 行式设置、Mica + 自定义标题栏；托盘与单实例尚未实现（v2 新做）
- `launcher-winui/StartupShortcut.cs`：启动文件夹 `.lnk`（`--autostart`），并识别/迁移旧 `WorkBuddy Remote.lnk`
- 开机静默启动：`--autostart` 时若返回 42（WorkBuddy 没开调试端口）自动带 `-RestartWorkBuddy` 再跑一次
- 网页端：远程文件浏览（`/bridge/fs/*`）、设置浮层、WorkDaddy 面板与同源代理、产物接口超时放宽到 120s
- 这些逻辑的正确性已经实机验证过，v2 只改“谁拉起谁”，不要顺手重写业务逻辑

---

## 3. 目标结构

```
workbuddy-remote-v2/
├─ bridge/                    # 原 src/，逻辑不动
│  ├─ main.mjs
│  ├─ host/                   # 新增：配置、WorkBuddy 探测、端口分配、诊断包
│  ├─ bridge/ server/ web/ workspace/
│  ├─ config.mjs  logger.mjs  shared.mjs  asar.mjs
├─ apps/console/              # 原 launcher-winui/
│  ├─ AppHost.cs              # 从 launcher/AppHost.cs 移入（AppSettings/NetProbe/Locators）
│  ├─ BridgeHost.cs           # 新增：spawn node + Job Object + 健康轮询 + 日志接管
│  ├─ TrayIcon.cs             # 新增：Shell_NotifyIcon + MenuFlyout
│  ├─ SingleInstance.cs       # 新增：Mutex + 激活
│  ├─ MainWindow.xaml(.cs)  SettingsCard.*  StartupShortcut.cs
│  └─ Assets/app.ico|app.png
├─ installer/                 # Inno .iss + 安装期资源
├─ scripts/                   # 原 tools/（PS1 仅供开发/CI）
├─ .github/workflows/         # ci.yml + release.yml
├─ package.json               # 版本唯一源 + ws 依赖
├─ config.example.json        # 取代带本机绝对路径的 config
└─ README.md / LICENSE / .gitignore
```

`AppHost.cs` 的命名空间是 `WorkBuddyRemote`（父命名空间），移入后 `WorkBuddyRemote.WinUI` 仍能解析，无需改命名空间。可顺手删掉其中 WinForms 用的 `Theme` 类以去掉 `System.Drawing` 依赖（非必须）。

---

## 4. 执行阶段

### Phase 0 · v1 入库与冻结（在旧目录执行）

目标：把散落的改动变成可回退的历史，打 `v1-final`。

```bash
cd "D:/Program Files/Tools/workbuddy-remote"

# 0.1 .workbuddy 不入库
printf '\n# 代理工作记忆\n.workbuddy/\n' >> .gitignore

# 0.2 四个语义提交
git add src/web/workbuddy-native.mjs src/server/bridge-server.mjs src/bridge/runtime.mjs \
        src/server/access-auth.mjs src/server/fs-browse.mjs \
        src/server/workdaddy-proxy.mjs src/server/workdaddy-panel.mjs
git commit -m "feat(web): 远程文件浏览、设置浮层与 WorkDaddy 代理接入"

git add launcher launcher-winui WorkBuddyRemote-WinUI.vbs tools/build-launcher.ps1
git commit -m "feat(console): WinUI 3 控制台与设置界面"

git add tools/start-workbuddy-remote.ps1 tools/stop-workbuddy-remote.ps1 workbuddy-remote.config.json
git commit -m "feat(startup): 启动脚本支持 WorkDaddy 与 CDP 端口对齐"

git add .gitignore README.md README.en.md MIGRATION-HANDOFF.md
git commit -m "docs: 更新说明并加入 v2 迁移交接文档"

# 0.3 合入 main 并打 tag
git switch main
git merge --no-ff fix/workbuddy-5.8-webui -m "merge: v1 最终状态"
git tag -a v1-final -m "v1 最终版本（WinForms + WinUI 双控制台）"
git push origin main
git push origin v1-final
```

**验收**
```bash
git status --porcelain            # 空
git tag -l v1-final               # 存在
npm run check                     # 通过
```
再启动一次 `WorkBuddyRemote-WinUI.vbs` 确认控制台仍能起来（回归基线）。

**回滚**：任何后续阶段出问题，`git switch -c rescue v1-final` 即可回到可运行状态。

---

### Phase 1 · 新仓、目录重排、CI 骨架、.NET 10

**前置**：装 .NET 10 SDK（`winget install Microsoft.DotNet.SDK.10`，或官方安装包；装完 `dotnet --list-sdks` 应看到 `10.0.x`）。装在 8.0 旁边即可，不冲突。

```bash
# 1.1 推全部本地分支与 tag（不要用 --mirror，避免把 refs/remotes 推上去）
git remote add v2 https://github.com/Panda35064/workbuddy-remote-winui3.git
git push v2 --all
git push v2 --tags

# 1.2 新目录工作副本
git clone https://github.com/Panda35064/workbuddy-remote-winui3.git "D:/Program Files/Tools/workbuddy-remote-v2"
cd "D:/Program Files/Tools/workbuddy-remote-v2"
git switch -c v2

# 1.3 目录重排（用 git mv 保留历史）
mkdir -p apps installer .github/workflows
git mv src bridge
git mv launcher-winui apps/console
git mv tools scripts
git mv launcher/AppHost.cs apps/console/AppHost.cs
git rm -r launcher
git rm WorkBuddyRemote-WinUI.vbs start-workbuddy-remote.vbs
git rm scripts/build-launcher.ps1
git rm workbuddy-remote.config.json
```

随后手工改动（不能靠 git mv 的部分）：

1. `apps/console/WorkBuddyRemote.WinUI.csproj`
   - 删除 `<Compile Include="..\launcher\AppHost.cs" ... />`
   - TFM 改 `net10.0-windows10.0.19041.0`
   - 保留 `Assets\app.ico|app.png` 的 Content 项
   - `ApplicationIcon` 不变
2. `package.json`
   - `start` → `node bridge/main.mjs`
   - `check` 里所有 `src/` → `bridge/`，并把末尾 `tools/workbuddy-password-hash.mjs` → `scripts/…`
3. `scripts/start-workbuddy-remote.ps1`
   - `$mainScript = Join-Path $projectRoot "src\main.mjs"` → `bridge\main.mjs`
   - 其它 `src\` 字面量一并替换
4. `scripts/stop-workbuddy-remote.ps1` 注释与匹配串里的 `src\main.mjs` → `bridge\main.mjs`
5. `bridge/bridge/runtime.mjs` 第 15–21 行：`RESTART_HELPER_SCRIPT_PATH` 里拼的 `"tools"` → `"scripts"`
   （它按 `__dirname/../../tools/...` 解析，目录改名后不改这一处，网页端“重启 WorkBuddy”会直接失效）
6. 新增 `config.example.json`（端口、监听、开关；**不含任何绝对路径**）
7. `.gitignore` 补：`apps/console/bin|obj|publish`、`installer/out`、`dist/`
8. 新增 `.github/workflows/ci.yml`：
   - `windows-latest`；`actions/setup-dotnet@v4` 装 `10.0.x`；`actions/setup-node@v4` 装 24
   - `npm ci` → `npm run check` → 起 bridge 打 `/readyz`（冒烟）→ `dotnet build apps/console -c Release -p:Platform=x64`
   - 上传 `apps/console/bin/x64/Release/net10.0-windows10.0.19041.0/` 为 artifact

**验收**
```bash
git status --porcelain                       # 空
# 不允许再出现旧路径引用（应为空输出）
grep -rn "src/main.mjs" package.json scripts bridge
grep -rn "launcher" apps/console/*.csproj
npm ci && npm run check
node bridge/main.mjs --port 8799 &           # curl 127.0.0.1:8799/readyz 返回 ok
dotnet build apps/console -c Release -p:Platform=x64   # 0 error
```
另：**全新克隆**到临时目录，重复上面两条构建命令，确认不依赖任何本机残留。

**回滚**：`v1-final`（旧目录仍在，可随时对照）。

---

### Phase 2 · 启动链 + 托盘 + 单实例（风险最高）

新增代码（建议顺序）：

1. **`apps/console/BridgeHost.cs`**
   - `spawn(node.exe, [bridge/main.mjs, --port, --cdp-port, ...])`，`RedirectStandardOutput/Error` 到 `output/runtime/temp/bridge-<port>.log`（**不要**让它继承父进程管道）
   - Job Object：`CreateJobObject` + `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` + `AssignProcessToJobObject`
   - 健康轮询 `/readyz`；状态变更事件回调窗口
2. **配置**：`%APPDATA%\WorkBuddyRemote\config.json`；首次运行自动写默认值；兼容导入旧 `workbuddy-remote.config.json`（路径字段无效则丢弃）
3. **WorkBuddy 探测**：注册表 `Uninstall` 键 → `%ProgramFiles%` 常见路径 → 运行中进程的 `ExecutablePath`
4. **端口分配**：探测 8780..8799；被占用则顺延并写回配置；CDP 端口只有在占用者不是目标 `WorkBuddy.exe` 时才顺延
5. **`TrayIcon.cs`**：`Shell_NotifyIcon`（NIM_ADD/NIM_SETVERSION/NIM_DELETE）+ 回调窗口消息；双击还原；右键 `MenuFlyout`（打开控制台 / 打开网页 / 启动·停止桥接 / 退出）；气泡只用一次“仍在后台运行”
6. **关闭语义**：`AppWindow.Closing` → 若“关闭即退出”关闭则 `args.Cancel = true` + `AppWindow.Hide()`；托盘“退出”走真正退出
7. **`SingleInstance.cs`**：`Mutex("Global\\WorkBuddyRemote.Console")`；第二实例通过命名管道（或 `WM_COPYDATA`）通知首实例 `ShowWindow + SetForegroundWindow`，然后退出
8. 设置页新增：`关闭窗口时退出`、`关闭窗口时最小化到托盘提示已看过`、`WorkDaddy 集成（默认关）`、`保持桥接在控制台退出后运行（默认关）`

**验收**
- 删除 VBS 后双击 `apps/console` 的 exe 能起；**全程没有任何 PowerShell 窗口闪现**
- 关窗口 → 托盘常驻（首次气泡一次）；托盘双击唤回；托盘退出 → node 进程结束
- 再次双击 exe → 不产生第二个进程/第二个托盘图标，只把窗口置前
- 手工占用 8780 再启动 → 自动用 8781，且界面显示实际端口
- 断网/无 node 时给出明确错误，不是静默失败

**回滚**：`scripts/start-workbuddy-remote.ps1` 保留完整能力，可用它绕过控制台直接起桥接排查。

---

### Phase 3 · 打包与发布

**前置**：装 Inno Setup 6（`winget install JRSoftware.InnoSetup`），确认 `ISCC.exe` 在 `%ProgramFiles(x86)%\Inno Setup 6\`。

1. `installer/WorkBuddyRemote.iss`（Inno Setup 6）
   - `DefaultDirName={localappdata}\Programs\WorkBuddyRemote`、`PrivilegesRequired=lowest`
   - 开始菜单快捷方式 + 卸载项；安装后可选“开机启动”（写 `HKCU\...\Run` 或启动文件夹，与 App 内开关同一实现）
   - 通过 `/DAppVersion=2.0.0`、`/DPayload=...` 参数化
2. 载荷布局
   ```
   WorkBuddyRemote/
   ├─ WorkBuddyRemoteWinUI.exe + WinUI 运行时文件（自包含版含 .NET + WinAppSDK）
   ├─ bridge/  node_modules/ws
   ├─ runtime/node/node.exe        # 随包，不依赖系统 Node
   └─ assets/
   ```
3. 两种构建
   - 自包含：`dotnet publish apps/console -c Release -p:Platform=x64 -r win-x64 --self-contained true -p:WindowsAppSDKSelfContained=true -p:PublishSingleFile=false`
   - 精简：`--self-contained false -p:WindowsAppSDKSelfContained=false`（安装器检测 .NET 8/10 与 WinAppRuntime，缺失则提示下载）
   - **注意**：`publish` 目录可能缺 `.xbf`，打包含前必须校验 `*.xbf` 数量 ≥ 3，否则拒绝出包
4. `release.yml`：`v*` tag → 构建双包 → Inno 两次 → `sha256sum` → `actions/upload-release-asset` 或 `gh release create`
5. 应用内更新检查：`GET https://api.github.com/repos/Panda35064/workbuddy-remote-winui3/releases/latest`（失败回退 `v4.gh-proxy.org` 前缀），只比较版本号并弹 InfoBar

**验收**
- 干净 Win10 虚拟机（未装 Node/.NET/WinAppRuntime）：自包含安装包 → 安装 → 首次启动 → 浏览器可访问
- 精简版在缺运行库时给出明确提示
- 安装目录下没有写入失败；卸载后 `%APPDATA%` 配置保留（符合预期）
- Release 里双包 + `checksums.txt` 齐全

---

### Phase 4 · 收尾

1. 诊断包：`GET /bridge/diagnostics` 或控制台按钮 → zip（日志、脱敏配置、OS build、.NET/node/WorkBuddy 版本、端口状态）；密码哈希、token、用户名脱敏
2. WorkDaddy 开关：默认关；开启后 `/workdaddy/status` 才探测；未安装显示项目链接（`https://github.com/babygoton/WorkDaddy`）
3. README / README.en：未签名说明、SHA256、AGPL 第三方声明、从源码构建步骤
4. Release notes 模板：变更点 / 已知问题 / 未签名提示 / 校验方法
5. 冒烟脚本 `scripts/smoke.ps1`：起桥接 → `/readyz` → 摘 `/workdaddy/status` → 停桥接

---

## 5. 关键技术要点（实现时直接照做）

### 5.1 子进程与管道
- node 的 stdout/stderr **必须**重定向到文件（`ProcessStartInfo.RedirectStandardOutput = true` + 异步读，或直接 `-RedirectStandardOutput` 等价物）。历史坑：脚本拉起的 GUI 进程继承管道句柄，导致 `WaitForExit()` 永远等 EOF。
- 用 `process.Exited` 事件 + 超时，不要裸 `WaitForExit()`。

### 5.2 Job Object
```csharp
CreateJobObject(IntPtr.Zero, null);
var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { BasicLimitInformation = { LimitFlags = 0x2000 } }; // KILL_ON_JOB_CLOSE
SetInformationJobObject(job, 9, ref info, size);
AssignProcessToJobObject(job, process.Handle);
```
“控制台退出后保持桥接运行”= 不把子进程放进 Job（或显式 `CREATE_BREAKAWAY_FROM_JOB`），并在退出时 `Dispose` 句柄但保留进程。

### 5.3 托盘
- `NOTIFYICONDATAW` 大小随系统版本变化，用 `Marshal.SizeOf` 并设置 `cbSize`
- 回调消息用 `WM_APP + 1`，通过 `SetWindowSubclass`（comctl32）挂到 WinUI 的 HWND（`WinRT.Interop.WindowNative.GetWindowHandle(this)`）
- 右键菜单：取光标屏幕坐标 → 转成 root 元素坐标 → `MenuFlyout.ShowAt(RootGrid, new FlyoutShowOptions { Position = pt })`
- 图标：`ExtractIconEx` 从 `Assets/app.ico` 取，或 `LoadImage` + `IMAGE_ICON`
- 退出时务必 `NIM_DELETE`，否则托盘留下幽灵图标

### 5.4 单实例
- `Mutex` 名带 `Global\` 前缀（跨会话）或 `Local\`（仅当前会话，推荐 `Local\`，避免多用户互斥）
- 第二实例：连接命名管道 `\\.\pipe\workbuddy-remote-console`，写一行 `activate`，首实例 `AppWindow.Show()` + `SetForegroundWindow`

### 5.5 配置
```json
{
  "bridgePort": 8780,
  "cdpPort": 9333,
  "listenHost": "127.0.0.1",
  "passwordHash": "",
  "workBuddyExePath": "",
  "userDataDir": "",
  "startWorkDaddy": false,
  "closeToTray": true,
  "keepBridgeOnExit": false,
  "enableFileManager": true,
  "enableRestart": true,
  "maskModelSecrets": false
}
```
- 读配置要容错：未知字段保留、缺失字段用默认值、端口非法回落
- 写配置用「临时文件 + 原子替换」，避免断电写坏

### 5.6 Node 与项目根定位
- 解析顺序：`<安装目录>\runtime\node\node.exe`（随包）→ `%ProgramFiles%\nodejs\node.exe` → `%ProgramFiles(x86)%\nodejs\node.exe` → `PATH` 里的 `node`
- 项目根解析顺序：exe 所在目录向上找 `bridge/main.mjs` → 注册表/配置里记录的安装目录 → 当前工作目录；**不能**再依赖 VBS 切 CWD
- 桥接脚本路径：`<root>\bridge\main.mjs`；依赖 `node_modules` 在根目录（`npm ci --omit=dev` 产出）
- 打包后目录结构必须与开发目录一致，否则 `bridge/*.mjs` 里 `../shared.mjs` 这类相对导入会失败

---

## 6. 已知坑（都踩过，别再踩）

1. **WinUI 部署缺 `.xbf` 会启动即崩**：`dotnet publish -o` 出来的目录不完整，必须用 `bin/x64/Release/<tfm>/` 整目录，或 publish 后校验 `.xbf` 数量。
2. **PowerShell 5.1 环境变量大小写重复**：本机同时有 `HTTPS_PROXY` 和 `https_proxy`，带重定向的 `Start-Process` 会抛「已添加项。字典中的关键字…」，进程根本起不来。启动 WorkDaddy/node 时用 `System.Diagnostics.ProcessStartInfo` + `UseShellExecute=true` 绕开，或先做环境去重。
3. **`WaitForExit()` 挂死**：见 5.1。
4. **本机 dotnet 没有全局 NuGet 源**：`launcher-winui/NuGet.config` 固定了 nuget.org，迁移后保留在 `apps/console/NuGet.config`，否则 NU1100。
5. **MicaBackdrop 命名空间**：在 `Microsoft.UI.Xaml.Media`，不是 `Microsoft.UI.Composition.SystemBackdrops`。
6. **UIA 的 ScrollPattern 对 WinUI ScrollViewer 会抛「对象状态无效」**：验证界面时直接 `SetWindowPos` 拉高窗口再 `PrintWindow`，不要折腾滚动。
7. **WorkDaddy 是 AGPL-3.0**：我们现在的做法是把它的 `inject.js` 拼装后由我们的 HTTP 服务对外提供，AGPL §13 的网络条款可能要求提供源码。v2 的处理：不打包其文件、默认关闭、用户主动开启、README 单独声明。**不要把账号切换当作宣传卖点**。
8. **shim 是模板字面量**：`renderWorkBuddyNativeShimJs` 里写 `\n`、`/\//g` 会被吃掉一层转义，生成的 JS 语法错误导致整页白屏。改完必须 `node --check` 生成结果。
9. **网页端 HTML 里注入的 CSS 不要碰桌面端**：历史 bug 是把 `#root` 的 `margin-top/height` 和窗口控件 `display:none` 注入到桌面渲染进程，直接把标题栏干掉了。桌面端只做“清理旧版本残留”，隐藏逻辑只留在 shim。
10. **端口 9333 是 WorkBuddy 的 CDP 端口**，WorkDaddy 默认认 9222；要让 WorkDaddy 注入生效，必须用 `WBSWITCH_CDP_PORT` 对齐到 9333。
11. **未签名 + 注入型程序**：Defender/国产杀软误报概率不低，发布页给 SHA256 与源码链接，不要建议关杀软。

---

## 7. 完成定义（八条，缺一不算完）

1. 新仓 fresh clone → `npm ci` + `dotnet build` 两条命令成功
2. 安装包免管理员安装，安装后能启动
3. 无 VBS、无 PowerShell 窗口闪现
4. 端口冲突自动顺延且界面可见
5. 便携版配置写在程序目录
6. `git status` 干净；版本号只有 `package.json` 一处
7. PR 冒烟绿；`v*` tag 产出双包 + SHA256 的 Release
8. 诊断包可一键导出且已脱敏

---

## 8. 发布物与分支约定

- 分支：`main`（稳定）、`v2`（开发）、`feat/*`、`fix/*`
- tag：`v1-final`（旧版冻结）、`v2.0.0`、`v2.0.1`…
- Release 资产命名：
  - `WorkBuddyRemote-2.0.0-setup.exe`（自包含）
  - `WorkBuddyRemote-2.0.0-slim-setup.exe`（精简）
  - `WorkBuddyRemote-2.0.0-portable.zip`
  - `checksums.txt`
- 提交信息：`type(scope): 中文描述`，type ∈ feat/fix/docs/chore/refactor/build/ci

---

## 9. 开工前必须验证的开放项

| # | 事项 | 不成立的应对 |
|---|---|---|
| 1 | GitHub 能推 `Panda35064/workbuddy-remote-winui3`（git 走 https，`gh api` 不可用） | 用 `git push` 试推一个空提交；失败则让用户建 token 或改 SSH |
| 2 | 旧仓归档只能靠 GitHub 网页（`gh api` 403） | 交给用户点一下 Archive |
| 3 | 本机能装 .NET 10 SDK（winget 或官方安装包） | 先只改 TFM 不编译，等 SDK 到位再继续 |
| 4 | net10 + WindowsAppSDK 1.8 能编译 | 若报 XAML/编译器不兼容，评估 WinAppSDK 2.x，并在执行记录里写明 |
| 5 | 能装上 Inno Setup 6（`winget install JRSoftware.InnoSetup`） | 暂时只出便携 zip，安装器延后 |
| 6 | 有一台干净 Win10/11 可测（VM 或实机） | 至少用「新建本地用户 + 不装运行时」近似验证 |

---

## 10. 工作约定（给下一个会话）

- 全程简体中文，直接给结论和可执行命令，不要客套。
- 每个阶段结束必须给出**可验证的证据**：命令输出、`git status`、截图或日志，不要只说“已完成”。
- 改代码前先 `git status`，确认工作区干净；每阶段一个（或几个）语义提交，不要攒大提交。
- 不要用 `git push --force`；不要改历史（除了 Phase 0 之前本来就未提交的内容）。
- 追加日志/文档时只写增量，不要用 `a` 模式重写全文。
- 涉及杀进程、删目录、改注册表的操作，先确认目标，再执行。

---

## 11. 执行记录（后续会话填写）

| 日期 | 阶段 | 结果 | 偏差/备注 |
|---|---|---|---|
| 2026-10-24 | Phase 0 | 完成。4 个语义提交 + `--no-ff` 合入 main，打 `v1-final`，工作区干净，`npm run check` 通过 | **旧仓 `vergess3/workbuddy-remote` 推送被拒（403，`Panda35064` 无写权限）**。改为把 `main`、`fix/workbuddy-5.8-webui`、`v1-final` 直接推到新仓。旧仓归档仍需用户在网页上操作 |
| 2026-10-24 | Phase 1 | 完成。目录重排（`bridge/` `apps/console/` `scripts/`）、删 WinForms/VBS、TFM 升 `net10.0-windows10.0.19041.0`、加 `config.example.json` + `.gitignore` + `ci.yml`；fresh clone 下 `npm ci` / `npm run check` / `dotnet build` 全通过（0 error） | ① 本机原本只有 .NET 8，已用 winget 装 **10.0.402**；net10 + WinAppSDK 1.8 编译正常（§9-4 成立）。② 本机 Node 是 **v22.22.2**，不是文档写的 v24。③ CI 里 `/readyz` 必然是 503（没有 WorkBuddy），冒烟改用已有的 `/healthz`；为此新增 `WORKBUDDY_ALLOW_MISSING_ASAR=1` 降级开关，让没有 WorkBuddy 时桥接也能起（生产不设该变量，行为不变） |
| 2026-10-24 | Phase 2 | 完成。新增 `BridgeHost.cs`（直接 spawn node、Job Object、健康轮询、端口顺延）、`TrayIcon.cs`、`SingleInstance.cs`；配置改到 `%APPDATA%\WorkBuddyRemote\config.json`（便携版 `portable.flag` 同目录），首次运行自动写默认值并导入 v1 配置；设置页新增“关闭窗口时收进托盘”“退出后保留桥接” | ① 实测：exe 起桥接无 PowerShell；关窗口后进程存活、`trayTipShown` 落盘、托盘图标创建成功；再次双击 exe 只唤回窗口（单实例）；kill 控制台后 node 随之结束（Job Object 生效）；手工占用 8780 时自动用 8781 并写回配置。② 托盘**未**使用 `NIM_SETVERSION` v4，保持 lParam 直接是鼠标消息，避免版本位解析差异。③ 桥接新增 `WORKBUDDY_REMOTE_CONFIG` 环境变量（读控制台配置）与 `bridge-<port>.pid`（用于停止上次保留的桥接）；控制台额外传 `WORKBUDDY_EXE_PATH`，否则自定义安装路径下找不到 app.asar |
| 2026-10-24 | Phase 3 | 完成。`installer/WorkBuddyRemote.iss`（纯 ASCII、`PrivilegesRequired=lowest`、`/DAppVersion` `/DPayload` `/DFlavorSuffix` `/DCheckRuntime` 参数化）+ `scripts/build-release.ps1` + `.github/workflows/release.yml`；产出 `setup.exe` 80 MB、`slim-setup.exe` 29 MB、`portable.zip` 116 MB 与 `checksums.txt`。实测：静默安装到临时目录 → 启动 → 桥接起来，且 node 用的是安装目录下的 `runtime\node\node.exe`（不依赖系统 Node） | ① **关键坑（新）**：`dotnet publish` 会把 `Microsoft.UI.Xaml.dll` 换成自包含变体、同时保留框架依赖的 apphost，并丢掉应用自己的 `.pri`，结果启动即崩（`Microsoft.UI.Xaml.dll` / 0xc000027b）。改用 `dotnet build`（`-r win-x64 -p:SelfContained=true -p:WindowsAppSDKSelfContained=true`）的输出做载荷后正常。② winget 拉 Inno 安装包被墙，改为走本机代理下载 6.7.3 并 `/CURRENTUSER` 静默安装到 `%LOCALAPPDATA%\Programs\Inno Setup 6`。③ 安装器的“开机启动”写 `WorkBuddyRemote.lnk`（ASCII），`StartupShortcut` 已兼容识别/清理。④ **尚未做**：干净 Win10/11 虚拟机验证（无可用 VM），slim 缺 .NET 运行时的提示（`[Code]` 里已加，未实测） |
| 2026-10-24 | Phase 4 | 完成。控制台“导出诊断包”一键 zip（日志 + 脱敏配置 + 系统信息，实测脱敏正确、日志用共享读避免占用报错）；WorkDaddy 默认关、README/README.en 重写；`scripts/smoke.ps1` 实测通过；`release.yml` 内置 Release notes 模板；概览页新增更新检查（GitHub `releases/latest`，只提示 + 跳转） | ① 诊断包做在**控制台**而不是 `/bridge/diagnostics`，避免给桥接加 zip 依赖。② 控制台状态判定用 `/healthz`（200 即进程活着）+ `/readyz`（WorkBuddy 是否连上），两者语义分开。③ `smoke.ps1` 踩到 PS 5.1 的 `HTTPS_PROXY`/`https_proxy` 重复键坑，已在脚本和两个 workflow 里做环境去重 |
| 2026-10-24 | Phase 4 补 | 安装器本地化与参数向导：`[Languages]` 加英文 + 简体中文（`installer/languages/ChineseSimplified.isl`，取自 Inno 官方仓库），默认跟随系统语言且始终显示语言选择；新增两个自定义向导页（访问范围 / 端口 + 访问密码，密码框用 `Edits[i].Password := True` 掩码），带端口范围、同端口、密码必填/长度/一致性校验；安装参数通过 `install-overrides.json` 交给控制台首次启动消费（转 sha256 后删除） | ① **`.iss` 必须 UTF-8 with BOM**，否则中文全部乱码；`Edit` 工具改文件后要复查 BOM。② Inno 的 `SaveStringToFile` 写的是 ANSI(GBK)，中文密码会坏 → 安装器用 UTF-16 十六进制传递，控制台侧还原后再哈希（实测中文密码哈希一致、`/` 返回 401）。③ PascalScript 没有 `IntToHex`/`TryStrToInt`、不支持函数内 `const`、`TInputQueryWizardPage` 无 `PasswordChar`（用 `Password` 属性，6.7 才有）。④ 静默安装且已有配置时跳过写入，避免覆盖用户设置 |
| | | | |
