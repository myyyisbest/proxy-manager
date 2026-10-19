$ErrorActionPreference = 'Stop'
$taskWorkspace = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
& dotnet run --project (Join-Path $taskWorkspace 'tests\ProxyManager.Tests\ProxyManager.Tests.csproj') -c Release --no-restore -p:MSBuildEnableWorkloadResolver=false
if ($LASTEXITCODE -ne 0) { throw '隔离诊断失败，请查看测试输出。此脚本不会启动真实 TUN 或结束现有代理。' }
