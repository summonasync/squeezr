#Requires -Version 5.1
<#
.SYNOPSIS
  Builds and installs Squeezr CLI for the current user.
.DESCRIPTION
  Publishes a self-contained single-file build, copies it to the install
  directory and registers it on the user PATH. Checks for Docker (needed for
  `squeezr services up` - Qdrant and, optionally, Ollama) and prints setup
  hints when missing. The central store %USERPROFILE%\.squeezr (workspaces,
  models, infra) is NOT touched by install or uninstall.
.PARAMETER InstallDir
  Target directory. Default: %LOCALAPPDATA%\Programs\Squeezr
.PARAMETER Rid
  Runtime identifier: win-x64 (default) or win-arm64 for ARM devices.
.PARAMETER SkipBuild
  Use the already published output in artifacts\publish instead of publishing.
.EXAMPLE
  pwsh -File scripts\install.ps1
.EXAMPLE
  pwsh -File scripts\install.ps1 -Rid win-arm64
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\Squeezr'),
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Rid = 'win-x64',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repoRoot 'artifacts\publish'

function Write-Step([string]$msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

if (-not $SkipBuild) {
    Write-Step "Publishing self-contained $Rid single-file build"
    # dotnet publish does not clean -o; leftovers from a previous publish with
    # another RID would leak into the install dir.
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    dotnet publish (Join-Path $repoRoot 'src\Squeezr.Cli\Squeezr.Cli.csproj') `
        -c Release -r $Rid --self-contained true `
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
    Write-Step 'Docker detected - run "squeezr services up" to start Qdrant and Ollama'
} else {
    Write-Host @'
[!] Docker not found. Squeezr works without it (trigram + symbol layers),
    but semantic search needs Qdrant in Docker (start it with: squeezr services up;
    provider "ollama" also uses Docker for the Ollama service).
    Install Docker Desktop: https://docs.docker.com/desktop/setup/install/windows-install/
    or via winget: winget install Docker.DockerDesktop
'@ -ForegroundColor Yellow
}

$storeHome = if ($env:SQUEEZR_HOME) { $env:SQUEEZR_HOME } else { Join-Path $env:USERPROFILE '.squeezr' }
if (-not (Test-Path (Join-Path $InstallDir 'Models')) -and -not (Test-Path (Join-Path $storeHome 'models'))) {
    Write-Host @'
[!] No embedding model found - semantic search will stay disabled until you fetch one.
    Run scripts\fetch-model.ps1 (downloads to ~\.squeezr\models), or bundle a model
    with the CLI via scripts\fetch-model.ps1 -ModelsDir src\Squeezr.Cli\Models and re-run this script.
'@ -ForegroundColor Yellow
}

$summary = @'

Squeezr installed.
  Binary : {0}
  {1}
Verify in a new terminal:  squeezr --help
'@ -f (Join-Path $InstallDir 'squeezr.exe'), $pathNote
Write-Host $summary -ForegroundColor Green
