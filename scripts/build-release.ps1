param(
    [string]$Version = "",
    [ValidateSet("selfcontained", "slim", "both")]
    [string]$Flavor = "both",
    [switch]$SkipInstaller,
    [switch]$SkipPortable
)

# Builds release payload + installer + portable zip. Pure ASCII on purpose:
# Windows PowerShell 5.1 reads BOM-less .ps1 files as ANSI.

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$tfm = "net10.0-windows10.0.19041.0"
$consoleProject = Join-Path $root "apps\console\WorkBuddyRemote.WinUI.csproj"
$buildDir = Join-Path $root "build"
$distDir = Join-Path $root "dist"

if ([string]::IsNullOrWhiteSpace($Version)) {
    $pkg = Get-Content (Join-Path $root "package.json") -Raw | ConvertFrom-Json
    $Version = $pkg.version
}
Write-Host "Version: $Version"

function Ensure-Directory([string]$path) {
    if (-not (Test-Path $path)) {
        New-Item -ItemType Directory -Force -Path $path | Out-Null
    }
}

function Copy-Tree([string]$source, [string]$target) {
    if (-not (Test-Path $source)) { return }
    Ensure-Directory $target
    Copy-Item -Path (Join-Path $source "*") -Destination $target -Recurse -Force
}

function Get-IsccPath {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
    )
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) { return $candidate }
    }
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return ""
}

function Get-NodeExe {
    $candidates = @(
        (Join-Path $root "runtime\node\node.exe"),
        (Join-Path $env:ProgramFiles "nodejs\node.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "nodejs\node.exe")
    )
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) { return $candidate }
    }
    $cmd = Get-Command node.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return ""
}

# 1. Dependencies (production only)
Write-Host "== npm ci --omit=dev =="
npm ci --omit=dev
if ($LASTEXITCODE -ne 0) { throw "npm ci failed" }

# 2. Publish console
$flavors = @()
if ($Flavor -eq "both") { $flavors = @("selfcontained", "slim") } else { $flavors = @($Flavor) }

$nodeExe = Get-NodeExe
if ([string]::IsNullOrWhiteSpace($nodeExe)) { throw "node.exe not found; cannot bundle runtime/node" }
Write-Host "Bundling node from: $nodeExe"

foreach ($current in $flavors) {
    $payload = Join-Path $buildDir $current
    if (Test-Path $payload) { Remove-Item -Recurse -Force $payload }
    Ensure-Directory $payload

    # NOTE: use "dotnet build", NOT "dotnet publish".
    # publish swaps Microsoft.UI.Xaml.dll for the self-contained flavour while keeping a
    # framework-dependent apphost, and drops the app's .pri -> XAML init crashes (0xc000027b).
    # build with -r/-p:SelfContained produces a complete, runnable folder.
    $outDir = Join-Path $root "apps\console\bin\x64\Release\$tfm"
    if ($current -eq "selfcontained") {
        $outDir = Join-Path $outDir "win-x64"
        if (Test-Path $outDir) { Remove-Item -Recurse -Force $outDir }
        Write-Host "== dotnet build (selfcontained) =="
        & dotnet build $consoleProject -c Release -p:Platform=x64 -r win-x64 `
            -p:SelfContained=true -p:WindowsAppSDKSelfContained=true `
            -p:Version=$Version --nologo
    } else {
        if (Test-Path $outDir) { Remove-Item -Recurse -Force $outDir }
        Write-Host "== dotnet build (slim) =="
        & dotnet build $consoleProject -c Release -p:Platform=x64 -p:Version=$Version --nologo
    }
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed for $current" }
    if (-not (Test-Path $outDir)) { throw "build output not found: $outDir" }

    Copy-Item -Path (Join-Path $outDir "*") -Destination $payload -Recurse -Force
    # The framework-dependent build leaves a nested win-x64 folder in bin; do not ship it twice.
    if (Test-Path (Join-Path $payload "win-x64")) {
        Remove-Item -Recurse -Force (Join-Path $payload "win-x64")
    }

    $xbf = @(Get-ChildItem -Path $payload -Filter *.xbf -ErrorAction SilentlyContinue)
    if ($xbf.Count -lt 3) { throw "Payload is missing .xbf files ($($xbf.Count)); refusing to package." }
    if (-not (Test-Path (Join-Path $payload "WorkBuddyRemoteWinUI.pri"))) {
        throw "Payload is missing WorkBuddyRemoteWinUI.pri; refusing to package."
    }
    if (-not (Test-Path (Join-Path $payload "WorkBuddyRemoteWinUI.exe"))) {
        throw "Payload is missing WorkBuddyRemoteWinUI.exe; refusing to package."
    }

    # 3. Bridge + node runtime
    Copy-Tree (Join-Path $root "bridge") (Join-Path $payload "bridge")
    Copy-Tree (Join-Path $root "node_modules") (Join-Path $payload "node_modules")
    Copy-Item (Join-Path $root "package.json") (Join-Path $payload "package.json") -Force
    Copy-Item (Join-Path $root "LICENSE") (Join-Path $payload "LICENSE") -Force

    $runtimeNode = Join-Path $payload "runtime\node"
    Ensure-Directory $runtimeNode
    Copy-Item $nodeExe (Join-Path $runtimeNode "node.exe") -Force

    foreach ($required in @("WorkBuddyRemoteWinUI.exe", "WorkBuddyRemoteWinUI.pri", "bridge\main.mjs", "node_modules\ws\package.json", "runtime\node\node.exe")) {
        $path = Join-Path $payload $required
        if (-not (Test-Path $path)) { throw "Payload missing: $required" }
    }

    $sizeMb = [math]::Round(((Get-ChildItem -Recurse -File $payload | Measure-Object -Sum Length).Sum / 1MB), 1)
    Write-Host "Payload $current ready: $sizeMb MB"
}

# 4. Portable zip (self-contained payload + portable.flag)
if (-not $SkipPortable) {
    $portableSource = Join-Path $buildDir "selfcontained"
    if (-not (Test-Path $portableSource)) { throw "portable zip needs the selfcontained payload" }

    $portableDir = Join-Path $buildDir "portable"
    if (Test-Path $portableDir) { Remove-Item -Recurse -Force $portableDir }
    Copy-Tree $portableSource $portableDir
    Set-Content -Path (Join-Path $portableDir "portable.flag") -Value "portable" -Encoding ASCII

    Ensure-Directory $distDir
    $zipPath = Join-Path $distDir "WorkBuddyRemote-$Version-portable.zip"
    if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
    Compress-Archive -Path (Join-Path $portableDir "*") -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host "Portable zip: $zipPath"
}

# 5. Installer(s)
if (-not $SkipInstaller) {
    $iscc = Get-IsccPath
    if ([string]::IsNullOrWhiteSpace($iscc)) {
        throw "ISCC.exe not found. Install Inno Setup 6 (winget install JRSoftware.InnoSetup) or pass -SkipInstaller."
    }
    Ensure-Directory $distDir
    $iss = Join-Path $root "installer\WorkBuddyRemote.iss"

    foreach ($current in $flavors) {
        $payload = Join-Path $buildDir $current
        $suffix = ""
        $checkRuntime = "0"
        if ($current -eq "slim") {
            $suffix = "slim-"
            $checkRuntime = "1"
        }

        Write-Host "== ISCC ($current) =="
        & $iscc "/DAppVersion=$Version" "/DPayload=$payload" "/DFlavorSuffix=$suffix" "/DCheckRuntime=$checkRuntime" $iss
        if ($LASTEXITCODE -ne 0) { throw "ISCC failed for $current" }
    }
}

# 6. Checksums
if (Test-Path $distDir) {
    $files = Get-ChildItem -Path $distDir -File |
        Where-Object { $_.Name -ne "checksums.txt" } |
        Sort-Object Name
    $lines = @()
    foreach ($file in $files) {
        $hash = (Get-FileHash -Path $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $lines += "$hash  $($file.Name)"
    }
    Set-Content -Path (Join-Path $distDir "checksums.txt") -Value ($lines -join "`n") -Encoding ASCII
    Write-Host "checksums.txt written"
    $lines | ForEach-Object { Write-Host $_ }
}
