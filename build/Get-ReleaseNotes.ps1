<#
.SYNOPSIS
    Writes the notes of a GitHub release from the changelog.

.DESCRIPTION
    Takes the section "## [<version>]" of CHANGELOG.md (up to the next section of the same level) and writes it, below a
    line about the unsigned build, to the given file; the release workflow publishes it as the release text, and the central
    server shows it under "What's new" (src/SUSyphos.Server/AgentPackages/ReleaseNotes.cs). Without such a section the
    notes only point to the changelog. Before tagging a release, rename "## [Unreleased]" to "## [<version>] - <date>"
    and start a new empty "## [Unreleased]" above it.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$OutputFile
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$lines = Get-Content (Join-Path $root 'CHANGELOG.md') -Encoding utf8
$heading = "## [$Version]"
$start = [Array]::FindIndex($lines, [Predicate[string]] { param($l) $l.StartsWith($heading, [StringComparison]::Ordinal) })
$notes = @('Unsigned test build: the packages are not code-signed yet.', '')
if ($start -ge 0) {
    $end = [Array]::FindIndex($lines, $start + 1, [Predicate[string]] { param($l) $l.StartsWith('## ', [StringComparison]::Ordinal) })
    if ($end -lt 0) { $end = $lines.Count }
    $notes += $lines[($start + 1)..($end - 1)]
}
else {
    $notes += 'See CHANGELOG.md and docs/admin/test-guide.md.'
}

[IO.File]::WriteAllText($OutputFile, (($notes -join "`n").Trim() + "`n"), (New-Object Text.UTF8Encoding($false)))
Write-Host "Wrote the notes of $Version to $OutputFile"
