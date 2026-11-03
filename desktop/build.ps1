param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist')
)

$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$taskDist = [IO.Path]::GetFullPath($OutputDirectory)
$taskFramework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$taskCompiler = Join-Path $taskFramework 'csc.exe'
if (-not (Test-Path -LiteralPath $taskCompiler)) {
    throw 'Build requires Windows x64 and the .NET Framework C# compiler.'
}
New-Item -ItemType Directory -Path $taskDist -Force | Out-Null

Add-Type -AssemblyName System.Drawing
$taskIconBitmap = New-Object System.Drawing.Bitmap 64,64
$taskGraphics = [System.Drawing.Graphics]::FromImage($taskIconBitmap)
$taskGraphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$taskGraphics.Clear([System.Drawing.ColorTranslator]::FromHtml('#2964DE'))
$taskPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White),5
$taskPen.StartCap = $taskPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
@(@(14,28,14,38),@(26,17,26,49),@(38,23,38,43),@(50,11,50,55)) | ForEach-Object {
    $taskGraphics.DrawLine($taskPen, $_[0], $_[1], $_[2], $_[3])
}
$taskIconPath = Join-Path $taskDist 'studio.ico'
$taskIconStream = [IO.File]::Create($taskIconPath)
[System.Drawing.Icon]::FromHandle($taskIconBitmap.GetHicon()).Save($taskIconStream)
$taskIconStream.Dispose()
$taskPen.Dispose()
$taskGraphics.Dispose()
$taskIconBitmap.Dispose()

$taskReferences = @(
    (Join-Path $taskFramework 'System.Core.dll'),
    (Join-Path $taskFramework 'System.Web.Extensions.dll'),
    (Join-Path $taskFramework 'System.Windows.Forms.dll'),
    (Join-Path $taskFramework 'System.IO.Compression.dll'),
    (Join-Path $taskFramework 'System.IO.Compression.FileSystem.dll'),
    (Join-Path $taskFramework 'System.Xaml.dll'),
    (Join-Path $taskFramework 'WPF\WindowsBase.dll'),
    (Join-Path $taskFramework 'WPF\PresentationCore.dll'),
    (Join-Path $taskFramework 'WPF\PresentationFramework.dll')
)
$taskArgs = @(
    '/nologo','/target:winexe','/platform:x64','/optimize+','/codepage:65001',
    ('/win32icon:' + $taskIconPath),
    ('/out:' + (Join-Path $taskDist '声线工作室.exe')),
    ('/resource:' + (Join-Path $taskRoot 'MainWindow.xaml') + ',MainWindow.xaml'),
    ('/resource:' + (Join-Path $taskRoot 'worker.py') + ',worker.py')
)
$taskArgs += $taskReferences | ForEach-Object { '/reference:' + $_ }
$taskArgs += Join-Path $taskRoot 'VoiceStudio.cs'
$taskArgs += Join-Path $taskRoot 'VoicePackage.cs'
$taskArgs += Join-Path $taskRoot 'ProjectFiles.cs'
$taskArgs += Join-Path $taskRoot 'ProjectManagerWindow.cs'
$taskArgs += Join-Path $taskRoot 'ProjectFilesTests.cs'
& $taskCompiler @taskArgs
if ($LASTEXITCODE -ne 0) { throw 'C# build failed.' }

Copy-Item -LiteralPath (Join-Path $taskRoot '使用说明.txt') -Destination $taskDist -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'README.md') -Destination $taskDist -Force
Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $taskRoot) 'LICENSE') -Destination (Join-Path $taskDist 'GPT-SoVITS-LICENSE.txt') -Force
Get-Item -LiteralPath (Join-Path $taskDist '声线工作室.exe') | Select-Object FullName,Length
