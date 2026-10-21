<#
.SYNOPSIS
    Renders the SUSyphos icons from the SVG masters in assets/logo.

.DESCRIPTION
    Draws the masters in headless Microsoft Edge (sizes 16 and 24 from susyphos-icon-16.svg, the others from
    susyphos-icon.svg, antialiased, transparent outside the rounded tile) and writes
      - src/SUSyphos.Server/wwwroot/favicon.ico (16, 32, 48 px) and the SVGs the dashboard serves,
      - assets/logo/susyphos.ico (16, 24, 32, 48, 64, 128, 256 px; 256 stored as PNG, the others as bitmaps)
        for the Start menu shortcut and the Programs and Features entry of both installers.
    Run it after changing a master and commit the results; builds and CI use the committed files.
#>
[CmdletBinding()]
param(
    [string]$Edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$logo = Join-Path $root 'assets\logo'
$wwwroot = Join-Path $root 'src\SUSyphos.Server\wwwroot'
$work = Join-Path ([IO.Path]::GetTempPath()) "SUSyphos-icons-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $work | Out-Null

try {
    $big = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $logo 'susyphos-icon.svg')))
    $small = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $logo 'susyphos-icon-16.svg')))
    $page = @"
<!doctype html><html><body><pre id="out"></pre><script>
const load = (src) => new Promise((resolve) => { const img = new Image(); img.onload = () => resolve(img); img.src = src; });
(async () => {
  const big = await load('data:image/svg+xml;base64,$big'), small = await load('data:image/svg+xml;base64,$small');
  const lines = [];
  for (const size of [16, 24, 32, 48, 64, 128, 256]) {
    const canvas = document.createElement('canvas'); canvas.width = size; canvas.height = size;
    const ctx = canvas.getContext('2d'); ctx.imageSmoothingQuality = 'high';
    ctx.drawImage(size <= 24 ? small : big, 0, 0, size, size);
    lines.push(size + ' ' + canvas.toDataURL('image/png').split(',')[1]);
  }
  document.getElementById('out').textContent = lines.join('\n');
})();
</script></body></html>
"@
    $html = Join-Path $work 'render.html'
    [IO.File]::WriteAllText($html, $page)
    # Edge logs harmless diagnostics to stderr, which Windows PowerShell would turn into terminating errors.
    $ErrorActionPreference = 'Continue'
    $dom = & $Edge --headless=new --disable-gpu --virtual-time-budget=3000 --dump-dom "file:///$($html.Replace('\', '/'))" 2>$null | Out-String
    $ErrorActionPreference = 'Stop'
    $pngs = @{}
    foreach ($match in [regex]::Matches($dom, '(?m)(?:^|>)(\d+) ([A-Za-z0-9+/=]{20,})')) {
        $pngs[[int]$match.Groups[1].Value] = [Convert]::FromBase64String($match.Groups[2].Value)
    }
    if ($pngs.Count -ne 7) { throw "Edge rendered $($pngs.Count) of 7 sizes." }

    function ConvertTo-Dib([byte[]]$png) {
        $stream = New-Object IO.MemoryStream(, $png)
        $bitmap = [Drawing.Bitmap]::FromStream($stream)
        try {
            $size = $bitmap.Width
            $out = New-Object IO.MemoryStream
            $writer = New-Object IO.BinaryWriter($out)
            # BITMAPINFOHEADER: the height counts the colour image and the AND mask.
            $writer.Write([int]40); $writer.Write([int]$size); $writer.Write([int]($size * 2))
            $writer.Write([int16]1); $writer.Write([int16]32); $writer.Write([int]0)
            $writer.Write([int]0); $writer.Write([int]0); $writer.Write([int]0); $writer.Write([int]0); $writer.Write([int]0)
            for ($y = $size - 1; $y -ge 0; $y--) {
                for ($x = 0; $x -lt $size; $x++) {
                    $c = $bitmap.GetPixel($x, $y)
                    $writer.Write([byte]$c.B); $writer.Write([byte]$c.G); $writer.Write([byte]$c.R); $writer.Write([byte]$c.A)
                }
            }
            # AND mask: all zero (the alpha channel decides), rows padded to 32 bits.
            $rowBytes = [int]([math]::Ceiling($size / 32) * 4)
            $writer.Write((New-Object byte[] ($rowBytes * $size)))
            $writer.Flush()
            return , $out.ToArray()
        }
        finally {
            $bitmap.Dispose()
            $stream.Dispose()
        }
    }

    function Write-Ico([string]$path, [int[]]$sizes) {
        $images = foreach ($size in $sizes) {
            if ($size -ge 256) { , $pngs[$size] } else { , (ConvertTo-Dib $pngs[$size]) }
        }
        $out = New-Object IO.MemoryStream
        $writer = New-Object IO.BinaryWriter($out)
        $writer.Write([int16]0); $writer.Write([int16]1); $writer.Write([int16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $size = $sizes[$i]
            $dimension = if ($size -ge 256) { 0 } else { $size }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([int16]1); $writer.Write([int16]32); $writer.Write([int]$images[$i].Length); $writer.Write([int]$offset)
            $offset += $images[$i].Length
        }
        foreach ($image in $images) { $writer.Write($image) }
        $writer.Flush()
        [IO.File]::WriteAllBytes($path, $out.ToArray())
        Write-Host "Wrote $path ($($sizes -join ', ') px)"
    }

    Write-Ico (Join-Path $wwwroot 'favicon.ico') @(16, 32, 48)
    Write-Ico (Join-Path $logo 'susyphos.ico') @(16, 24, 32, 48, 64, 128, 256)
    Copy-Item (Join-Path $logo 'susyphos-icon.svg') (Join-Path $wwwroot 'susyphos-icon.svg') -Force
    Copy-Item (Join-Path $logo 'susyphos-mark-white.svg') (Join-Path $wwwroot 'susyphos-mark-white.svg') -Force
}
finally {
    Remove-Item -Recurse -Force $work
}
