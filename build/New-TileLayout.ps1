<#
.SYNOPSIS
    Writes the CSS that spreads dashboard tiles over equally full rows.

.DESCRIPTION
    A tile needs at least 13rem plus a 1rem gap. For every number of tiles in a row (2 to 8) and every number of columns
    the page has room for, the rows are made as equally full as possible: 7 tiles become 7, 4 + 3, 3 + 2 + 2 and then two
    columns (docs/06-dashboard.md, section 8 "Tiles"). Uneven rows use a grid of the least common multiple of the row
    sizes, in which each tile spans its share. The rules sit between the markers in src/SUSyphos.Server/wwwroot/app.css;
    run this script after changing the tile width or gap there, and commit the result.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$css = Join-Path $root 'src\SUSyphos.Server\wwwroot\app.css'
$start = '/* tile-layout:start */'
$end = '/* tile-layout:end */'

function Get-Gcd([int]$a, [int]$b) { while ($b -ne 0) { $a, $b = $b, ($a % $b) }; $a }
function Get-Lcm([int[]]$values) { $l = 1; foreach ($v in $values) { $l = $l * $v / (Get-Gcd $l $v) }; [int]$l }

$rules = [ordered]@{}
foreach ($n in 2..8) {
    $previous = ''
    foreach ($k in 2..$n) {
        $rows = [math]::Ceiling($n / $k)
        $base = [math]::Floor($n / $rows)
        $extra = $n % $rows
        $sizes = @(0..($rows - 1) | ForEach-Object { if ($_ -lt $extra) { $base + 1 } else { $base } })
        $sel = ".tiles:has(> :nth-child($n):last-child)"
        $lines = [System.Collections.Generic.List[string]]::new()
        if ($k -eq 2 -or @($sizes | Select-Object -Unique).Count -eq 1) {
            $lines.Add("$sel { grid-template-columns: repeat($($sizes[0]), minmax(0, 1fr)); }")
            $lines.Add("$sel > * { grid-column: auto; }")
        }
        else {
            $l = Get-Lcm $sizes
            $lines.Add("$sel { grid-template-columns: repeat($l, minmax(0, 1fr)); }")
            $spans = [ordered]@{}
            $i = 1
            foreach ($size in $sizes) { foreach ($x in 1..$size) { $spans[[string]$i] = $l / $size; $i++ } }
            foreach ($group in $spans.GetEnumerator() | Group-Object Value | Sort-Object { [int]$_.Name }) {
                $lines.Add((($group.Group | ForEach-Object { "$sel > :nth-child($($_.Key))" }) -join ', ') + " { grid-column: span $($group.Name); }")
            }
        }

        $key = $lines -join "`n"
        if ($key -eq $previous) { continue }
        $previous = $key
        if (-not $rules.Contains("$k")) { $rules["$k"] = [System.Collections.Generic.List[string]]::new() }
        $rules["$k"].AddRange($lines)
    }
}

$block = [System.Text.StringBuilder]::new()
[void]$block.AppendLine($start)
[void]$block.AppendLine('/* Balanced tile rows, written by build/New-TileLayout.ps1 (docs/06-dashboard.md, section 8 "Tiles"). Do not edit. */')
[void]$block.AppendLine('main { container: page / inline-size; }')
foreach ($k in $rules.Keys) {
    [void]$block.AppendLine("@container page (min-width: $(14 * [int]$k - 1)rem) {")
    foreach ($line in $rules[$k]) { [void]$block.AppendLine("    $line") }
    [void]$block.AppendLine('}')
}
[void]$block.Append($end)

$text = [IO.File]::ReadAllText($css)
$from = $text.IndexOf($start, [StringComparison]::Ordinal)
$to = $text.IndexOf($end, [StringComparison]::Ordinal)
if ($from -lt 0 -or $to -lt 0) { throw "The markers $start and $end are missing in $css." }
$text = $text.Substring(0, $from) + $block.ToString() + $text.Substring($to + $end.Length)
[IO.File]::WriteAllText($css, $text.Replace("`r`n", "`n"), (New-Object Text.UTF8Encoding($false)))
Write-Host "Wrote the tile layout to $css"
