# 生成 AnMusic 应用图标：科技蓝渐变圆角方块 + 白色音符
Add-Type -AssemblyName System.Drawing

$size = 256
$bmp = New-Object System.Drawing.Bitmap($size, $size)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias

$g.Clear([System.Drawing.Color]::Transparent)

# 圆角方块路径（留 8px 边距）
$r = 56
$rect = New-Object System.Drawing.Rectangle(8, 8, ($size - 16), ($size - 16))
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$path.AddArc($rect.X, $rect.Y, $r, $r, 180, 90)
$path.AddArc($rect.Right - $r, $rect.Y, $r, $r, 270, 90)
$path.AddArc($rect.Right - $r, $rect.Bottom - $r, $r, $r, 0, 90)
$path.AddArc($rect.X, $rect.Bottom - $r, $r, $r, 90, 90)
$path.CloseFigure()

# 对角渐变：深蓝 #1B5FD0 -> 亮蓝 #4C93F0
$grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.Point(0, $size)), (New-Object System.Drawing.Point($size, 0)),
    [System.Drawing.Color]::FromArgb(255, 27, 95, 208),
    [System.Drawing.Color]::FromArgb(255, 76, 147, 240))
$g.FillPath($grad, $path)

# 白色音符：两个椭圆 + 音柱 + 连接梁（双八分音符）
$white = [System.Drawing.Color]::White
$brush = New-Object System.Drawing.SolidBrush($white)

# 音符头（两个斜椭圆）
$tilt = 20
$m1 = New-Object System.Drawing.Rectangle(64, 158, 52, 40)
$m2 = New-Object System.Drawing.Rectangle(140, 142, 52, 40)
$g.FillEllipse($brush, $m1)
$g.FillEllipse($brush, $m2)
$g.ResetTransform()

# 音柱（两条竖线，从音符头右侧向上）
$pen = New-Object System.Drawing.Pen($white, 12)
$pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Flat
$pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Flat
$g.DrawLine($pen, 110, 178, 110, 70)
$g.DrawLine($pen, 186, 162, 186, 54)

# 顶部连接梁（斜四边形）
$beamPts = New-Object 'System.Collections.Generic.List[System.Drawing.PointF]'
$beamPts.Add((New-Object System.Drawing.PointF(104, 62)))
$beamPts.Add((New-Object System.Drawing.PointF(192, 46)))
$beamPts.Add((New-Object System.Drawing.PointF(192, 68)))
$beamPts.Add((New-Object System.Drawing.PointF(104, 84)))
$g.FillPolygon($brush, $beamPts.ToArray())

$g.Dispose()

# 保存 256x256 PNG
$pngPath = Join-Path $PSScriptRoot "app-icon.png"
$bmp.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)

# 构造 ICO（Vista+ 支持 256x256 PNG 压缩项）：ICONDIR + ICONDIRENTRY + PNG 数据
$pngBytes = [System.IO.File]::ReadAllBytes($pngPath)
$ico = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ico)

# ICONDIR: reserved(2)=0, type(2)=1, count(2)=1
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]1)
# ICONDIRENTRY: width(1)=0(256), height(1)=0(256), colors(1)=0, reserved(1)=0
# planes(2)=1, bitcount(2)=32, bytes(4)=pngLen, offset(4)=22
$bw.Write([Byte]0); $bw.Write([Byte]0); $bw.Write([Byte]0); $bw.Write([Byte]0)
$bw.Write([UInt16]1); $bw.Write([UInt16]32)
$bw.Write([UInt32]$pngBytes.Length); $bw.Write([UInt32]22)
$bw.Write($pngBytes)
$bw.Flush()

$icoPath = Join-Path $PSScriptRoot "app.ico"
[System.IO.File]::WriteAllBytes($icoPath, $ico.ToArray())
$bw.Dispose(); $ico.Dispose(); $bmp.Dispose()

Write-Host "PNG: $pngPath ($((Get-Item $pngPath).Length) bytes)"
Write-Host "ICO: $icoPath ($((Get-Item $icoPath).Length) bytes)"
