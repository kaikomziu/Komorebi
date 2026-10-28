# アプリアイコン(app.ico)とロゴ(docs/logo.png)を生成する。 実行: pwsh tools/make-icon.ps1
# 木漏れ日（葉の間からこぼれる光）をモチーフにした、緑のグラデーション + 光の粒 + 葉。
Add-Type -AssemblyName System.Drawing

function New-Logo([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    # 角丸の背景（緑のグラデーション）
    $r = [int]($size * 0.22)
    $d = $r * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($size - $d - 1, 0, $d, $d, 270, 90)
    $path.AddArc($size - $d - 1, $size - $d - 1, $d, $d, 0, 90)
    $path.AddArc(0, $size - $d - 1, $d, $d, 90, 90)
    $path.CloseFigure()
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.Point]::new(0, 0)), ([System.Drawing.Point]::new($size, $size)), ([System.Drawing.Color]::FromArgb(255, 22, 52, 40)), ([System.Drawing.Color]::FromArgb(255, 63, 122, 74))
    $g.FillPath($bg, $path)
    $g.SetClip($path)

    # 光の粒（木漏れ日）
    foreach ($s in @(@(0.74, 0.26, 0.34, 235), @(0.30, 0.66, 0.18, 190), @(0.66, 0.78, 0.11, 170), @(0.20, 0.22, 0.09, 150))) {
        $cx = $size * $s[0]; $cy = $size * $s[1]; $rad = $size * $s[2]
        $gp = New-Object System.Drawing.Drawing2D.GraphicsPath
        $gp.AddEllipse($cx - $rad, $cy - $rad, $rad * 2, $rad * 2)
        $pb = New-Object System.Drawing.Drawing2D.PathGradientBrush $gp
        $pb.CenterColor = [System.Drawing.Color]::FromArgb($s[3], 255, 244, 176)
        $pb.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 255, 244, 176))
        $g.FillPath($pb, $gp)
    }

    # 葉
    $leaf = New-Object System.Drawing.Drawing2D.GraphicsPath
    $h = $size * 0.30; $w = $size * 0.17
    $leaf.AddBezier(0, -$h, $w * 1.3, -$h * 0.35, $w * 1.3, $h * 0.45, 0, $h)
    $leaf.AddBezier(0, $h, -$w * 1.3, $h * 0.45, -$w * 1.3, -$h * 0.35, 0, -$h)
    $m = New-Object System.Drawing.Drawing2D.Matrix
    $m.Translate([single]($size * 0.40), [single]($size * 0.58))
    $m.Rotate(-38)
    $leaf.Transform($m)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(238, 232, 250, 222))), $leaf)
    # 葉脈
    $vein = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(200, 40, 92, 58)), ([single]([Math]::Max(1, $size * 0.018)))
    $vp = New-Object System.Drawing.Drawing2D.GraphicsPath
    $vp.AddLine(0, [single](-$h * 0.8), 0, [single]($h * 0.95))
    $vp.Transform($m)
    $g.DrawPath($vein, $vp)

    $g.Dispose()
    return $bmp
}

function Get-PngBytes($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return ,$ms.ToArray()   # カンマで byte[] のまま返す（PowerShell の自動展開を防ぐ）
}

$root = Split-Path -Parent $PSScriptRoot
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) { , (Get-PngBytes (New-Logo $s)) }

# ICO（PNG圧縮の複数サイズ）を書き出す
$ico = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $ico
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $bw.Write([byte]$(if ($s -ge 256) { 0 } else { $s }))
    $bw.Write([byte]$(if ($s -ge 256) { 0 } else { $s }))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write([byte[]]$p) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $root 'app.ico'), $ico.ToArray())

New-Item -ItemType Directory -Force (Join-Path $root 'docs') | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path $root 'docs\logo.png'), (Get-PngBytes (New-Logo 512)))
"app.ico / docs/logo.png を生成しました"
