$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'release.ps1') @args
