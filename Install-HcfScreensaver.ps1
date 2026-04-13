<#
.SYNOPSIS
    Builds, publishes and installs the HALT AND CATCH FIRE screensaver as a
    system-wide .scr file in %WINDIR%\System32.

.DESCRIPTION
    The script:
      1. Requires PowerShell 5.1+ or PowerShell 7+ on Windows 11.
      2. Self-elevates to Administrator if not already elevated.
      3. Publishes a self-contained Release build via 'dotnet publish'.
      4. Copies and renames the output EXE to HcfScreensaver.scr.
      5. Copies the .scr file to %WINDIR%\System32\ (requires elevation).
      6. Optionally opens the Screen Saver Settings dialog.

.NOTES
    Run from the workspace root:
        .\Install-HcfScreensaver.ps1

    To uninstall:
        Remove-Item "$env:WINDIR\System32\HcfScreensaver.scr" -Force
#>

#Requires -Version 5.1

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── Configuration ─────────────────────────────────────────────────────────────

$ProjectPath   = Join-Path $PSScriptRoot 'HcfScreensaver\HcfScreensaver.csproj'
$PublishDir    = Join-Path $PSScriptRoot 'HcfScreensaver\bin\Release\publish'
$PublishedExe  = Join-Path $PublishDir   'HcfScreensaver.exe'
$ScrLocal      = Join-Path $PSScriptRoot 'HcfScreensaver.scr'
$ScrSystem     = Join-Path $env:WINDIR   'System32\HcfScreensaver.scr'
$DotnetRuntime = 'win-x64'

# ── Self-elevation ────────────────────────────────────────────────────────────

function Test-Administrator {
    $current = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    $current.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
}

if (-not (Test-Administrator)) {
    Write-Host "Requesting administrator elevation..." -ForegroundColor Yellow
    $pwsh    = if ($PSVersionTable.PSEdition -eq 'Core') { 'pwsh' } else { 'powershell' }
    $argList = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    $proc    = Start-Process $pwsh -ArgumentList $argList -Verb RunAs -Wait -PassThru
    if ($proc.ExitCode -ne 0) {
        Write-Error "Elevated process exited with code $($proc.ExitCode)."
    }
    exit $proc.ExitCode
}

# ── Helpers ───────────────────────────────────────────────────────────────────

function Write-Step([string]$msg) {
    Write-Host ""
    Write-Host "  $msg" -ForegroundColor Green
}

function Assert-DotNet {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Write-Error ".NET SDK not found. Install from https://dot.net and re-run."
    }
    $version = (dotnet --version 2>&1).Trim()
    Write-Host "  Using .NET SDK $version" -ForegroundColor DarkGray
}

# ── Main ──────────────────────────────────────────────────────────────────────

Write-Host ""
Write-Host " =============================================" -ForegroundColor DarkGreen
Write-Host "   HALT AND CATCH FIRE — Screensaver Installer " -ForegroundColor Green
Write-Host "   CARDIFF GIANT COMPUTING  //  1983           " -ForegroundColor DarkGreen
Write-Host " =============================================" -ForegroundColor DarkGreen

# 1. Prerequisites
Write-Step "Step 1/4 — Checking prerequisites"
Assert-DotNet

if (-not (Test-Path $ProjectPath)) {
    Write-Error "Project file not found: $ProjectPath`nRun this script from the HCF_screensaver root."
}

# 2. Publish
Write-Step "Step 2/4 — Publishing single-file self-contained (Release, $DotnetRuntime)"
& dotnet publish $ProjectPath `
    --configuration Release `
    --runtime $DotnetRuntime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    --output $PublishDir `
    --verbosity minimal

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path $PublishedExe)) {
    Write-Error "Expected output not found: $PublishedExe"
}
Write-Host "  Published to: $PublishDir" -ForegroundColor DarkGray

# 3. Create .scr
Write-Step "Step 3/4 — Creating HcfScreensaver.scr"
Copy-Item -Path $PublishedExe -Destination $ScrLocal -Force
Write-Host "  Created: $ScrLocal" -ForegroundColor DarkGray

# 4. Install to System32
Write-Step "Step 4/4 — Installing to $ScrSystem"
Copy-Item -Path $ScrLocal -Destination $ScrSystem -Force
Write-Host "  Installed: $ScrSystem" -ForegroundColor Green

# ── Done ──────────────────────────────────────────────────────────────────────

Write-Host ""
Write-Host " =============================================" -ForegroundColor DarkGreen
Write-Host "  Installation complete!                      " -ForegroundColor Green
Write-Host " =============================================" -ForegroundColor DarkGreen
Write-Host ""
Write-Host "  Next steps:" -ForegroundColor White
Write-Host "    1. Open Settings > Personalization > Lock screen > Screen saver" -ForegroundColor DarkGray
Write-Host "    2. Select 'HcfScreensaver' from the dropdown" -ForegroundColor DarkGray
Write-Host "    3. Click Settings... to configure or Preview to test" -ForegroundColor DarkGray
Write-Host ""

$openSettings = Read-Host "  Open Screen Saver Settings now? [Y/n]"
if ($openSettings -notmatch '^[Nn]') {
    Start-Process 'control.exe' -ArgumentList 'desk.cpl,,1'
}
