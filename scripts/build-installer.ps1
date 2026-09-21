#Requires -Version 5.1
<#
.SYNOPSIS
  Builds the Squeezr Windows setup executable.
.DESCRIPTION
  Publishes a self-contained single-file build and packages it with Inno Setup 6
  into artifacts\installer\SqueezrSetup-<version>-<rid>.exe. Runs on any Windows
  machine with the .NET 10 SDK and Inno Setup 6 - no CI runner and no Gitea
  release are involved; Windows packaging is deliberately local-only.

  Inno Setup 6 is a build prerequisite. It is looked up on PATH, in the HKCU and
  HKLM uninstall keys and in the default install directories, so a per-user
  installation under %LOCALAPPDATA%\Programs is found as readily as a
  machine-wide one. Pass -InstallInno to install it via winget when missing.

  The produced package is unsigned: SmartScreen warns on first run.
.PARAMETER Rid
  Runtime identifier: win-x64 (default) or win-arm64.
.PARAMETER Version
  Override the version. Default: <Version> from src\Squeezr.Cli\Squeezr.Cli.csproj.
.PARAMETER IsccPath
  Explicit path to ISCC.exe, skipping discovery.
.PARAMETER OutputDir
  Where the package lands. Default: artifacts\installer
.PARAMETER SkipBuild
  Package the existing artifacts\publish tree instead of publishing again.
.PARAMETER InstallInno
  Install Inno Setup 6 via winget when discovery finds nothing.
.EXAMPLE
  pwsh -File scripts\build-installer.ps1
.EXAMPLE
  pwsh -File scripts\build-installer.ps1 -Rid win-arm64
.EXAMPLE
  pwsh -File scripts\build-installer.ps1 -SkipBuild -Version 1.0.3
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Rid = 'win-x64',
    [string]$Version,
    [string]$IsccPath,
    [string]$OutputDir,
    [switch]$SkipBuild,
    [switch]$InstallInno
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repoRoot 'artifacts\publish'
$issFile = Join-Path $repoRoot 'installer\squeezr.iss'
$innoDownloadUrl = 'https://jrsoftware.org/isdl.php'

if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot 'artifacts\installer' }

function Write-Step([string]$msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

function Resolve-ProjectVersion {
    $csproj = Join-Path $repoRoot 'src\Squeezr.Cli\Squeezr.Cli.csproj'
    if (-not (Test-Path $csproj)) { throw "Project file not found: $csproj" }
    # Feature 027 made the csproj the single version source. If <Version> ever
    # moves to a Directory.Build.props, swap this for `dotnet msbuild -getProperty:Version`.
    $match = [regex]::Match((Get-Content $csproj -Raw), '<Version>(.+?)</Version>')
    if (-not $match.Success) {
        throw "No <Version> element in $csproj - pass -Version <value> explicitly."
    }
    return $match.Groups[1].Value.Trim()
}

function Resolve-VersionInfoVersion([string]$value) {
    # ISCC rejects a pre-release suffix in the Win32 version resource: 0.9.0-pre -> 0.9.0.
    $match = [regex]::Match($value, '^\d+(\.\d+){0,3}')
    if (-not $match.Success) {
        throw "Version '$value' has no numeric prefix usable as VersionInfoVersion."
    }
    return $match.Value
}

function Find-Iscc {
    $onPath = Get-Command 'iscc' -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    # Inno is a 32-bit application and installs per-user as readily as
    # machine-wide, so all three uninstall hives are in play.
    $keys = @(
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1'
    )
    foreach ($key in $keys) {
        $location = $null
        try { $location = (Get-ItemProperty -Path $key -ErrorAction Stop).InstallLocation } catch { }
        if (-not $location) { continue }
        $candidate = Join-Path $location 'ISCC.exe'
        if (Test-Path $candidate) { return $candidate }
    }

    $dirs = New-Object System.Collections.Generic.List[string]
    if ($env:LOCALAPPDATA) { $dirs.Add((Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6')) }
    if (${env:ProgramFiles(x86)}) { $dirs.Add((Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6')) }
    if ($env:ProgramFiles) { $dirs.Add((Join-Path $env:ProgramFiles 'Inno Setup 6')) }
    foreach ($dir in $dirs) {
        $candidate = Join-Path $dir 'ISCC.exe'
        if (Test-Path $candidate) { return $candidate }
    }

    return $null
}

function Install-InnoSetup {
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        throw "Inno Setup 6 is missing and winget is not available. Download it from $innoDownloadUrl and re-run."
    }
    Write-Step 'Installing Inno Setup 6 via winget'
    winget install -e --id JRSoftware.InnoSetup --accept-source-agreements --accept-package-agreements
    if ($LASTEXITCODE -ne 0) { throw "winget install JRSoftware.InnoSetup failed with exit code $LASTEXITCODE" }
}

function Resolve-Iscc {
    if ($IsccPath) {
        if (-not (Test-Path $IsccPath)) { throw "-IsccPath does not exist: $IsccPath" }
        return $IsccPath
    }
    $found = Find-Iscc
    if ($found) { return $found }
    if (-not $InstallInno) {
        throw @"
Inno Setup 6 was not found (searched PATH, the HKCU and HKLM uninstall keys, and
the default install directories).

Install it with:
  winget install -e --id JRSoftware.InnoSetup

or re-run this script with -InstallInno, or download it from $innoDownloadUrl
"@
    }
    Install-InnoSetup
    $found = Find-Iscc
    if (-not $found) { throw "Inno Setup 6 is still not found after the winget install - install it manually from $innoDownloadUrl" }
    return $found
}

if (-not $Version) { $Version = Resolve-ProjectVersion }
$versionInfoVersion = Resolve-VersionInfoVersion $Version
$iscc = Resolve-Iscc
Write-Step "Using Inno Setup compiler: $iscc"

if ($SkipBuild) {
    if (-not (Test-Path (Join-Path $publishDir 'squeezr.exe'))) {
        throw "No published binary at $publishDir\squeezr.exe - re-run without -SkipBuild."
    }
    Write-Step "Packaging the existing publish tree at $publishDir"
} else {
    Write-Step "Publishing self-contained $Rid single-file build"
    # dotnet publish does not clean -o; leftovers from a previous publish with
    # another RID would be packaged into the installer.
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    dotnet publish (Join-Path $repoRoot 'src\Squeezr.Cli\Squeezr.Cli.csproj') `
        -c Release -r $Rid --self-contained true `
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none -p:DebugSymbols=false `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$publishDirFull = (Resolve-Path $publishDir).Path
$outputDirFull = (Resolve-Path $OutputDir).Path
$baseName = "SqueezrSetup-$Version-$Rid"

Write-Step "Compiling $baseName.exe"
& $iscc /Qp `
    "/DSourceDir=$publishDirFull" `
    "/DAppVersion=$Version" `
    "/DVersionInfoVersion=$versionInfoVersion" `
    "/DRid=$Rid" `
    "/DOutputDir=$outputDirFull" `
    "/DOutputBaseFilename=$baseName" `
    $issFile
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }

$package = Join-Path $outputDirFull "$baseName.exe"
if (-not (Test-Path $package)) { throw "ISCC reported success but $package is missing." }
$sizeMb = [math]::Round((Get-Item $package).Length / 1MB, 1)

$summary = @'

Installer built.
  Package : {0}
  Size    : {1} MB
  Version : {2}
  RID     : {3}

The package is UNSIGNED - Windows SmartScreen warns on first run
("More info" -> "Run anyway"). Installing needs no administrator rights.
'@ -f $package, $sizeMb, $Version, $Rid
Write-Host $summary -ForegroundColor Green
