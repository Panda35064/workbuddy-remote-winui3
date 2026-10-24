param(
    [Nullable[int]]$CdpPort = $null,
    [Nullable[int]]$BridgePort = $null,
    [string]$ListenHost,
    [string]$WorkBuddyExePath,
    [string]$UserDataDir,
    [string]$PasswordHash,
    [switch]$KillWorkBuddyProcessesBeforeStart,
    [switch]$HideWorkBuddyWindowAfterStart,
    [switch]$OpenBrowser,
    [switch]$RestartWorkBuddy,
    [switch]$StartWorkDaddy
)

# Exit codes consumed by the WinForms console.
# 0  = ready
# 42 = WorkBuddy is already running without the debug port (needs a restart)
# 43 = WorkBuddy did not expose the debug port in time
$script:EXIT_NEEDS_WORKBUDDY_RESTART = 42
$script:EXIT_CDP_TIMEOUT = 43

$ErrorActionPreference = "Stop"
$scriptPath = $MyInvocation.MyCommand.Path
$scriptDir = Split-Path -Parent $scriptPath
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $scriptDir "..")).Path
$configPath = Join-Path $projectRoot "workbuddy-remote.config.json"

function Show-StartupError {
    param([string]$Message)

    try {
        Add-Type -AssemblyName System.Windows.Forms
        [System.Windows.Forms.MessageBox]::Show(
            $Message,
            "WorkBuddy Remote Failed to Start",
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Error
        ) | Out-Null
    }
    catch {
        try {
            [Console]::Error.WriteLine($Message)
        }
        catch {
        }
    }
}

trap {
    $message = if ($_.Exception) { $_.Exception.Message } else { "$_" }
    Show-StartupError -Message $message
    exit 1
}

function Get-Config {
    if (-not (Test-Path -LiteralPath $configPath)) {
        return [pscustomobject]@{}
    }
    $raw = Get-Content -LiteralPath $configPath -Raw
    if ([string]::IsNullOrWhiteSpace($raw)) {
        return [pscustomobject]@{}
    }
    return $raw | ConvertFrom-Json
}

function Resolve-ProjectPath {
    param([string]$PathValue)
    $expanded = [Environment]::ExpandEnvironmentVariables($PathValue)
    if (-not [System.IO.Path]::IsPathRooted($expanded)) {
        $expanded = Join-Path $projectRoot $expanded
    }
    return [System.IO.Path]::GetFullPath($expanded)
}

function Get-ConfigString {
    param([object]$Config, [string]$Name)
    $value = $Config.$Name
    if ($value -is [string] -and -not [string]::IsNullOrWhiteSpace($value)) {
        return $value.Trim()
    }
    return $null
}

function Get-ConfigInt {
    param([object]$Config, [string]$Name, [int]$Fallback)
    $value = $Config.$Name
    if ($value -is [int] -and $value -gt 0) {
        return $value
    }
    if ($value -is [long] -and $value -gt 0) {
        return [int]$value
    }
    if ($value -is [string]) {
        $parsed = 0
        if ([int]::TryParse($value.Trim(), [ref]$parsed) -and $parsed -gt 0) {
            return $parsed
        }
    }
    return $Fallback
}

function Get-ConfigBool {
    param([object]$Config, [string]$Name, [bool]$Fallback)
    $value = $Config.$Name
    if ($value -is [bool]) {
        return $value
    }
    if ($value -is [string]) {
        $normalized = $value.Trim().ToLowerInvariant()
        if ($normalized -in @("1", "true", "yes", "on")) {
            return $true
        }
        if ($normalized -in @("0", "false", "no", "off")) {
            return $false
        }
    }
    return $Fallback
}

function Find-NodeExecutable {
    $command = Get-Command node -ErrorAction SilentlyContinue
    if ($command -and $command.Source -and (Test-Path -LiteralPath $command.Source)) {
        return $command.Source
    }

    $candidates = @()
    if ($env:ProgramFiles) {
        $candidates += Join-Path $env:ProgramFiles "nodejs\node.exe"
    }
    if (${env:ProgramFiles(x86)}) {
        $candidates += Join-Path ${env:ProgramFiles(x86)} "nodejs\node.exe"
    }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    throw "Node.js was not found. Install Node.js first, or add node.exe to PATH."
}

function Find-NpmExecutable {
    $command = Get-Command npm.cmd -ErrorAction SilentlyContinue
    if ($command -and $command.Source -and (Test-Path -LiteralPath $command.Source)) {
        return $command.Source
    }

    $command = Get-Command npm -ErrorAction SilentlyContinue
    if ($command -and $command.Source -and (Test-Path -LiteralPath $command.Source)) {
        return $command.Source
    }

    $candidates = @()
    if ($env:ProgramFiles) {
        $candidates += Join-Path $env:ProgramFiles "nodejs\npm.cmd"
    }
    if (${env:ProgramFiles(x86)}) {
        $candidates += Join-Path ${env:ProgramFiles(x86)} "nodejs\npm.cmd"
    }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    throw "npm was not found. Install Node.js first, or add npm.cmd to PATH."
}

function Ensure-NodeDependencies {
    param([string]$TempDir)

    $packageDir = Join-Path $projectRoot "node_modules\ws"
    if (Test-Path -LiteralPath $packageDir) {
        return
    }

    $npmPath = Find-NpmExecutable
    $installLog = Join-Path $TempDir "npm-install.log"
    $installErr = Join-Path $TempDir "npm-install.err.log"
    Remove-Item $installLog, $installErr -ErrorAction SilentlyContinue

    Write-Host "Installing Node dependencies..."
    $installProcess = Start-Process `
        -FilePath $npmPath `
        -ArgumentList @("install", "--omit=dev") `
        -WorkingDirectory $projectRoot `
        -WindowStyle Hidden `
        -RedirectStandardOutput $installLog `
        -RedirectStandardError $installErr `
        -Wait `
        -PassThru

    if ($installProcess.ExitCode -ne 0) {
        $stderr = ""
        if (Test-Path -LiteralPath $installErr) {
            $stderr = (Get-Content -LiteralPath $installErr -ErrorAction SilentlyContinue | Select-Object -First 30) -join [Environment]::NewLine
        }
        throw "npm install failed. Check log: $installErr`r`n$stderr"
    }

    if (-not (Test-Path -LiteralPath $packageDir)) {
        throw "Dependency ws is still missing after npm install. Check log: $installLog"
    }
}

function Find-WorkBuddyExecutable {
    param([object]$Config)

    if (-not [string]::IsNullOrWhiteSpace($WorkBuddyExePath)) {
        $resolved = Resolve-ProjectPath $WorkBuddyExePath
        if (-not (Test-Path -LiteralPath $resolved)) {
            throw "WorkBuddy executable path not found: $resolved"
        }
        return $resolved
    }

    $configuredPath = Get-ConfigString -Config $Config -Name "workbuddyExePath"
    if ($configuredPath) {
        $resolved = Resolve-ProjectPath $configuredPath
        if (-not (Test-Path -LiteralPath $resolved)) {
            throw "Configured WorkBuddy executable path not found: $resolved"
        }
        return $resolved
    }

    $projectParent = Split-Path -Parent $projectRoot
    $candidates = @(
        (Join-Path $projectParent "WorkBuddy\WorkBuddy.exe"),
        (Join-Path $projectParent "workbuddy\WorkBuddy.exe")
    )
    if ($env:LOCALAPPDATA) {
        $candidates += Join-Path $env:LOCALAPPDATA "Programs\WorkBuddy\WorkBuddy.exe"
    }
    if ($env:ProgramFiles) {
        $candidates += Join-Path $env:ProgramFiles "WorkBuddy\WorkBuddy.exe"
    }
    if (${env:ProgramFiles(x86)}) {
        $candidates += Join-Path ${env:ProgramFiles(x86)} "WorkBuddy\WorkBuddy.exe"
    }

    try {
        $siblingDirs = Get-ChildItem -LiteralPath $projectParent -Directory -ErrorAction Stop |
            Where-Object { $_.Name -match "workbuddy" -and $_.FullName -ne $projectRoot }
        foreach ($dir in $siblingDirs) {
            $candidates += Join-Path $dir.FullName "WorkBuddy.exe"
        }
    }
    catch {
    }

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }

    throw "WorkBuddy.exe was not found. Set workbuddyExePath in workbuddy-remote.config.json or pass -WorkBuddyExePath."
}

function Test-PortListening {
    param([int]$Port)
    $conn = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
    return $null -ne $conn
}

function Find-WorkDaddyLauncher {
    param([object]$Config)

    $configuredPath = Get-ConfigString -Config $Config -Name "workDaddyLauncherPath"
    if ($configuredPath) {
        $resolved = Resolve-ProjectPath $configuredPath
        if (Test-Path -LiteralPath $resolved) {
            return $resolved
        }
    }

    $candidates = @()
    if ($env:LOCALAPPDATA) {
        $candidates += Join-Path $env:LOCALAPPDATA "Programs\WorkDaddy\WorkDaddyLauncher.exe"
    }
    if ($env:ProgramFiles) {
        $candidates += Join-Path $env:ProgramFiles "WorkDaddy\WorkDaddyLauncher.exe"
    }
    if (${env:ProgramFiles(x86)}) {
        $candidates += Join-Path ${env:ProgramFiles(x86)} "WorkDaddy\WorkDaddyLauncher.exe"
    }
    $candidates += Join-Path (Split-Path -Parent $projectRoot) "WorkDaddy\WorkDaddyLauncher.exe"

    try {
        $uninstallRoots = @(
            "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*",
            "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*",
            "HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*"
        )
        $entries = Get-ItemProperty -Path $uninstallRoots -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName -like "*WorkDaddy*" -and $_.InstallLocation }
        foreach ($entry in $entries) {
            $candidates += Join-Path $entry.InstallLocation "WorkDaddyLauncher.exe"
        }
    }
    catch {
    }

    # 正在运行的 WorkDaddy 守护进程：<install>\scripts\runtime\node\node.exe
    try {
        $daemon = Get-CimInstance Win32_Process -Filter "Name='node.exe'" -ErrorAction Stop |
            Where-Object { $_.CommandLine -like "*WorkDaddy*daemon.js*" } |
            Select-Object -First 1
        if ($daemon -and $daemon.ExecutablePath) {
            $installRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $daemon.ExecutablePath)))
            $candidates += Join-Path $installRoot "WorkDaddyLauncher.exe"
        }
    }
    catch {
    }

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }

    try {
        $running = Get-CimInstance Win32_Process -Filter "Name='WorkDaddyLauncher.exe'" -ErrorAction Stop |
            Select-Object -First 1
        if ($running -and $running.ExecutablePath -and (Test-Path -LiteralPath $running.ExecutablePath)) {
            return [System.IO.Path]::GetFullPath($running.ExecutablePath)
        }
    }
    catch {
    }

    return ""
}

function Get-WorkDaddyCdpPort {
    $file = Join-Path $env:APPDATA "WorkDaddy\cdp-port.json"
    try {
        $data = Get-Content -LiteralPath $file -Raw -ErrorAction Stop | ConvertFrom-Json
        if ($data -and $data.port) {
            return [int]$data.port
        }
    }
    catch {
    }
    return 0
}

function Stop-WorkDaddyDaemon {
    try {
        Get-CimInstance Win32_Process -Filter "Name='node.exe'" -ErrorAction Stop |
            Where-Object { $_.CommandLine -like "*WorkDaddy*daemon.js*" } |
            ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    }
    catch {
    }
}

function Start-WorkDaddyProcess {
    param(
        [string]$ExePath,
        [int]$CdpPort
    )

    # 把 WorkDaddy 的 CDP 端口对齐到本项目，否则它无法注入面板、
    # 切换账号后也无法让客户端重载。
    $previousCdpPort = $env:WBSWITCH_CDP_PORT
    $env:WBSWITCH_CDP_PORT = "$CdpPort"
    try {
        # 不能加重定向：PowerShell 5.1 在大小写重复的代理变量下会抛字典异常。
        Start-Process -FilePath $ExePath -WorkingDirectory (Split-Path -Parent $ExePath) | Out-Null
    }
    finally {
        if ($null -eq $previousCdpPort) {
            Remove-Item Env:WBSWITCH_CDP_PORT -ErrorAction SilentlyContinue
        }
        else {
            $env:WBSWITCH_CDP_PORT = $previousCdpPort
        }
    }
}

function Stop-PortProcess {
    param([int]$Port)
    $conn = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($conn) {
        Write-Host "Stopping process $($conn.OwningProcess) on port $Port..."
        Stop-Process -Id $conn.OwningProcess -Force
        Start-Sleep -Seconds 1
    }
}

function Test-BridgeReady {
    param([string]$HealthUrl)
    try {
        $response = Invoke-WebRequest -Uri $HealthUrl -UseBasicParsing -TimeoutSec 2
        return $response.StatusCode -ge 200 -and $response.StatusCode -lt 300
    }
    catch {
        return $false
    }
}

function Open-BridgeUrlSafely {
    param([string]$Url)

    try {
        Start-Process -FilePath $Url | Out-Null
        return
    }
    catch {
    }

    $rundll32Path = Join-Path $env:SystemRoot "System32\rundll32.exe"
    if (Test-Path -LiteralPath $rundll32Path) {
        try {
            Start-Process -FilePath $rundll32Path -ArgumentList @("url.dll,FileProtocolHandler", $Url) -WindowStyle Hidden | Out-Null
            return
        }
        catch {
        }
    }

    $explorerPath = Join-Path $env:SystemRoot "explorer.exe"
    if (Test-Path -LiteralPath $explorerPath) {
        try {
            Start-Process -FilePath $explorerPath -ArgumentList @($Url) | Out-Null
            return
        }
        catch {
        }
    }

    Write-Host "Bridge is ready, but the browser could not be opened automatically: $Url"
}

function Quote-NativeArgument {
    param([string]$Value)

    if ([string]::IsNullOrEmpty($Value)) {
        return '""'
    }

    if ($Value -notmatch '[\s"]') {
        return $Value
    }

    $builder = [System.Text.StringBuilder]::new()
    [void]$builder.Append('"')
    $backslashCount = 0
    foreach ($char in $Value.ToCharArray()) {
        if ($char -eq '\') {
            $backslashCount++
            continue
        }
        if ($char -eq '"') {
            [void]$builder.Append('\' * ($backslashCount * 2 + 1))
            [void]$builder.Append($char)
            $backslashCount = 0
            continue
        }
        if ($backslashCount -gt 0) {
            [void]$builder.Append('\' * $backslashCount)
            $backslashCount = 0
        }
        [void]$builder.Append($char)
    }
    if ($backslashCount -gt 0) {
        [void]$builder.Append('\' * ($backslashCount * 2))
    }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Build-CommandLineArgumentString {
    param([object[]]$Arguments)

    # Start-Process -ArgumentList 会把数组用空格拼接，且不会自动加引号。
    # 路径里带空格（如 D:\Program Files\...）时必须自己补引号，否则子进程只收到 "D:\Program"。
    return (@($Arguments) | ForEach-Object { Quote-NativeArgument ([string]$_) }) -join " "
}

function Get-BridgeUrls {
    param([int]$Port, [string]$ListenAddress)
    $urls = [System.Collections.Specialized.OrderedDictionary]::new()
    $normalizedHost = $ListenAddress.Trim().ToLowerInvariant()
    $primaryHost = if ($normalizedHost -eq "0.0.0.0") { "127.0.0.1" } else { $ListenAddress }
    $primaryUrl = "http://$primaryHost`:$Port/agent-manager/"
    $urls[$primaryUrl] = $true

    if ($normalizedHost -eq "0.0.0.0") {
        try {
            $addresses = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
                Where-Object {
                    $_.IPAddress -and
                    $_.IPAddress -ne "127.0.0.1" -and
                    $_.IPAddress -notlike "169.254.*"
                } |
                Sort-Object -Property IPAddress -Unique
            foreach ($entry in $addresses) {
                $url = "http://$($entry.IPAddress):$Port/agent-manager/"
                if (-not $urls.Contains($url)) {
                    $urls[$url] = $true
                }
            }
        }
        catch {
        }
    }

    return @($urls.Keys)
}

$config = Get-Config
if (-not $PSBoundParameters.ContainsKey("CdpPort")) {
    $CdpPort = Get-ConfigInt -Config $config -Name "cdpPort" -Fallback 9333
}
if (-not $PSBoundParameters.ContainsKey("BridgePort")) {
    $BridgePort = Get-ConfigInt -Config $config -Name "bridgePort" -Fallback 8780
}
if (-not $PSBoundParameters.ContainsKey("ListenHost") -or [string]::IsNullOrWhiteSpace($ListenHost)) {
    $configuredListenHost = Get-ConfigString -Config $config -Name "listenHost"
    $ListenHost = if ($configuredListenHost) { $configuredListenHost } else { "127.0.0.1" }
}
if (-not $PSBoundParameters.ContainsKey("UserDataDir") -or [string]::IsNullOrWhiteSpace($UserDataDir)) {
    $configuredUserDataDir = Get-ConfigString -Config $config -Name "workbuddyUserDataDir"
    $UserDataDir = if ($configuredUserDataDir) {
        Resolve-ProjectPath $configuredUserDataDir
    }
    else {
        Join-Path $env:APPDATA "WorkBuddy"
    }
}
if (-not $PSBoundParameters.ContainsKey("PasswordHash") -or [string]::IsNullOrWhiteSpace($PasswordHash)) {
    $configuredPasswordHash = Get-ConfigString -Config $config -Name "passwordHash"
    $PasswordHash = if ($configuredPasswordHash) { $configuredPasswordHash } else { $env:WORKBUDDY_REMOTE_PASSWORD_HASH }
}
if (-not $PSBoundParameters.ContainsKey("KillWorkBuddyProcessesBeforeStart")) {
    $KillWorkBuddyProcessesBeforeStart = Get-ConfigBool -Config $config -Name "killWorkBuddyProcessesBeforeStart" -Fallback $false
}
if (-not $PSBoundParameters.ContainsKey("HideWorkBuddyWindowAfterStart")) {
    $HideWorkBuddyWindowAfterStart = Get-ConfigBool -Config $config -Name "hideWorkBuddyWindowAfterStart" -Fallback $false
}
if (-not $PSBoundParameters.ContainsKey("StartWorkDaddy")) {
    $StartWorkDaddy = Get-ConfigBool -Config $config -Name "startWorkDaddy" -Fallback $true
}

$normalizedListenHost = $ListenHost.Trim().ToLowerInvariant()
if ($normalizedListenHost -notin @("127.0.0.1", "localhost", "::1") -and -not $PasswordHash) {
    throw "When ListenHost is not localhost, set -PasswordHash or WORKBUDDY_REMOTE_PASSWORD_HASH."
}

$runtimeRoot = Get-ConfigString -Config $config -Name "runtimeRootDir"
if ($runtimeRoot) {
    $runtimeRoot = Resolve-ProjectPath $runtimeRoot
}
else {
    $runtimeRoot = Join-Path $projectRoot "output\runtime"
}
$tempDir = Join-Path $runtimeRoot "temp"
New-Item -ItemType Directory -Force -Path $tempDir | Out-Null
New-Item -ItemType Directory -Force -Path $UserDataDir | Out-Null
Ensure-NodeDependencies -TempDir $tempDir

$bridgeLog = Join-Path $tempDir "bridge-$BridgePort.log"
$bridgeErr = Join-Path $tempDir "bridge-$BridgePort.err.log"
$bridgeEvents = Join-Path $tempDir "bridge-$BridgePort.events.log"
Remove-Item $bridgeLog, $bridgeErr, $bridgeEvents -ErrorAction SilentlyContinue

Write-Host "Stopping bridge on port $BridgePort (if any)..."
Stop-PortProcess -Port ([int]$BridgePort)

$exePath = Find-WorkBuddyExecutable -Config $config
$workBuddyPid = 0
$runningWorkBuddy = @(Get-Process WorkBuddy -ErrorAction SilentlyContinue)

if ($KillWorkBuddyProcessesBeforeStart -or $RestartWorkBuddy) {
    if ($runningWorkBuddy.Count -gt 0) {
        Write-Host "Stopping existing WorkBuddy processes..."
        $runningWorkBuddy | Stop-Process -Force
        for ($i = 0; $i -lt 20; $i++) {
            Start-Sleep -Milliseconds 500
            if (@(Get-Process WorkBuddy -ErrorAction SilentlyContinue).Count -eq 0) { break }
        }
    }
    $runningWorkBuddy = @()
}

if (Test-PortListening -Port ([int]$CdpPort)) {
    Write-Host "Reusing existing WorkBuddy CDP port $CdpPort."
}
elseif ($runningWorkBuddy.Count -gt 0) {
    # WorkBuddy 5.9.x only opens the DevTools port when it is started with the
    # debug flag already in its environment. A running instance cannot be
    # upgraded in place, so the app has to be restarted once.
    Write-Host "WorkBuddy is already running without a debug port on $CdpPort."
    Write-Host "WorkBuddy $((Get-Item -LiteralPath $exePath).VersionInfo.ProductVersion) needs a restart to enable the bridge."
    Write-Host "RESTART_WORKBUDDY_REQUIRED"
    exit $script:EXIT_NEEDS_WORKBUDDY_RESTART
}
else {
    Write-Host "Launching WorkBuddy with CDP on port $CdpPort..."
    Write-Host "User data dir: $UserDataDir"

    # WorkBuddy 5.9 uses its own environment switch instead of a Chromium
    # command line flag: main process reads WORKBUDDY_REMOTE_DEBUGGING_PORT and
    # appends --remote-debugging-port/--remote-allow-origins itself. This also
    # survives the in-place updater relaunch, which drops command line args.
    $previousDebugPortEnv = $env:WORKBUDDY_REMOTE_DEBUGGING_PORT
    $previousRunAsNodeEnv = $env:ELECTRON_RUN_AS_NODE
    $previousNodeOptionsEnv = $env:NODE_OPTIONS
    $env:WORKBUDDY_REMOTE_DEBUGGING_PORT = "$CdpPort"
    # When the console itself is launched from a WorkBuddy terminal, these two
    # variables are inherited and would turn WorkBuddy.exe into a plain node.
    Remove-Item Env:ELECTRON_RUN_AS_NODE -ErrorAction SilentlyContinue
    Remove-Item Env:NODE_OPTIONS -ErrorAction SilentlyContinue
    $workBuddyOutLog = Join-Path $tempDir "workbuddy-$CdpPort.out.log"
    $workBuddyErrLog = Join-Path $tempDir "workbuddy-$CdpPort.err.log"
    Remove-Item -LiteralPath $workBuddyOutLog, $workBuddyErrLog -ErrorAction SilentlyContinue
    try {
        # Redirect the app stdio on purpose: a GUI process started without redirection
        # inherits the caller's redirected pipe, keeps it open after this script exits,
        # and any caller waiting for EOF (the console window) hangs forever.
        $workBuddyProcess = Start-Process `
            -FilePath $exePath `
            -RedirectStandardOutput $workBuddyOutLog `
            -RedirectStandardError $workBuddyErrLog `
            -PassThru
        $workBuddyPid = $workBuddyProcess.Id
    }
    finally {
        if ($null -eq $previousDebugPortEnv) {
            Remove-Item Env:WORKBUDDY_REMOTE_DEBUGGING_PORT -ErrorAction SilentlyContinue
        }
        else {
            $env:WORKBUDDY_REMOTE_DEBUGGING_PORT = $previousDebugPortEnv
        }
        if ($null -ne $previousRunAsNodeEnv) {
            $env:ELECTRON_RUN_AS_NODE = $previousRunAsNodeEnv
        }
        if ($null -ne $previousNodeOptionsEnv) {
            $env:NODE_OPTIONS = $previousNodeOptionsEnv
        }
    }

    $cdpReady = $false
    for ($i = 0; $i -lt 60; $i++) {
        if (Test-PortListening -Port ([int]$CdpPort)) {
            $cdpReady = $true
            break
        }
        Start-Sleep -Seconds 1
    }
    if (-not $cdpReady) {
        Write-Host "WorkBuddy did not open the debug port $CdpPort in time."
        Write-Host "Check that no other WorkBuddy instance is holding the single instance lock."
        exit $script:EXIT_CDP_TIMEOUT
    }
    Write-Host "WorkBuddy debug port is ready on $CdpPort."
}

$nodePath = Find-NodeExecutable
$mainScript = Join-Path $projectRoot "src\main.mjs"
$bridgeArgs = @(
    $mainScript,
    "--cdp-port", "$CdpPort",
    "--host", "$ListenHost",
    "--port", "$BridgePort",
    "--user-data-dir", "$UserDataDir",
    "--workbuddy-pid", "$workBuddyPid",
    "--log-path", "$bridgeEvents"
)
if ($PasswordHash) {
    $bridgeArgs += @("--password-hash", "$PasswordHash")
}
if ($OpenBrowser) {
    $bridgeArgs += "--open-browser"
}
if ($HideWorkBuddyWindowAfterStart) {
    $bridgeArgs += "--hide-workbuddy-window-after-start"
}

$previousWorkBuddyExePath = $env:WORKBUDDY_EXE_PATH
$previousBridgeEventLogPath = $env:WORKBUDDY_REMOTE_EVENT_LOG_PATH
$previousBridgeUiLang = $env:WORKBUDDY_REMOTE_UI_LANG
$env:WORKBUDDY_EXE_PATH = $exePath
$env:WORKBUDDY_REMOTE_EVENT_LOG_PATH = $bridgeEvents
$env:WORKBUDDY_REMOTE_UI_LANG = [System.Globalization.CultureInfo]::CurrentUICulture.Name
try {
    Write-Host "Starting lightweight bridge on $ListenHost`:$BridgePort..."
    $bridgeArgumentLine = Build-CommandLineArgumentString -Arguments $bridgeArgs
    $bridgeProcess = Start-Process `
        -FilePath $nodePath `
        -ArgumentList $bridgeArgumentLine `
        -WorkingDirectory $projectRoot `
        -WindowStyle Hidden `
        -RedirectStandardOutput $bridgeLog `
        -RedirectStandardError $bridgeErr `
        -PassThru
}
finally {
    if ($null -eq $previousWorkBuddyExePath) {
        Remove-Item Env:WORKBUDDY_EXE_PATH -ErrorAction SilentlyContinue
    }
    else {
        $env:WORKBUDDY_EXE_PATH = $previousWorkBuddyExePath
    }

    if ($null -eq $previousBridgeEventLogPath) {
        Remove-Item Env:WORKBUDDY_REMOTE_EVENT_LOG_PATH -ErrorAction SilentlyContinue
    }
    else {
        $env:WORKBUDDY_REMOTE_EVENT_LOG_PATH = $previousBridgeEventLogPath
    }

    if ($null -eq $previousBridgeUiLang) {
        Remove-Item Env:WORKBUDDY_REMOTE_UI_LANG -ErrorAction SilentlyContinue
    }
    else {
        $env:WORKBUDDY_REMOTE_UI_LANG = $previousBridgeUiLang
    }
}

$primaryHost = if ($normalizedListenHost -eq "0.0.0.0") { "127.0.0.1" } else { $ListenHost }
$readyUrl = "http://$primaryHost`:$BridgePort/readyz"
$bridgeReady = $false
for ($i = 0; $i -lt 120; $i++) {
    if ($bridgeProcess.HasExited) {
        $stderr = ""
        if (Test-Path -LiteralPath $bridgeErr) {
            $stderr = (Get-Content -LiteralPath $bridgeErr -ErrorAction SilentlyContinue | Select-Object -First 30) -join [Environment]::NewLine
        }
        throw "Bridge process exited unexpectedly.`r`n$stderr"
    }

    if (Test-BridgeReady -HealthUrl $readyUrl) {
        $bridgeReady = $true
        break
    }

    Start-Sleep -Milliseconds 500
}

if (-not $bridgeReady) {
    throw "Bridge did not become ready in time. Check log: $bridgeErr"
}

$urls = @(Get-BridgeUrls -Port ([int]$BridgePort) -ListenAddress $ListenHost)
$primaryUrl = @($urls)[0]
Write-Host "Bridge ready: $primaryUrl"
foreach ($url in $urls) {
    if ($url -ne $primaryUrl) {
        Write-Host "LAN URL: $url"
    }
}
Write-Host "Log: $bridgeEvents"

# WorkDaddy 与本桥接共用同一个 CDP 端口：只有端口一致，它才能注入面板，
# 并在切换账号后让客户端重载。WorkDaddy 默认按 profile 用 9222，
# 这里用 WBSWITCH_CDP_PORT 把它对齐到本项目的 cdpPort。
if ($StartWorkDaddy) {
    $workDaddyExe = Find-WorkDaddyLauncher -Config $config
    if (-not $workDaddyExe) {
        Write-Host "WorkDaddy launcher not found; skipping."
    }
    else {
        $workDaddyRunning = (Test-PortListening -Port 47832) -or (Test-PortListening -Port 47833)
        $workDaddyCdpPort = Get-WorkDaddyCdpPort
        $workDaddyPortMismatch = $workDaddyCdpPort -gt 0 -and $workDaddyCdpPort -ne [int]$CdpPort

        if ($workDaddyRunning -and -not $workDaddyPortMismatch) {
            Write-Host "WorkDaddy is already running (CDP $workDaddyCdpPort)."
        }
        else {
            if ($workDaddyRunning) {
                Write-Host "WorkDaddy is bound to CDP $workDaddyCdpPort, restarting it on $CdpPort..."
                Stop-WorkDaddyDaemon
                Start-Sleep -Milliseconds 800
            }
            Write-Host "Starting WorkDaddy: $workDaddyExe (CDP $CdpPort)"
            Start-WorkDaddyProcess -ExePath $workDaddyExe -CdpPort ([int]$CdpPort)

            for ($i = 0; $i -lt 30; $i++) {
                if ((Test-PortListening -Port 47832) -or (Test-PortListening -Port 47833)) {
                    Write-Host "WorkDaddy API is ready."
                    break
                }
                Start-Sleep -Milliseconds 500
            }
        }
    }
}

if ($OpenBrowser) {
    Open-BridgeUrlSafely -Url $primaryUrl
}
