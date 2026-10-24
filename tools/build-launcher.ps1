param(
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "launcher\WorkBuddyRemote.csproj"
$publishDir = Join-Path $root "launcher\publish"
$target = Join-Path $root "WorkBuddyRemote.exe"

if (-not (Test-Path -LiteralPath $project)) {
    throw "Project file not found: $project"
}

$running = Get-Process WorkBuddyRemote -ErrorAction SilentlyContinue
if ($running) {
    throw "WorkBuddyRemote.exe is running. Exit it from the tray icon first, then rebuild."
}

$selfContainedValue = if ($SelfContained) { "true" } else { "false" }

Push-Location $root
try {
    dotnet publish $project `
        -c Release `
        -r win-x64 `
        --self-contained $selfContainedValue `
        -p:PublishSingleFile=true `
        -p:DebugType=none `
        -o $publishDir `
        --nologo

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }

    Copy-Item -LiteralPath (Join-Path $publishDir "WorkBuddyRemote.exe") -Destination $target -Force
    Write-Host ""
    Write-Host "Built: $target"
    Write-Host ("Size : {0:N0} KB" -f ((Get-Item -LiteralPath $target).Length / 1KB))
}
finally {
    Pop-Location
}
