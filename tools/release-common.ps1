Set-StrictMode -Version Latest

function Get-ProxyManagerWorkspace {
    [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
}

function Get-OwnedProxyProcesses {
    param([Parameter(Mandatory)][string]$ApplicationDirectory)
    $taskDirectory = [System.IO.Path]::GetFullPath($ApplicationDirectory).TrimEnd('\')
    $taskExpected = @(
        [System.IO.Path]::Combine($taskDirectory, 'ProxyManager.exe'),
        [System.IO.Path]::Combine($taskDirectory, 'sing-box.exe')
    )
    foreach ($taskProcess in Get-Process -Name ProxyManager,sing-box -ErrorAction SilentlyContinue) {
        try {
            if ($taskProcess.Path -and $taskExpected -contains $taskProcess.Path) {
                $taskProcess
            }
        } catch { }
    }
}

function Stop-OwnedProxyManager {
    param([Parameter(Mandatory)][string]$ApplicationDirectory)
    $taskDirectory = [System.IO.Path]::GetFullPath($ApplicationDirectory).TrimEnd('\')
    $taskExe = Join-Path $taskDirectory 'ProxyManager.exe'
    if (Test-Path -LiteralPath $taskExe) {
        $taskVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($taskExe)
        if ($taskVersion.FileMajorPart -gt 1 -or
            ($taskVersion.FileMajorPart -eq 1 -and $taskVersion.FileMinorPart -ge 4)) {
            $taskRequest = Start-Process -FilePath $taskExe -ArgumentList '--shutdown' -WindowStyle Hidden -PassThru
            if (-not $taskRequest.WaitForExit(6000)) {
                Stop-Process -Id $taskRequest.Id -ErrorAction SilentlyContinue
            }
        }
    }
    $taskDeadline = [DateTime]::UtcNow.AddSeconds(8)
    do {
        $taskOwned = @(Get-OwnedProxyProcesses -ApplicationDirectory $taskDirectory)
        if ($taskOwned.Count -eq 0) { return }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $taskDeadline)
    foreach ($taskOwnedProcess in Get-OwnedProxyProcesses -ApplicationDirectory $taskDirectory) {
        Stop-Process -Id $taskOwnedProcess.Id -ErrorAction Stop
        $null = $taskOwnedProcess.WaitForExit(5000)
    }
    if (@(Get-OwnedProxyProcesses -ApplicationDirectory $taskDirectory).Count -gt 0) {
        throw "应用目录中仍有进程占用文件：$taskDirectory"
    }
}

function Assert-ProxyOutputDirectory {
    param([Parameter(Mandatory)][string]$Directory)
    $taskWorkspace = Get-ProxyManagerWorkspace
    $taskResolved = [System.IO.Path]::GetFullPath($Directory).TrimEnd('\')
    if (-not $taskResolved.StartsWith($taskWorkspace + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "构建输出目录必须在项目内：$taskResolved"
    }
    $taskResolved
}
