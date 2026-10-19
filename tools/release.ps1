[CmdletBinding()]
param(
    [string]$InnoCompiler = (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')
$taskWorkspace = Get-ProxyManagerWorkspace
$taskProject = Join-Path $taskWorkspace 'src\ProxyManager\ProxyManager.csproj'
[xml]$taskProjectXml = Get-Content -LiteralPath $taskProject -Raw
$taskVersion = [string]$taskProjectXml.Project.PropertyGroup.Version
if ($taskVersion -notmatch '^\d+\.\d+\.\d+$') { throw '无效发布版本号' }
$taskArtifacts = Assert-ProxyOutputDirectory (Join-Path $taskWorkspace 'artifacts')
$taskRelease = Assert-ProxyOutputDirectory (Join-Path $taskWorkspace 'release')
$taskPublish = Assert-ProxyOutputDirectory (Join-Path $taskArtifacts "publish-$taskVersion")
$taskPayload = Assert-ProxyOutputDirectory (Join-Path $taskArtifacts 'payload')
$taskPortableName = "ProxyManager-Portable-$taskVersion-win-x64"
$taskPortable = Assert-ProxyOutputDirectory (Join-Path $taskArtifacts $taskPortableName)
foreach ($taskDirectory in @($taskArtifacts,$taskRelease,$taskPublish)) {
    New-Item -ItemType Directory -Path $taskDirectory -Force | Out-Null
}
# The path above is validated inside the workspace. Do not copy stale outputs
# from an earlier publish into the payload.
if (Test-Path -LiteralPath $taskPublish) { Remove-Item -LiteralPath $taskPublish -Recurse -Force }
New-Item -ItemType Directory -Path $taskPublish | Out-Null
if (-not (Test-Path -LiteralPath $InnoCompiler)) { throw "找不到 Inno Setup 编译器：$InnoCompiler" }
Push-Location $taskWorkspace
try {
    & (Join-Path $PSScriptRoot 'prepare-engine.ps1')
    if (-not $SkipTests) {
        & dotnet restore tests/ProxyManager.Tests/ProxyManager.Tests.csproj -p:MSBuildEnableWorkloadResolver=false --locked-mode
        if ($LASTEXITCODE -ne 0) { throw '测试依赖还原失败' }
        & dotnet run --project tests/ProxyManager.Tests/ProxyManager.Tests.csproj -c Release --no-restore -p:MSBuildEnableWorkloadResolver=false
        if ($LASTEXITCODE -ne 0) { throw '验证失败，终止发布' }
    }
    & dotnet restore $taskProject -r win-x64 -p:SelfContained=true -p:PublishSingleFile=true -p:MSBuildEnableWorkloadResolver=false -p:NuGetLockFilePath=packages.win-x64.lock.json --locked-mode
    if ($LASTEXITCODE -ne 0) { throw '发布依赖还原失败' }
    & dotnet publish $taskProject -c Release -r win-x64 --self-contained true --no-restore `
        -p:MSBuildEnableWorkloadResolver=false -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None -p:DebugSymbols=false -o $taskPublish
    if ($LASTEXITCODE -ne 0) { throw 'publish 失败，未生成交付包' }
    if (-not (Test-Path -LiteralPath (Join-Path $taskPublish 'ProxyManager.exe')) -or
        -not (Test-Path -LiteralPath (Join-Path $taskPublish 'sing-box.exe'))) { throw '发布文件不完整' }
    foreach ($taskDirectory in @($taskPayload,$taskPortable)) {
        if (Test-Path -LiteralPath $taskDirectory) { Remove-Item -LiteralPath $taskDirectory -Recurse -Force }
        New-Item -ItemType Directory -Path $taskDirectory | Out-Null
    }
    Copy-Item -Path (Join-Path $taskPublish '*') -Destination $taskPayload -Recurse
    Copy-Item -LiteralPath (Join-Path $taskWorkspace '使用说明.md') -Destination $taskPayload
    Copy-Item -LiteralPath (Join-Path $taskWorkspace 'THIRD-PARTY-NOTICES.md') -Destination $taskPayload
    Copy-Item -LiteralPath (Join-Path $taskWorkspace 'licenses') -Destination $taskPayload -Recurse
    $taskSource = Join-Path $taskPayload 'sources'
    New-Item -ItemType Directory -Path $taskSource | Out-Null
    Copy-Item -LiteralPath (Join-Path $taskWorkspace '.build\sing-box-1.14.3-source.zip') -Destination $taskSource
    $taskProjectSource = Join-Path $taskSource "ProxyManager-$taskVersion-source.zip"
    $taskArchive = [System.IO.Compression.ZipFile]::Open($taskProjectSource, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $taskSourceFiles = @(
            Get-ChildItem -LiteralPath (Join-Path $taskWorkspace 'src'),(Join-Path $taskWorkspace 'tests'),(Join-Path $taskWorkspace 'tools'),(Join-Path $taskWorkspace 'licenses') -File -Recurse |
                Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.Extension -ne '.exe' }
            Get-Item -LiteralPath (Join-Path $taskWorkspace 'README.md'),(Join-Path $taskWorkspace '使用说明.md'),(Join-Path $taskWorkspace 'THIRD-PARTY-NOTICES.md'),(Join-Path $taskWorkspace 'ProxyManager.sln')
        )
        foreach ($taskFile in $taskSourceFiles) {
            $taskEntryName = [System.IO.Path]::GetRelativePath($taskWorkspace,$taskFile.FullName).Replace('\','/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($taskArchive,$taskFile.FullName,$taskEntryName) | Out-Null
        }
    } finally { $taskArchive.Dispose() }
    $taskManifest = [ordered]@{
        product = 'ProxyManager'; version = $taskVersion; architecture = 'win-x64'
        builtUtc = [DateTime]::UtcNow.ToString('o')
        runtime = 'self-contained .NET 8'; engine = 'sing-box 1.14.3'
        engineSource = 'https://github.com/SagerNet/sing-box/tree/v1.14.3'
        files = @(Get-ChildItem -LiteralPath $taskPayload -File -Recurse | ForEach-Object {
            [ordered]@{ path = [System.IO.Path]::GetRelativePath($taskPayload,$_.FullName); sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash }
        })
    }
    $taskManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskPayload 'manifest.json') -Encoding utf8
    Copy-Item -Path (Join-Path $taskPayload '*') -Destination $taskPortable -Recurse
    'ProxyManager portable mode' | Set-Content -LiteralPath (Join-Path $taskPortable 'portable.marker') -Encoding ascii
    $taskZip = Join-Path $taskRelease ($taskPortableName + '.zip')
    Compress-Archive -LiteralPath $taskPortable -DestinationPath $taskZip -CompressionLevel Optimal -Force
    & $InnoCompiler "/DMyAppVersion=$taskVersion" "/DPayloadDir=$taskPayload" "/DReleaseDir=$taskRelease" (Join-Path $PSScriptRoot 'installer.iss')
    if ($LASTEXITCODE -ne 0) { throw '安装器编译失败' }
    $taskSetup = Join-Path $taskRelease "ProxyManager-Setup-$taskVersion-win-x64.exe"
    Get-FileHash -LiteralPath $taskZip,$taskSetup | ForEach-Object { "$($_.Hash)  $([System.IO.Path]::GetFileName($_.Path))" } |
        Set-Content -LiteralPath (Join-Path $taskRelease 'SHA256SUMS.txt') -Encoding ascii
    Write-Output "发布完成：$taskZip"
    Write-Output "发布完成：$taskSetup"
} finally { Pop-Location }
