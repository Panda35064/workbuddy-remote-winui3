param(
    [int]$Port = 8799,
    [string]$ProjectRoot = ""
)

# Smoke test: start the bridge, hit /healthz and /workdaddy/status, stop it.
# Works without WorkBuddy installed (WORKBUDDY_ALLOW_MISSING_ASAR=1).

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = Split-Path -Parent $PSScriptRoot
}
Set-Location $ProjectRoot

$env:WORKBUDDY_ALLOW_MISSING_ASAR = "1"

# Windows PowerShell 5.1 throws "duplicate key" in Start-Process when both
# HTTPS_PROXY and https_proxy exist (common with Clash/V2Ray). Drop the lowercase copies.
foreach ($name in @("http_proxy", "https_proxy", "no_proxy", "all_proxy")) {
    if (Test-Path "Env:$name") {
        Remove-Item "Env:$name" -ErrorAction SilentlyContinue
    }
}

$out = Join-Path $env:TEMP "wr-smoke-$Port.out.log"
$err = Join-Path $env:TEMP "wr-smoke-$Port.err.log"

$proc = Start-Process -FilePath "node" `
    -ArgumentList @("bridge/main.mjs", "--port", "$Port") `
    -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
if (-not $proc) { throw "failed to start node" }

function Wait-Http([string]$url, [int]$seconds) {
    for ($i = 0; $i -lt ($seconds * 2); $i++) {
        Start-Sleep -Milliseconds 500
        if ($proc.HasExited) { return $null }
        try {
            $res = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 3
            return $res
        } catch {
        }
    }
    return $null
}

$failed = $false
try {
    $health = Wait-Http "http://127.0.0.1:$Port/healthz" 20
    if ($null -eq $health -or $health.StatusCode -ne 200) {
        Write-Host "FAIL /healthz"
        if (Test-Path $out) { Get-Content $out -Tail 30 }
        if (Test-Path $err) { Get-Content $err -Tail 30 }
        $failed = $true
    } else {
        Write-Host "OK   /healthz  $($health.StatusCode)"
        try {
            $wd = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/workdaddy/status" -UseBasicParsing -TimeoutSec 5
            Write-Host "OK   /workdaddy/status $($wd.StatusCode) $($wd.Content)"
        } catch {
            Write-Host "WARN /workdaddy/status unavailable: $($_.Exception.Message)"
        }
    }
} finally {
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
}

if ($failed) { exit 1 }
Write-Host "smoke passed"
