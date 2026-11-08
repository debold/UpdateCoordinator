<#
.SYNOPSIS
    Takes the screenshots of the dashboard help and of the README anew.

.DESCRIPTION
    Runs the browser tests in tests/SUSyphos.UiTests that photograph dialogs and drop-downs of the demo lab (HelpScreenshotTests)
    and writes the images to src/SUSyphos.Server/wwwroot/help-images, from the clean showcase lab (ShowcaseData). Run it
    after changing a dialog the help shows, then commit the images. With -Readme it also writes the README images to
    docs/images (ReadmeScreenshotTests); the README shows the released version, so take them at a release, not while
    unreleased changes are on main. Needs Microsoft Edge, like the other browser tests; nothing on the machine is changed.

.PARAMETER Readme
    Also takes the README screenshots (docs/images).
#>
[CmdletBinding()]
param([switch] $Readme)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
# The pages show the version of the latest release instead of a development build (0.0.0-alpha...).
$version = (& git -C $root describe --tags --match 'release/v*' --abbrev=0) -replace '^release/v', ''
if (-not $version) { throw 'No release tag found.' }
$env:SUSYPHOS_HELP_SCREENSHOTS = 'update'
$env:SUSYPHOS_README_SCREENSHOTS = if ($Readme) { 'update' } else { 'keep' }
try {
    & dotnet test --project (Join-Path $root 'tests\SUSyphos.UiTests') "-p:MinVerVersionOverride=$version" --filter-class '*HelpScreenshotTests' --filter-class '*ReadmeScreenshotTests'
    if ($LASTEXITCODE -ne 0) { throw 'Taking the help screenshots failed.' }
}
finally {
    Remove-Item Env:\SUSYPHOS_HELP_SCREENSHOTS, Env:\SUSYPHOS_README_SCREENSHOTS
}
Get-ChildItem (Join-Path $root 'src\SUSyphos.Server\wwwroot\help-images'), (Join-Path $root 'docs\images') -Filter '*.png' | Format-Table Name, Length
