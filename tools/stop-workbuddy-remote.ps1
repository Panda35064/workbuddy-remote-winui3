param(
    [int]$BridgePort = 8780,
    [string]$ProjectRoot,
    [switch]$Quiet
)

$ErrorActionPreference = "Stop"

if (-not $ProjectRoot -or [string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
}

function Write-StopLog {
    param([string]$Message)
    if (-not $Quiet) {
        Write-Host $Message
    }
}

$stoppedIds = @()

# Pass 1: find node.exe processes running this project's src\main.mjs.
try {
    $targets = Get-CimInstance Win32_Process -Filter "Name='node.exe'" -ErrorAction Stop |
        Where-Object {
            $_.CommandLine -and
            $_.CommandLine -like "*main.mjs*" -and
            $_.CommandLine -like "*$ProjectRoot*"
        }
}
catch {
    $targets = @()
}

foreach ($target in @($targets)) {
    try {
        Stop-Process -Id $target.ProcessId -Force -ErrorAction Stop
        $stoppedIds += [int]$target.ProcessId
    }
    catch {
        Write-StopLog ("Failed to stop PID " + $target.ProcessId + ": " + $_.Exception.Message)
    }
}

if ($stoppedIds.Count -gt 0) {
    Start-Sleep -Milliseconds 500
}

# Pass 2: if the port is still held by node.exe, stop that owner as well.
# This mirrors Stop-PortProcess in start-workbuddy-remote.ps1.
if ($BridgePort -gt 0) {
    $listeners = @(Get-NetTCPConnection -LocalPort $BridgePort -State Listen -ErrorAction SilentlyContinue)
    foreach ($listener in $listeners) {
        try {
            $owner = Get-Process -Id $listener.OwningProcess -ErrorAction Stop
            if ($owner.ProcessName -ieq "node") {
                Stop-Process -Id $owner.Id -Force -ErrorAction Stop
                if ($stoppedIds -notcontains [int]$owner.Id) {
                    $stoppedIds += [int]$owner.Id
                }
            }
            else {
                Write-StopLog ("Port " + $BridgePort + " is held by " + $owner.ProcessName + " (PID " + $owner.Id + "), left untouched.")
            }
        }
        catch {
        }
    }
}

if ($stoppedIds.Count -eq 0) {
    Write-StopLog "No running bridge process was found."
}
else {
    Write-StopLog ("Stopped bridge process(es): " + (($stoppedIds | Sort-Object -Unique) -join ", "))
}
