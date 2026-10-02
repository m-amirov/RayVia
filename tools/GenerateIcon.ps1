param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

Add-Type -AssemblyName System.Drawing

$size = 256
$bitmap = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::FromArgb(10, 20, 39))

$blue = [System.Drawing.Color]::FromArgb(30, 136, 255)
$cyan = [System.Drawing.Color]::FromArgb(54, 224, 235)

$mainPen = New-Object System.Drawing.Pen($cyan, 28)
$mainPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$mainPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$mainPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

$innerPen = New-Object System.Drawing.Pen($blue, 18)
$innerPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$innerPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

$routePen = New-Object System.Drawing.Pen($blue, 10)
$routePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$routePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

$graphics.DrawBezier($mainPen, 78, 62, 176, 62, 205, 66, 205, 122)
$graphics.DrawBezier($mainPen, 205, 122, 205, 161, 173, 169, 142, 169)
$graphics.DrawLine($mainPen, 142, 169, 205, 224)

$graphics.DrawLine($innerPen, 55, 112, 145, 112)
$graphics.DrawBezier($innerPen, 145, 112, 177, 112, 181, 132, 181, 144)
$graphics.DrawBezier($innerPen, 181, 144, 181, 164, 162, 169, 142, 169)

$graphics.DrawLine($routePen, 38, 112, 76, 112)
$graphics.DrawLine($routePen, 38, 153, 112, 153)
$graphics.DrawLine($routePen, 48, 194, 88, 194)

$nodeBrush = New-Object System.Drawing.SolidBrush($cyan)
$graphics.FillEllipse($nodeBrush, 28, 102, 20, 20)
$graphics.FillEllipse($nodeBrush, 101, 142, 22, 22)
$graphics.FillEllipse($nodeBrush, 78, 184, 20, 20)

$directory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Force -Path $directory | Out-Null

$pngStream = New-Object System.IO.MemoryStream
$bitmap.Save($pngStream, [System.Drawing.Imaging.ImageFormat]::Png)
$pngBytes = $pngStream.ToArray()

$file = [System.IO.File]::Open($OutputPath, [System.IO.FileMode]::Create)
$writer = New-Object System.IO.BinaryWriter($file)
$writer.Write([UInt16]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]1)
$writer.Write([Byte]0)
$writer.Write([Byte]0)
$writer.Write([Byte]0)
$writer.Write([Byte]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]32)
$writer.Write([UInt32]$pngBytes.Length)
$writer.Write([UInt32]22)
$writer.Write($pngBytes)
$writer.Flush()
$writer.Dispose()
$file.Dispose()

$pngStream.Dispose()
$nodeBrush.Dispose()
$routePen.Dispose()
$innerPen.Dispose()
$mainPen.Dispose()
$graphics.Dispose()
$bitmap.Dispose()
