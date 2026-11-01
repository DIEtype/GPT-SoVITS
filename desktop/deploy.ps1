param(
    [Parameter(Mandatory = $true)]
    [string]$EnginePath,
    [string]$Destination
)

$ErrorActionPreference = 'Stop'
$taskEngine = (Resolve-Path -LiteralPath $EnginePath).Path
foreach ($taskRelative in @('runtime\python.exe','runtime\ffmpeg.exe','runtime\ffprobe.exe','api_v2.py')) {
    if (-not (Test-Path -LiteralPath (Join-Path $taskEngine $taskRelative))) {
        throw ('Incomplete Windows engine package: missing ' + $taskRelative)
    }
}
if ([string]::IsNullOrWhiteSpace($Destination)) { $Destination = Join-Path $taskEngine 'VoiceStudio' }
$taskDestination = [IO.Path]::GetFullPath($Destination)
$taskDist = Join-Path $PSScriptRoot 'dist'
if (-not (Test-Path -LiteralPath (Join-Path $taskDist '声线工作室.exe'))) {
    throw 'Run desktop/build.ps1 before deploying.'
}
New-Item -ItemType Directory -Path $taskDestination -Force | Out-Null
foreach ($taskName in @('声线工作室.exe','使用说明.txt','README.md','GPT-SoVITS-LICENSE.txt')) {
    Copy-Item -LiteralPath (Join-Path $taskDist $taskName) -Destination (Join-Path $taskDestination $taskName) -Force
}
$taskConfig = Join-Path $taskDestination 'settings.json'
if (-not (Test-Path -LiteralPath $taskConfig)) {
    $taskJson = @{engine=$taskEngine;last_project=''} | ConvertTo-Json
    [IO.File]::WriteAllText($taskConfig, $taskJson, (New-Object System.Text.UTF8Encoding $false))
}
Get-Item -LiteralPath (Join-Path $taskDestination '声线工作室.exe') | Select-Object FullName,Length
