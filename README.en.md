# WorkBuddy Remote New

> WorkBuddy now has an official WorkBuddy Remote / web WorkBuddy at <https://www.codebuddy.cn/agents>. If you want the fastest setup path, use the official WorkBuddy Remote first.
>
> This project is still useful when you prefer local or intranet deployment for data control, custom API settings, or a customizable UI and bridge layer.

This is the refactored WorkBuddy remote bridge. Because the official WorkBuddy frontend has been rebuilt, this project has moved away from the older heavy relay/native mirror approach and now serves the official WebUI directly from `resources/app.asar`, injects a lightweight browser `buddyAPI` shim, and forwards calls to the desktop WorkBuddy window through CDP.

Compared with the old version, the standalone light/native frontend, VS Code IPC mirror, dynamic MessagePort relay, and large window cleanup layer are gone. The browser loads the official WebUI resources, while this bridge only fills in the native capabilities that a normal browser does not have. That keeps the project smaller, shortens startup, and makes the page open much faster.

## Setup

```powershell
cd C:\Users\Public\workbuddy-remote-new
npm install
```

## Start Locally

Double-click:

```text
WorkBuddyRemote.exe
```

This is the recommended way. It is a native control panel that shows live status (bridge / desktop CDP / listen mode), with Start, Restart bridge, Stop, Open page and Logs on the first row, plus Settings and Restart WorkBuddy on the second row. Settings is a graphical editor for ports, local/LAN mode, the access password, the WorkBuddy paths and the feature toggles; it writes back to `workbuddy-remote.config.json`. Closing the window keeps it in the tray and the bridge keeps running; right-click the tray icon to quit.

Legacy launcher (still supported):

```text
start-workbuddy-remote.vbs
```

It hides the PowerShell window, installs missing Node dependencies, starts WorkBuddy and the lightweight bridge, then opens the browser when ready.

Stop the bridge only (desktop WorkBuddy keeps running):

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\stop-workbuddy-remote.ps1
```

Manual PowerShell start:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\start-workbuddy-remote.ps1 -ListenHost 127.0.0.1
```

Open:

```text
http://127.0.0.1:8780/agent-manager/
```

If the CDP port is already listening, the launcher reuses the existing WorkBuddy instance.

## WorkBuddy 5.9 launch flag (important)

WorkBuddy 5.9 no longer forwards Chromium switches such as `--remote-debugging-port` or `--user-data-dir`. It reads an environment variable instead:

```text
WORKBUDDY_REMOTE_DEBUGGING_PORT=9333
```

The main process appends `remote-debugging-port` and `remote-allow-origins` itself. This project now sets only that variable, which also survives the in-place updater relaunch (command line arguments are lost, environment variables are not).

A WorkBuddy instance that is already running cannot open the debug port afterwards, so upgrade once and restart the desktop app: use the `Restart WorkBuddy` button in the control panel (it interrupts the current conversation, chat history is kept). If the start script detects a running instance without the port, it exits with code 42 immediately and the panel offers the restart instead of waiting 30 seconds for a timeout.

## LAN Access

Use `Settings` → `LAN / port mapping` in the control panel and type the password twice. The panel generates the `sha256:<salt>:<hash>` value and stores it in `passwordHash`.

Or from the command line:

```powershell
node .\tools\workbuddy-password-hash.mjs "your-password"
powershell -ExecutionPolicy Bypass -File .\tools\start-workbuddy-remote.ps1 -ListenHost 0.0.0.0 -PasswordHash "sha256:..."
```

Remote listening requires a password hash.

To reach it from the internet, map port `8780` with a tool such as UU Remote and open the mapped URL. All page assets use root-relative paths, so the bridge does not care about the host name or scheme.

## Remote-capable web features

These used to pop up on the host machine; they now happen inside the web page:

- **Pick workspace / pick directory**: `dialog:pickFolder` and `window:selectDirectory` open an in-page **file browser dialog** — sidebar with quick locations and drives, editable path bar (Enter to jump), up/refresh, a name/date/type/size list, double-click to enter, click to select, create-folder, and Choose/Cancel. Any drive and folder on the host can be browsed, not just preset workspaces. Backed by `/bridge/fs/{roots,list,mkdir}` and only enabled when `enableFileManager` is on.
- **Settings**: the desktop opens settings in a separate native window. On the web the same settings app is loaded in an in-page overlay iframe (`?windowAppId=settings`). Esc, a backdrop click, or the Close button dismisses it.
- **Full WorkDaddy panel**: with [WorkDaddy](https://github.com/babygoton/WorkDaddy) installed, its robot button appears bottom-right and opens the **same panel as the desktop app**: accounts (switch / note / delete / sign in / import-export), themes, sessions, models, enhance, automations, power and about.
  - The panel is not reimplemented: the bridge assembles WorkDaddy's own scripts the same way its daemon does (`markdown-preview.js` + `toast-runtime.js` + `workbuddy-compat.js` + `inject.js`, plus `automation-picker.js`) and serves them at `/workdaddy/panel.js`, rewriting only `__WBS_API__` to the same-origin prefix `/workdaddy` and filling the remaining placeholders (version / profile / caps / logo / platform) for `workbuddy-cn`.
  - The browser cannot reach `127.0.0.1:47832`, so API calls go through a same-origin proxy: `/workdaddy/api/*` → local daemon, with the token added server-side from `%APPDATA%\WorkDaddy\.api-token` (never exposed to the browser). Ports 47832–47835 are probed automatically; `WORKDADDY_API_PORT` overrides.
  - After a successful switch the daemon only reloads the desktop renderer; the shim watches `/workdaddy/api/switch` and reloads the web page 3.5s later, so the account list is never stale.
- **The launcher starts WorkDaddy too**: `start-workbuddy-remote.ps1` checks WorkDaddy after the bridge is ready and starts `WorkDaddyLauncher.exe` when needed (toggle in the console `Settings`). It also aligns WorkDaddy's CDP port with this project's `cdpPort` through `WBSWITCH_CDP_PORT`; a mismatched daemon is restarted once, which is required for panel injection and post-switch reloads.
- **View all artifacts**: timeouts for `getSessionArtifacts` and other artifact/session channels are raised to 120s (with one retry), so large sessions no longer fail to load over a remote connection.


## Config

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

## FAQ

**The favicon is missing when the page is reached through port mapping?**

Two causes, both fixed now: the favicon request happened before login and was answered with a 401 JSON payload, and `/favicon.ico` used to return PNG bytes with an `image/png` type. The bridge now serves the real `resources/icon.ico` as `image/x-icon`, exposes `/favicon.png` and `/apple-touch-icon.png`, injects icon links into the app page and the login page, and lets those paths load without authentication. If a browser still shows the old blank icon, hard-refresh (Ctrl+F5) to drop the cached failure.

When `maskBridgeModelSecrets` is `true`, the bridge redacts model API address / Base URL and API Key values on the server before returning config data to the browser, and the page also masks those inputs visually.

When `hideWorkBuddyWindowAfterStart` is `true`, the bridge asks WorkBuddy to hide its desktop window after the CDP connection is ready. The WorkBuddy process and tray entry stay alive, so the browser page remains usable.

Refreshing the web page only recreates the browser WebSocket. It does not create a new WorkBuddy process, and socket cleanup releases event subscriptions for the closed page.

## Console (WinUI 3)

The console is built with **WinUI 3** (Windows App SDK 1.8) and uses only the official control library ([microsoft-ui-xaml](https://github.com/microsoft/microsoft-ui-xaml)) — no hand-drawn UI: a `NavigationView` shell, `InfoBar` status, `SettingsCard`-style rows, `NumberBox` / `ToggleSwitch` / `ComboBox` / `PasswordBox`, plus a Mica backdrop and a custom title bar. Sources live in `launcher-winui/`.

Build with `dotnet build -c Release -p:Platform=x64` inside `launcher-winui/`. The output directory must be copied as a whole (the XAML `.xbf` files and the WinUI DLLs are required). It is deployed to `console\`; double-click `WorkBuddyRemote-WinUI.vbs` to start it.

Layout: **Overview** (status InfoBar, connection and WorkBuddy cards, action buttons, recent log), **Logs** (monospace log box with open/copy/clear), **About**, plus the built-in Settings gear and an "Open page" footer item. The app icon is reused from the old launcher (`launcher/assets/app.ico`) for the exe, window, title bar and About page.

Autostart creates `WorkBuddy Remote 控制台.lnk` in the Startup folder with `--autostart`; an existing legacy `WorkBuddy Remote.lnk` counts as enabled and is migrated on save. With `--autostart` the window starts minimized; a manual launch restores itself if it inherited a minimized state. There is no tray icon yet.
