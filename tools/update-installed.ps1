[CmdletBinding()]
param([Parameter(Mandatory)][string]$InstallDirectory)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')
$taskWorkspace = Get-ProxyManagerWorkspace
$taskInstall = [System.IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
if (-not (Test-Path -LiteralPath (Join-Path $taskInstall 'ProxyManager.exe'))) { throw '指定目录不是已有的应用安装目录' }
& (Join-Path $PSScriptRoot 'release.ps1')
if (-not $?) { throw '发布验证失败，未更新已安装应用' }
Stop-OwnedProxyManager -ApplicationDirectory $taskInstall
$taskPayload = Join-Path $taskWorkspace 'artifacts\payload'
Copy-Item -Path (Join-Path $taskPayload '*') -Destination $taskInstall -Recurse -Force
foreach ($taskFile in Get-ChildItem -LiteralPath $taskPayload -File -Recurse) {
    $taskTarget = Join-Path $taskInstall ([System.IO.Path]::GetRelativePath($taskPayload,$taskFile.FullName))
    if ((Get-FileHash -LiteralPath $taskFile.FullName).Hash -ne (Get-FileHash -LiteralPath $taskTarget).Hash) {
        throw "更新文件校验失败：$taskTarget"
    }
}
Write-Output "更新校验通过：$taskInstall。请手动启动应用。"
