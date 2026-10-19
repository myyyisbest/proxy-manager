$ErrorActionPreference = 'Stop'
$taskWorkspace = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
& dotnet build (Join-Path $taskWorkspace 'ProxyManager.sln') -c Debug -p:MSBuildEnableWorkloadResolver=false
if ($LASTEXITCODE -ne 0) { throw '构建失败，未停止或启动任何应用' }
Write-Output '构建成功。运行前请正常退出旧实例；此脚本不会结束其他代理进程。'
