<#
.SYNOPSIS
    Takes the screenshots of the dashboard help anew.

.DESCRIPTION
    Runs the browser tests in tests/SUSyphos.UiTests that photograph dialogs and drop-downs of the demo lab (HelpScreenshotTests)
    and writes the images to src/SUSyphos.Server/wwwroot/help-images. Run it after changing a dialog the help shows, then commit
    the images. Needs Microsoft Edge, like the other browser tests; nothing on the machine is changed.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$env:SUSYPHOS_HELP_SCREENSHOTS = 'update'
try {
    & dotnet test --project (Join-Path $root 'tests\SUSyphos.UiTests') --filter-class '*HelpScreenshotTests'
    if ($LASTEXITCODE -ne 0) { throw 'Taking the help screenshots failed.' }
}
finally {
    Remove-Item Env:\SUSYPHOS_HELP_SCREENSHOTS
}
Get-ChildItem (Join-Path $root 'src\SUSyphos.Server\wwwroot\help-images') -Filter '*.png' | Format-Table Name, Length
