# WorkBuddy Remote

> The official WorkBuddy Remote / web client exists now: <https://www.codebuddy.cn/agents>. If you just want the fastest path, use the official one.
>
> This project is for people who want local data ownership, LAN/intranet access, controllable API configuration, or a custom console and bridge.

Serve the desktop WorkBuddy as a browser workbench over CDP:

1. Read the original `renderer/index.html` and static assets from the desktop app's `resources/app.asar`.
2. Inject a lightweight `buddyAPI` shim into the page.
3. Forward `buddyAPI` calls to the desktop renderer's CDP target over one WebSocket.

Version **2.0.0**: the console (.NET 10 + WinUI 3) spawns the Node bridge directly and binds it with a Job Object. No VBS, no PowerShell entry point.

## Layout

```text
bridge/            Node bridge (only runtime dependency: ws)
apps/console/      Windows console (.NET 10 + WinUI 3)
installer/         Inno Setup script
scripts/           Dev/CI scripts (PS1 is no longer an entry point)
.github/workflows/ ci.yml + release.yml
```

## Install

| Asset | Notes |
|---|---|
| `WorkBuddyRemote-<ver>-setup.exe` | **Recommended.** Self-contained .NET 10 + Windows App SDK + Node |
| `WorkBuddyRemote-<ver>-slim-setup.exe` | Needs .NET 10 Desktop Runtime + Windows App Runtime |
| `WorkBuddyRemote-<ver>-portable.zip` | Unzip to a fixed folder first, then run (config lives next to the exe, see `portable.flag`) |

> **Unzip before running** the portable build. Double-clicking the exe inside the zip makes Windows extract the whole archive to a temp folder and open it in Explorer (which looks like "a File Explorer window pops up on startup"), and that repeats on every run.

Per-user install into `%LOCALAPPDATA%\Programs\WorkBuddyRemote` (no admin). `checksums.txt` lists SHA256 for every asset.

> **Not code-signed.** SmartScreen may warn on first run: "More info -> Run anyway". Verify the SHA256 or build from source if you prefer.

## First run

- Detects WorkBuddy (running process -> uninstall registry -> common paths) and stores the path.
- Imports a v1 `workbuddy-remote.config.json` when present (invalid paths are dropped).
- LAN access requires a password.
- Closing the window minimizes to tray (configurable); double-click restores it.
- Single instance: launching again only brings the existing window forward.
- A busy bridge port advances to the next free port; the console shows the real one.

## Build from source

Requires **.NET 10 SDK**, **Node.js 20+**, and **Inno Setup 6** for packaging.

```powershell
npm ci
npm run check
dotnet build apps/console/WorkBuddyRemote.WinUI.csproj -c Release -p:Platform=x64

.\scripts\build-release.ps1                  # both installers + portable zip into dist\
.\scripts\build-release.ps1 -SkipInstaller   # portable zip only
```

> Packaging gotcha: `dotnet publish` swaps `Microsoft.UI.Xaml.dll` for the self-contained flavour while keeping a framework-dependent apphost, and drops the app's `.pri`. The result crashes during XAML init (0xc000027b). The release script therefore uses `dotnet build` output and verifies `.xbf` / `.pri` before packaging.

## Configuration

Installed: `%APPDATA%\WorkBuddyRemote\config.json`
Portable: `config.json` next to the exe (when `portable.flag` exists)

```json
{
  "workbuddyExePath": "",
  "cdpPort": 9333,
  "bridgePort": 8780,
  "listenHost": "127.0.0.1",
  "passwordHash": "",
  "closeToTray": true,
  "keepBridgeOnExit": false,
  "startWorkDaddy": false,
  "enableFileManager": true,
  "enableRestart": true,
  "maskBridgeModelSecrets": false
}
```

Writes are atomic (temp file + replace); unknown keys are preserved.

## Supported WorkBuddy versions

This build targets **WorkBuddy 5.9.2** (the same bridge was verified on 5.9.0 – 5.9.2): CDP connection, `renderer` asset loading and shim injection all tested.

The console's About page shows both the **supported version** and the **locally detected version**. If the local build falls outside the verified range it is marked "unverified".

## WorkBuddy 5.9+ needs an env var

Since 5.9.0 WorkBuddy ignores `--remote-debugging-port` and reads:

```text
WORKBUDDY_REMOTE_DEBUGGING_PORT=9333
```

The main process appends the Chromium switch itself, so the setting survives auto-updates. An already running WorkBuddy cannot be upgraded in place: restart it once from the console.

## LAN access

Pick "LAN / port mapping" in settings and set a password; the console stores a `sha256:<salt>:<hash>` hash.

```powershell
node .\scripts\workbuddy-password-hash.mjs "your-password"
```

Non-loopback listeners require a password. Phones can open `http://<ip>:8780/agent-manager/`.

## Web features

- Folder picker becomes an in-page file browser (`/bridge/fs/*`, gated by `enableFileManager`).
- Settings open as an in-page overlay iframe instead of a separate desktop window.
- WorkDaddy panel is proxied same-origin (`/workdaddy/api/*`) with the token added server-side.
- Artifact calls get a 120s timeout.

## Checks

```powershell
npm run check
.\scripts\smoke.ps1
```

CI runs syntax checks, a bridge smoke test and `dotnet build`; `v*` tags build all three assets plus `checksums.txt`.

## Third party

- WorkDaddy is **AGPL-3.0**. Nothing of it is bundled here; it is off by default and only read from an existing local install when the user enables it. Source: <https://github.com/babygoton/WorkDaddy>.
- WorkBuddy UI assets come from the locally installed official client; no WorkBuddy binaries are in this repository.

## License

MIT, see [LICENSE](LICENSE).
