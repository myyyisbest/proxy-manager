$ErrorActionPreference = 'Stop'
$taskWorkspace = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskLock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'engine.lock.json') -Raw | ConvertFrom-Json
$taskBuild = Join-Path $taskWorkspace '.build'
New-Item -ItemType Directory -Path $taskBuild -Force | Out-Null
$taskArchive = Join-Path $taskBuild 'sing-box-official.zip'
$taskSource = Join-Path $taskBuild "sing-box-$($taskLock.version)-source.zip"
if (-not (Test-Path -LiteralPath $taskArchive)) { Invoke-WebRequest -Uri $taskLock.archiveUrl -OutFile $taskArchive -TimeoutSec 180 }
if ((Get-FileHash -LiteralPath $taskArchive).Hash -ne $taskLock.archiveSha256) { throw '内核官方归档校验失败' }
if (-not (Test-Path -LiteralPath $taskSource)) { Invoke-WebRequest -Uri $taskLock.sourceUrl -OutFile $taskSource -TimeoutSec 180 }
if ((Get-FileHash -LiteralPath $taskSource).Hash -ne $taskLock.sourceSha256) { throw '内核源码归档校验失败' }
$taskEngine = Join-Path $PSScriptRoot 'sing-box.exe'
if (-not (Test-Path -LiteralPath $taskEngine)) {
    $taskExtract = Join-Path $taskBuild ('engine-' + [guid]::NewGuid().ToString('N'))
    Expand-Archive -LiteralPath $taskArchive -DestinationPath $taskExtract
    $taskFound = @(Get-ChildItem -LiteralPath $taskExtract -Recurse -Filter 'sing-box.exe')
    if ($taskFound.Count -ne 1) { throw '官方归档内核文件数量异常' }
    Copy-Item -LiteralPath $taskFound[0].FullName -Destination $taskEngine
}
if ((Get-FileHash -LiteralPath $taskEngine).Hash -ne $taskLock.exeSha256) { throw '内核可执行文件校验失败' }
Write-Output "官方内核和源码校验通过：$($taskLock.version)"
