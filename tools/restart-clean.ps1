[CmdletBinding()]
param([Parameter(Mandatory)][string]$ApplicationDirectory)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')
Stop-OwnedProxyManager -ApplicationDirectory $ApplicationDirectory
Write-Output '指定目录的应用已停止。请手动重新启动。'
