#Requires -Version 5.1
<#
.SYNOPSIS
  Builds and installs Squeezr CLI for the current user.
.DESCRIPTION
  Publishes a self-contained single-file win-x64 build, copies it to the install
  directory and registers it on the user PATH. Checks for Docker (needed from
  milestone M4 for Qdrant) and prints setup hints when missing.
.PARAMETER InstallDir
  Target directory. Default: %LOCALAPPDATA%\Programs\Squeezr
.PARAMETER SkipBuild
  Use the already published output in artifacts\publish instead of publishing.
.EXAMPLE
  pwsh -File scripts\install.ps1
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\Squeezr'),
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repoRoot 'artifacts\publish'

function Write-Step([string]$msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

if (-not $SkipBuild) {
    Write-Step 'Publishing self-contained win-x64 single-file build'
    dotnet publish (Join-Path $repoRoot 'src\Squeezr.Cli\Squeezr.Cli.csproj') `
        -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none -p:DebugSymbols=false `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
}

$exe = Join-Path $publishDir 'squeezr.exe'
if (-not (Test-Path $exe)) { throw "Published binary not found: $exe (run without -SkipBuild first)" }

Write-Step "Installing to $InstallDir"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
# MSBuildWorkspace ships out-of-proc BuildHost folders next to the exe; they are required for M3 semantic indexing.
Copy-Item (Join-Path $publishDir '*') $InstallDir -Recurse -Force

$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$pathParts = @($userPath -split ';' | Where-Object { $_ -ne '' })
if ($pathParts -notcontains $InstallDir) {
    Write-Step 'Adding install dir to user PATH'
    [Environment]::SetEnvironmentVariable('Path', ($pathParts + $InstallDir) -join ';', 'User')
    $pathNote = 'PATH updated - open a NEW terminal for squeezr to resolve.'
} else {
    $pathNote = 'Install dir already on PATH.'
}

if (Get-Command docker -ErrorAction SilentlyContinue) {
    Write-Step 'Docker detected - Qdrant (from M4) will be available'
} else {
    Write-Host @'
[!] Docker not found. Squeezr works without it (trigram + symbol layers),
    but semantic search (milestone M4+) needs Qdrant in Docker.
    Install Docker Desktop: https://docs.docker.com/desktop/setup/install/windows-install/
    or via winget: winget install Docker.DockerDesktop
'@ -ForegroundColor Yellow
}

Write-Host @'

Squeezr installed.
  Binary : {0}
  {1}
Verify in a new terminal:  squeezr --help
'@ -f (Join-Path $InstallDir 'squeezr.exe'), $pathNote -ForegroundColor Green
