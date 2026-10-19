[CmdletBinding()]
param([Parameter(Mandatory)][string]$ApplicationDirectory)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')
Stop-OwnedProxyManager -ApplicationDirectory $ApplicationDirectory
Write-Output '已停止指定目录实例，请手动启动该目录中的 ProxyManager.exe。'
