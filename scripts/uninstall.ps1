#Requires -Version 5.1
<#
.SYNOPSIS
  Removes the Squeezr CLI installation for the current user.
.DESCRIPTION
  Deletes the install directory and removes it from the user PATH.
  Workspaces (.squeezr folders inside your projects) are NOT touched -
  use `squeezr drop` per workspace before uninstalling if you want them gone.
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
Write-Host 'Open a new terminal for the PATH change to take effect.'
