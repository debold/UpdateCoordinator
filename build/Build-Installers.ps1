<#
.SYNOPSIS
    Builds the MSI packages of agent and central server.

.DESCRIPTION
    Publishes both components self-contained for win-x64 and builds the WiX packages from the publish output.
    The product version is taken from the git tag (agent/vX.Y.Z, server/vX.Y.Z) through MinVer; -AgentVersion and
    -ServerVersion override it, e.g. to build two versions for an upgrade test. Output: artifacts\installers.
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $AgentVersion,
    [string] $ServerVersion,
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$output = if ($OutputDirectory) { $OutputDirectory } else { Join-Path $artifacts "installers" }
# Absolute, because the WiX build resolves relative output paths against the installer project.
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($output)
New-Item -ItemType Directory -Force -Path $output | Out-Null

function Build-Component([string] $Name, [string] $Project, [string] $Installer, [string] $Version, [string[]] $InstallerProperties = @()) {
    $publish = Join-Path $artifacts "publish\$Name"
    if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }

    $publishArgs = @('publish', $Project, '-c', $Configuration, '-r', 'win-x64', '--self-contained', '-o', $publish)
    if ($Version) { $publishArgs += "-p:MinVerVersionOverride=$Version" }
    & dotnet @publishArgs | Write-Host
    if ($LASTEXITCODE -ne 0) { throw "Publishing $Name failed." }

    # MSI versions are numeric: major.minor.patch of the published file version.
    $exe = Get-ChildItem $publish -Filter 'SUSyphos.*.exe' | Select-Object -First 1
    $fileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe.FullName).FileVersion
    $productVersion = ($fileVersion.Split('.')[0..2]) -join '.'
    # Full semantic version without build metadata, e.g. 0.4.0-rc.1: the version the agent reports and packages are named after.
    $semanticVersion = ([System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe.FullName).ProductVersion -split '\+')[0]

    # The WiX build hard-links its output to the intermediate file, which the next build overwrites in place; copying
    # gives every output folder its own file (an upgrade test otherwise compares a package with itself).
    $staging = Join-Path $artifacts "staging\$Name"
    if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
    & dotnet build $Installer -c $Configuration "-p:PublishDir=$publish" "-p:ProductVersion=$productVersion" "-p:OutputPath=$staging\" @InstallerProperties | Write-Host
    if ($LASTEXITCODE -ne 0) { throw "Building the $Name installer failed." }
    Get-ChildItem $staging -Filter '*.msi' | ForEach-Object { Copy-Item $_.FullName (Join-Path $output $_.Name) -Force }
    Remove-Item -Recurse -Force $staging
    Write-Host "$Name $productVersion -> $output"
    return $semanticVersion
}

$bundledAgentVersion = Build-Component 'agent' (Join-Path $root 'src\SUSyphos.Agent') (Join-Path $root 'installer\Agent\SUSyphos.Agent.Installer.wixproj') $AgentVersion
# The server package ships the agent package of the same build: download in the dashboard and the first agent package.
$agentMsi = Join-Path $output 'SUSyphos.Agent.msi'
$null = Build-Component 'server' (Join-Path $root 'src\SUSyphos.Server') (Join-Path $root 'installer\Server\SUSyphos.Server.Installer.wixproj') $ServerVersion @("-p:AgentMsi=$agentMsi", "-p:AgentVersion=$bundledAgentVersion")
