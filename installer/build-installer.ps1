<#
.SYNOPSIS
    Builds the Storage Manager installer (dist\StorageManager-Setup-<version>-x64.exe).

.DESCRIPTION
    1. Publishes the app self-contained for win-x64 (users don't need .NET installed).
    2. Compiles installer\StorageManager.iss with Inno Setup (ISCC.exe).

.PARAMETER Version
    Version to stamp on the installer. Defaults to <Version> in StorageManager.csproj.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
    powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 -Version 1.1.0
#>
param(
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$root       = Split-Path -Parent $PSScriptRoot
$project    = Join-Path $root 'src\StorageManager\StorageManager.csproj'
$publishDir = Join-Path $root 'artifacts\publish'
$iss        = Join-Path $PSScriptRoot 'StorageManager.iss'

if (-not $Version) {
    $Version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $Version) { $Version = '1.0.0' }
}
Write-Host "Building Storage Manager $Version" -ForegroundColor Cyan

# ── 1. Find Inno Setup compiler ───────────────────────────────────────────────
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { $iscc = (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source }
if (-not $iscc) {
    throw "Inno Setup 6 not found. Install it with:  winget install JRSoftware.InnoSetup"
}

# ── 2. Publish self-contained app ─────────────────────────────────────────────
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -p:Version=$Version -p:DebugType=none -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# ── 3. Compile installer ──────────────────────────────────────────────────────
& $iscc "/DAppVersion=$Version" "/DPublishDir=$publishDir" $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

$setup = Join-Path $root "dist\StorageManager-Setup-$Version-x64.exe"
$sizeMb = [math]::Round((Get-Item $setup).Length / 1MB, 1)
Write-Host "`nInstaller ready: $setup ($sizeMb MB)" -ForegroundColor Green
