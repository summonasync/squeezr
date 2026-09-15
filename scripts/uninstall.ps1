#Requires -Version 5.1
<#
.SYNOPSIS
  Removes the Squeezr CLI installation for the current user.
.DESCRIPTION
  Deletes the install directory and removes it from the user PATH.
  The central store %USERPROFILE%\.squeezr is NOT touched: it holds your
  workspaces (workspaces\<storeKey>\), embedding models (models\) and the
  managed infra compose file (infra\). Data survives the uninstall. If you
  really want it gone: run 'squeezr drop' per workspace and
  'squeezr services down' first, then remove %USERPROFILE%\.squeezr manually.
.PARAMETER InstallDir
  Directory to remove. Default: %LOCALAPPDATA%\Programs\Squeezr
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\Squeezr')
)

$ErrorActionPreference = 'Stop'

$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$pathParts = @($userPath -split ';' | Where-Object { $_ -ne '' -and $_ -ne $InstallDir })
[Environment]::SetEnvironmentVariable('Path', $pathParts -join ';', 'User')

if (Test-Path $InstallDir) {
    Remove-Item $InstallDir -Recurse -Force
    Write-Host "Removed $InstallDir and its PATH entry." -ForegroundColor Green
} else {
    Write-Host "PATH entry removed; $InstallDir did not exist." -ForegroundColor Yellow
}
$storeDir = Join-Path $env:USERPROFILE '.squeezr'
if (Test-Path $storeDir) {
    Write-Host "Note: the central store $storeDir (workspaces, models, infra) was left untouched." -ForegroundColor Yellow
    Write-Host "      Remove it manually only if you no longer need the data." -ForegroundColor Yellow
}
Write-Host 'Open a new terminal for the PATH change to take effect.'
