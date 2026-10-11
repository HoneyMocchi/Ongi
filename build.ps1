# Ongi 빌드 스크립트입니다. Visual Studio(또는 Build Tools)의 Roslyn csc로 .NET Framework 4.8용 단일 exe를 만듭니다.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$csc = $null
if (Test-Path $vswhere) { $csc = & $vswhere -latest -products * -find 'MSBuild\**\Roslyn\csc.exe' | Select-Object -First 1 }
if (-not $csc) { throw 'Roslyn csc.exe를 찾지 못했습니다. Visual Studio 또는 Build Tools가 필요합니다.' }

$ref = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
if (-not (Test-Path (Join-Path $ref 'mscorlib.dll'))) { $ref = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319' }

# 아이콘이 없으면 만듭니다(위젯과 같은 링: 아래가 열린 따뜻한 링 + 틈에 점 세 개).
$icon = Join-Path $root 'assets\Ongi.ico'
if (-not (Test-Path $icon)) {
    Add-Type -AssemblyName System.Drawing
    New-Item -ItemType Directory -Force (Split-Path $icon) | Out-Null
    $sizes = 16, 24, 32, 48, 256
    $pngs = @()
    foreach ($sz in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap $sz, $sz
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = 'AntiAlias'
        $k = $sz / 16.0
        $bg = New-Object System.Drawing.Drawing2D.GraphicsPath
        $r = 3.5 * $k; $d = $r * 2; $e = $sz - 0.5
        $bg.AddArc(0, 0, $d, $d, 180, 90); $bg.AddArc($e - $d, 0, $d, $d, 270, 90)
        $bg.AddArc($e - $d, $e - $d, $d, $d, 0, 90); $bg.AddArc(0, $e - $d, $d, $d, 90, 90); $bg.CloseFigure()
        $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 32, 33, 36))), $bg)
        # 링: 중심 (8,8), 반지름 4.6, 아래 110도가 열린 호. 작은 크기에서도 보이도록 두껍게 그립니다.
        $c = 8 * $k; $rr = 4.6 * $k; $sw = [Math]::Max(1.6, 1.9 * $k)
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(236, 146, 112)), ([single]$sw)
        $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
        $g.DrawArc($pen, [single]($c - $rr), [single]($c - $rr), [single]($rr * 2), [single]($rr * 2), [single]145, [single]250)
        # 틈의 점 세 개(세이지 초록): 24px 이하에서는 뭉개지므로 하나만 찍습니다.
        $dot = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(140, 200, 170))
        $angles = if ($sz -le 24) { @(90) } else { @(72, 90, 108) }
        $dr = [Math]::Max(0.9, 0.54 * $k)
        foreach ($a in $angles) {
            $rad = $a * [Math]::PI / 180
            $dx = $c + $rr * [Math]::Cos($rad); $dy = $c + $rr * [Math]::Sin($rad)
            $g.FillEllipse($dot, [single]($dx - $dr), [single]($dy - $dr), [single]($dr * 2), [single]($dr * 2))
        }
        $g.Dispose()
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        $pngs += , $ms.ToArray()
    }
    $fs = [System.IO.File]::Create($icon)
    $bw = New-Object System.IO.BinaryWriter $fs
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $sz = $sizes[$i]; $data = $pngs[$i]
        $dim = if ($sz -ge 256) { 0 } else { $sz }
        $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$data.Length); $bw.Write([uint32]$offset)
        $offset += $data.Length
    }
    foreach ($data in $pngs) { $bw.Write([byte[]]$data) }
    $bw.Close()
}

$out = Join-Path $root 'bin\Ongi.exe'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

# 실행 중이면 끄고 덮어쓴 뒤, 빌드가 끝나면 다시 켭니다.
$running = @(Get-Process Ongi -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $out })
$running | Stop-Process -Force
if ($running.Count -gt 0) { Start-Sleep -Milliseconds 300 }

$refs = 'mscorlib', 'System', 'System.Core', 'System.Drawing', 'System.Windows.Forms', 'System.Web.Extensions' |
    ForEach-Object { '/r:' + (Join-Path $ref "$_.dll") }
$sources = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }

& $csc /nologo /noconfig /nostdlib+ /target:winexe /platform:anycpu /optimize+ /langversion:7.3 /codepage:65001 /warn:4 `
    "/out:$out" "/win32manifest:$(Join-Path $root 'app.manifest')" "/win32icon:$icon" @refs @sources
if ($LASTEXITCODE -ne 0) { throw "빌드 실패 (exit $LASTEXITCODE)" }
Write-Host "빌드 완료: $out"
if ($running.Count -gt 0) { Start-Process -FilePath $out; Write-Host '실행 중이던 Ongi를 다시 켰습니다.' }
