# Gera Assets\cais.ico (16 a 256 px) desenhando o ícone do Cais com System.Drawing.
# Uso: pwsh tools\gerar-icone.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0

    # fundo: quadrado arredondado em degradê azul
    $tile = New-RoundedPath (8 * $s) (8 * $s) (240 * $s) (240 * $s) (56 * $s)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $size, $size), ([System.Drawing.Color]::FromArgb(255, 0x7A, 0xD4, 0xFF)), ([System.Drawing.Color]::FromArgb(255, 0x00, 0x5F, 0xC4))
    $g.FillPath($grad, $tile)

    # o "dock": barra translúcida com três apps em cima
    $bar = New-RoundedPath (40 * $s) (150 * $s) (176 * $s) (62 * $s) (22 * $s)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(90, 255, 255, 255))), $bar)
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    # três apps alinhados pela base; o do meio "ampliado", como no Mac
    $x = 49
    foreach ($h in 40, 54, 40) {
        $app = New-RoundedPath ($x * $s) ((202 - $h) * $s) ($h * $s) ($h * $s) (11 * $s)
        $g.FillPath($white, $app)
        $x += $h + 12
    }
    # pontinho de app aberto
    $g.FillEllipse($white, 124 * $s, 220 * $s, 8 * $s, 8 * $s)

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($sz in $sizes) { , (New-IconPng $sz) }

$out = Join-Path $PSScriptRoot '..\Assets\cais.ico'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $data = $images[$i]
    $dim = if ($sz -ge 256) { 0 } else { $sz }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$data.Length); $bw.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($data in $images) { $bw.Write($data) }
$bw.Close()

# Também salva um PNG grande para o README
$png = Join-Path $PSScriptRoot '..\docs\icone.png'
New-Item -ItemType Directory -Force (Split-Path $png) | Out-Null
[System.IO.File]::WriteAllBytes($png, (New-IconPng 256))
Copy-Item $png (Join-Path $PSScriptRoot '..\Assets\cais.png') -Force
Write-Host "Ícone gerado: $out"

