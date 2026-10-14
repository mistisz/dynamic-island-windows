# Builds DynamicIsland.exe with the C# compiler that ships inside Windows (.NET Framework 4.8). No SDK needed.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$src = Join-Path $PSScriptRoot 'src'
$out = Join-Path $PSScriptRoot 'DynamicIsland.exe'
# The in-box csc.exe fails on very long working/temp paths, so compile in a short one.
$work = Join-Path $env:LOCALAPPDATA 'Temp\dibuild'
New-Item -ItemType Directory -Force $work | Out-Null
# Byte copy instead of Copy-Item: it also works when the source folder is EFS-encrypted.
Get-ChildItem $src -File | ForEach-Object { [IO.File]::WriteAllBytes((Join-Path $work $_.Name), [IO.File]::ReadAllBytes($_.FullName)) }

function New-Icon($path) {
    $ms = New-Object IO.MemoryStream; $bw = New-Object IO.BinaryWriter($ms)
    $sizes = 16, 24, 32, 48, 256
    $pngs = foreach ($s in $sizes) {
        $bmp = New-Object Drawing.Bitmap $s, $s
        $g = [Drawing.Graphics]::FromImage($bmp); $g.SmoothingMode = 'AntiAlias'
        $f = $s / 256.0
        function RR($x, $y, $w, $h, $r) {
            $p = New-Object Drawing.Drawing2D.GraphicsPath
            $p.AddArc($x, $y, 2*$r, 2*$r, 180, 90); $p.AddArc($x+$w-2*$r, $y, 2*$r, 2*$r, 270, 90)
            $p.AddArc($x+$w-2*$r, $y+$h-2*$r, 2*$r, 2*$r, 0, 90); $p.AddArc($x, $y+$h-2*$r, 2*$r, 2*$r, 90, 90)
            $p.CloseFigure(); $p
        }
        $bg = RR 0 0 ($s-1) ($s-1) (56*$f)
        $grad = New-Object Drawing.Drawing2D.LinearGradientBrush((New-Object Drawing.Point 0,0), (New-Object Drawing.Point $s,$s), [Drawing.Color]::FromArgb(255,90,120,255), [Drawing.Color]::FromArgb(255,190,90,240))
        $g.FillPath($grad, $bg)
        $pill = RR (36*$f) (100*$f) (184*$f) (56*$f) (28*$f)
        $g.FillPath([Drawing.Brushes]::Black, $pill)
        $g.DrawPath((New-Object Drawing.Pen ([Drawing.Color]::FromArgb(150,255,255,255)), ([Math]::Max(1, 4*$f))), $pill)
        $g.FillEllipse((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255,48,209,88))), 174*$f, 118*$f, 20*$f, 20*$f)
        $pm = New-Object IO.MemoryStream; $bmp.Save($pm, [Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose()
        , $pm.ToArray()
    }
    $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $d = $sizes[$i] % 256
        $bw.Write([byte]$d); $bw.Write([byte]$d); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]$pngs[$i].Length); $bw.Write([UInt32]$offset)
        $offset += $pngs[$i].Length
    }
    foreach ($p in $pngs) { $bw.Write($p) }
    [IO.File]::WriteAllBytes($path, $ms.ToArray())
}
if (-not (Test-Path "$work\app.ico")) { New-Icon "$work\app.ico" }

$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$gac = "$env:WINDIR\Microsoft.NET\assembly\GAC_MSIL"
$wpfCore = Get-ChildItem "$env:WINDIR\Microsoft.NET\assembly\GAC_64\PresentationCore" -Recurse -Filter PresentationCore.dll | Select-Object -First 1
$winmd = "$env:WINDIR\System32\WinMetadata"
$refs = @(
    "$gac\PresentationFramework\v4.0_4.0.0.0__31bf3856ad364e35\PresentationFramework.dll",
    $wpfCore.FullName,
    "$gac\WindowsBase\v4.0_4.0.0.0__31bf3856ad364e35\WindowsBase.dll",
    "$gac\System.Xaml\v4.0_4.0.0.0__b77a5c561934e089\System.Xaml.dll",
    # System.Windows.Forms / System.Drawing are already referenced by csc.rsp
    "$gac\System.Runtime\v4.0_4.0.0.0__b03f5f7f11d50a3a\System.Runtime.dll",
    "$gac\System.Numerics.Vectors\v4.0_4.0.0.0__b03f5f7f11d50a3a\System.Numerics.Vectors.dll",
    "$gac\System.Numerics\v4.0_4.0.0.0__b77a5c561934e089\System.Numerics.dll",
    "$gac\System.Runtime.WindowsRuntime\v4.0_4.0.0.0__b77a5c561934e089\System.Runtime.WindowsRuntime.dll",
    "$gac\System.Runtime.InteropServices.WindowsRuntime\v4.0_4.0.0.0__b03f5f7f11d50a3a\System.Runtime.InteropServices.WindowsRuntime.dll",
    "$winmd\Windows.Foundation.winmd", "$winmd\Windows.Media.winmd", "$winmd\Windows.UI.winmd",
    "$winmd\Windows.Storage.winmd", "$winmd\Windows.ApplicationModel.winmd"
) | ForEach-Object { "-r:$_" }

$oldTmp = $env:TMP; $env:TMP = $env:TEMP = $work
Push-Location $work
try {
    & "$fw\csc.exe" -nologo -target:winexe -platform:x64 -optimize+ -out:DynamicIsland.exe -win32icon:app.ico `
        -resource:Island.xaml,Island.xaml -resource:Settings.xaml,Settings.xaml @refs Program.cs Services.cs Island.cs Backdrop.cs
    if ($LASTEXITCODE -ne 0) { throw "compile failed" }
} finally { Pop-Location; $env:TMP = $env:TEMP = $oldTmp }

[IO.File]::WriteAllBytes($out, [IO.File]::ReadAllBytes("$work\DynamicIsland.exe"))
$p = Start-Process $out -ArgumentList '--selftest' -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "self-test failed (see %APPDATA%\DynamicIsland\error.log)" }
Write-Host "Built $out ($([Math]::Round((Get-Item $out).Length / 1KB)) KB), self-test passed"
