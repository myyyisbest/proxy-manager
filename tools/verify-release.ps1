#Requires -Version 7.0
#Requires -RunAsAdministrator
[CmdletBinding()]
param([string]$Version = '1.4.0')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')
$taskWorkspace = Get-ProxyManagerWorkspace
$taskRelease = Join-Path $taskWorkspace 'release'
$taskRoot = Assert-ProxyOutputDirectory (Join-Path $taskWorkspace ('artifacts\package-verification-' + [guid]::NewGuid().ToString('N')))
New-Item -ItemType Directory -Path $taskRoot | Out-Null
$taskZip = Join-Path $taskRelease "ProxyManager-Portable-$Version-win-x64.zip"
$taskSetup = Join-Path $taskRelease "ProxyManager-Setup-$Version-win-x64.exe"
$taskReport = [ordered]@{ version=$Version; checkedUtc=[DateTime]::UtcNow.ToString('o'); checks=@() }

function Assert-Package([bool]$Value,[string]$Message) { if (-not $Value) { throw $Message } }
function Invoke-PackageProcess([string]$File,[string[]]$Arguments,[int]$Timeout=45000) {
    $taskInfo = [System.Diagnostics.ProcessStartInfo]::new($File)
    $taskInfo.UseShellExecute = $false
    $taskInfo.CreateNoWindow = $true
    foreach ($taskArgument in $Arguments) { $taskInfo.ArgumentList.Add($taskArgument) }
    $taskProcess = [System.Diagnostics.Process]::Start($taskInfo)
    if (-not $taskProcess.WaitForExit($Timeout)) {
        # Only this freshly created test process is terminated; no image-name operation.
        $taskProcess.Kill()
        throw "测试命令超时：$File"
    }
    Assert-Package ($taskProcess.ExitCode -eq 0) "测试命令退出码异常：$File ($($taskProcess.ExitCode))"
    $taskProcess.Dispose()
}
function Test-Manifest([string]$Directory) {
    $taskManifest = Get-Content -LiteralPath (Join-Path $Directory 'manifest.json') -Raw | ConvertFrom-Json
    Assert-Package ($taskManifest.version -eq $Version) '包内版本不匹配'
    foreach ($taskEntry in $taskManifest.files) {
        $taskPath = [System.IO.Path]::GetFullPath((Join-Path $Directory $taskEntry.path))
        Assert-Package ($taskPath.StartsWith($Directory.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) '清单存在越界路径'
        Assert-Package ((Get-FileHash -LiteralPath $taskPath).Hash -eq $taskEntry.sha256) "包内文件哈希不匹配：$($taskEntry.path)"
    }
}
function Test-PackagedApp([string]$Directory,[bool]$Portable) {
    $taskExe = Join-Path $Directory 'ProxyManager.exe'
    $taskData = if ($Portable) { Join-Path $Directory 'Data' } else { Join-Path $taskRoot 'installed-app-data' }
    New-Item -ItemType Directory -Path $taskData | Out-Null
    [ordered]@{ SchemaVersion=2; UseFakeIpDns=$false; Proxy=@{Type=0;Host='127.0.0.1';Port=7890};Rules=@();Theme='Light';StartEngineOnLaunch=$false;AutoStartWithWindows=$false;MinimizeToTray=$false } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskData 'config.json') -Encoding utf8
    $taskArguments = [System.Collections.Generic.List[string]]::new()
    if (-not $Portable) { foreach($taskArg in @('--isolated','--data-dir',$taskData)){ $taskArguments.Add($taskArg) } }
    $taskArguments.Add('--no-auto-start')
    $taskStart = [System.Diagnostics.ProcessStartInfo]::new($taskExe)
    $taskStart.UseShellExecute=$false
    foreach($taskArg in $taskArguments){$taskStart.ArgumentList.Add($taskArg)}
    $taskApp=[System.Diagnostics.Process]::Start($taskStart)
    try {
        $taskDeadline=[DateTime]::UtcNow.AddSeconds(15)
        $taskStarted=$false
        while([DateTime]::UtcNow -lt $taskDeadline -and -not $taskApp.HasExited) {
            $taskLogs = @(Get-ChildItem -LiteralPath (Join-Path $taskData 'logs') -Filter '*.log' -ErrorAction SilentlyContinue)
            $taskText = ($taskLogs | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw -ErrorAction SilentlyContinue }) -join "`n"
            if($taskText.Contains('应用代理管理器启动')){ $taskStarted=$true; break }
            Start-Sleep -Milliseconds 150
        }
        Assert-Package $taskStarted '交付程序未正常启动或未使用预期数据目录'
        Start-Sleep -Milliseconds 800
        Assert-Package (-not $taskApp.HasExited) '交付程序启动后异常退出'
        $taskShutdown = [System.Collections.Generic.List[string]]::new()
        if(-not $Portable){foreach($taskArg in @('--isolated','--data-dir',$taskData)){$taskShutdown.Add($taskArg)}}
        $taskShutdown.Add('--shutdown')
        Invoke-PackageProcess $taskExe $taskShutdown.ToArray() 15000
        Assert-Package ($taskApp.WaitForExit(15000)) '交付程序未响应正常退出请求'
        Assert-Package ($taskApp.ExitCode -eq 0) '交付程序退出码异常'
        $taskText = (Get-ChildItem -LiteralPath (Join-Path $taskData 'logs') -Filter '*.log' | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
        Assert-Package ($taskText -notmatch '致命异常|未处理异常|未观察任务异常') '交付程序出现异常日志'
        Assert-Package ($taskText -notmatch '分流引擎已启动') '包验收不应开启真实分流'
    } finally {
        if(-not $taskApp.HasExited){$taskApp.Kill()}
        $taskApp.Dispose()
    }
}

$taskArchive=[System.IO.Compression.ZipFile]::OpenRead($taskZip)
try {
    Assert-Package (-not @($taskArchive.Entries | Where-Object FullName -match '/Data/|/logs/|/config\.json$').Count) '便携包含运行数据'
} finally {$taskArchive.Dispose()}
Expand-Archive -LiteralPath $taskZip -DestinationPath (Join-Path $taskRoot 'portable')
$taskPortable=Join-Path $taskRoot "portable\ProxyManager-Portable-$Version-win-x64"
Assert-Package (Test-Path -LiteralPath (Join-Path $taskPortable 'portable.marker')) '便携标记缺失'
Test-Manifest $taskPortable
Test-PackagedApp $taskPortable $true
$taskReport.checks += 'portable archive manifest, clean data, actual startup and graceful exit'

$taskInstall=Join-Path $taskRoot 'installed'
$taskOriginalKey='HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{7A1C52E4-9B3D-4F8E-A6C1-2D5E8B4F9A13}_is1'
$taskBefore = Get-ItemProperty -LiteralPath $taskOriginalKey -ErrorAction SilentlyContinue | Select-Object InstallLocation,DisplayVersion,UninstallString | ConvertTo-Json -Compress
$taskInstalled=$false
try {
    Invoke-PackageProcess $taskSetup @('/VERIFY=1','/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS','/TASKS=',('/DIR='+$taskInstall),('/LOG='+ (Join-Path $taskRoot 'install.log'))) 60000
    $taskInstalled=$true
    Assert-Package (-not (Test-Path -LiteralPath (Join-Path $taskInstall 'portable.marker'))) '安装版错误包含便携标记'
    Test-Manifest $taskInstall
    Test-PackagedApp $taskInstall $false
    $taskReport.checks += 'actual setup install under separate verification identity, manifest and startup'
    Invoke-PackageProcess (Join-Path $taskInstall 'unins000.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG='+ (Join-Path $taskRoot 'uninstall.log'))) 60000
    $taskInstalled=$false
    Assert-Package (-not (Test-Path -LiteralPath (Join-Path $taskInstall 'ProxyManager.exe'))) '卸载未移除应用文件'
    Assert-Package (Test-Path -LiteralPath (Join-Path $taskRoot 'installed-app-data\config.json')) '卸载意外删除用户数据'
    $taskAfter = Get-ItemProperty -LiteralPath $taskOriginalKey -ErrorAction SilentlyContinue | Select-Object InstallLocation,DisplayVersion,UninstallString | ConvertTo-Json -Compress
    Assert-Package ($taskBefore -eq $taskAfter) '正式安装注册信息被验证安装修改'
    $taskReport.checks += 'actual uninstall removes installed files, preserves data and original registration'
} finally {
    if($taskInstalled -and (Test-Path -LiteralPath (Join-Path $taskInstall 'unins000.exe'))) {
        Invoke-PackageProcess (Join-Path $taskInstall 'unins000.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') 60000
    }
}
$taskReport.packages = @(Get-FileHash -LiteralPath $taskZip,$taskSetup | Select-Object Path,Hash)
$taskReport | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskRelease 'package-verification.json') -Encoding utf8
Write-Output ('PACKAGE VERIFICATION PASSED: '+(Join-Path $taskRelease 'package-verification.json'))
