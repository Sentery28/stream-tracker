# Собирает StreamTracker.exe из всех .cs в этой папке компилятором из .NET Framework (есть в любой Windows 10/11).
# Использование: powershell -ExecutionPolicy Bypass -File build.ps1 [-OutDir <папка>]
param([string]$OutDir = $PSScriptRoot)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null

# Иконка: фиолетовый скруглённый квадрат с белым треугольником «play». ICO с PNG внутри, несколько размеров.
function New-IconPng([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $r = $s / 4.5; $d = $r * 2; $e = $s - 1
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90); $path.AddArc($e - $d, 0, $d, $d, 270, 90)
    $path.AddArc($e - $d, $e - $d, $d, $d, 0, 90); $path.AddArc(0, $e - $d, $d, $d, 90, 90); $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(145, 70, 255))), $path)
    $pts = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF ($s * 0.38), ($s * 0.27)),
        (New-Object System.Drawing.PointF ($s * 0.38), ($s * 0.73)),
        (New-Object System.Drawing.PointF ($s * 0.75), ($s * 0.50)))
    $g.FillPolygon([System.Drawing.Brushes]::White, $pts)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 256
$pngs = $sizes | ForEach-Object { , (New-IconPng $_) }
$ico = Join-Path $OutDir 'StreamTracker.ico'
$fs = [System.IO.File]::Create($ico)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i] % 256   # 256 записывается как 0
    $w.Write([byte]$sz); $w.Write([byte]$sz); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
$pngs | ForEach-Object { $w.Write($_) }
$w.Close()

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$fw = Split-Path $csc
& $csc /nologo /target:winexe /optimize /codepage:65001 `
    "/out:$(Join-Path $OutDir 'StreamTracker.exe')" "/win32icon:$ico" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    "/r:$fw\System.Web.Extensions.dll" `
    (Get-ChildItem $PSScriptRoot -Filter *.cs | ForEach-Object FullName)
if ($LASTEXITCODE -ne 0) { throw "Сборка не удалась" }
Write-Host "Собрано: $(Join-Path $OutDir 'StreamTracker.exe')"
