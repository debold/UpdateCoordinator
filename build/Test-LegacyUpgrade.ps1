<#
.SYNOPSIS
    Upgrades an installation from before the rename (UpdateCoordinator 0.4.0) to the current SUSyphos packages.

.DESCRIPTION
    Runs on a disposable CI machine only. Installs the published UpdateCoordinator 0.4.0 central server and agent, waits
    until the agent registered, upgrades both with the current packages without options and checks that services,
    settings, the database, the agent identity and the trust survive under the new name. Uninstalls both at the end.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $LegacyDirectory,
    [Parameter(Mandatory)] [string] $CurrentDirectory,
    [string] $LogDirectory = (Join-Path $PSScriptRoot '..\artifacts\install-logs\legacy'),
    [int] $Port = 9444
)

$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -and $env:SUSYPHOS_INSTALL_TEST -ne 'yes') {
    throw 'This test changes the machine (services, certificates, firewall). It runs only on CI machines.'
}

$LegacyDirectory = (Resolve-Path $LegacyDirectory).Path
$CurrentDirectory = (Resolve-Path $CurrentDirectory).Path
New-Item -ItemType Directory -Force -Path $LogDirectory | Out-Null
$LogDirectory = [System.IO.Path]::GetFullPath($LogDirectory)
$failures = [System.Collections.Generic.List[string]]::new()

function Check([string] $Name, [scriptblock] $Condition) {
    try { $ok = [bool](& $Condition) } catch { $ok = $false; Write-Host "  $($_.Exception.Message)" }
    if ($ok) { Write-Host "PASS $Name" } else { Write-Host "FAIL $Name"; $failures.Add($Name) }
}

function Wait-Until([scriptblock] $Condition, [int] $Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        try { if (& $Condition) { return $true } } catch { }
        Start-Sleep -Seconds 2
    }
    return $false
}

function Invoke-Msi([string] $Arguments, [string] $Log) {
    $process = Start-Process msiexec.exe -ArgumentList "$Arguments /qn /norestart /l*v `"$(Join-Path $LogDirectory $Log)`"" -Wait -PassThru
    return $process.ExitCode
}

$legacyData = "$env:ProgramData\UpdateCoordinator"
$data = "$env:ProgramData\SUSyphos"
# Start from a clean machine: an earlier test leaves data under ProgramData on purpose.
Remove-Item -Recurse -Force $legacyData, $data -ErrorAction SilentlyContinue

$legacyServer = Get-ChildItem $LegacyDirectory -Filter 'UpdateCoordinator.Server*.msi' | Select-Object -First 1
$legacyAgent = Get-ChildItem $LegacyDirectory -Filter 'UpdateCoordinator.Agent*.msi' | Select-Object -First 1
$healthUrl = "https://localhost:$Port/health"

# --- UpdateCoordinator 0.4.0 -------------------------------------------------------------------------------------
Check 'legacy server installs' { (Invoke-Msi "/i `"$($legacyServer.FullName)`" HTTPSPORT=$Port" 'legacy-server-install.log') -eq 0 }
Check 'legacy server answers' { Wait-Until { (Invoke-WebRequest $healthUrl -SkipCertificateCheck -UseBasicParsing).StatusCode -eq 200 } 120 }
Check 'legacy agent installs' { (Invoke-Msi "/i `"$($legacyAgent.FullName)`" SERVERURL=https://localhost:$Port/" 'legacy-agent-install.log') -eq 0 }
Check 'legacy agent registers' {
    Wait-Until { Select-String -Path "$legacyData\Agent\Logs\agent-*.log" -Pattern 'Registered at the central server' -Quiet } 120
}
$identityBefore = (Get-FileHash "$legacyData\Agent\identity.dat").Hash
$bindingBefore = Invoke-RestMethod "https://localhost:$Port/api/v1/trust/binding" -SkipCertificateCheck

# --- Upgrade to SUSyphos -----------------------------------------------------------------------------------------
Check 'server upgrades to SUSyphos' { (Invoke-Msi "/i `"$CurrentDirectory\SUSyphos.Server.msi`"" 'upgrade-server.log') -eq 0 }
Check 'old server service removed, new one runs' {
    -not (Get-Service UpdateCoordinator.Server -ErrorAction SilentlyContinue) -and (Wait-Until { (Get-Service SUSyphos.Server).Status -eq 'Running' } 60)
}
Check 'server keeps its port' { (Get-ItemProperty 'HKLM:\SOFTWARE\SUSyphos\Server').HttpsUrl -eq "https://*:$Port" }
Check 'server answers after the upgrade' { Wait-Until { (Invoke-WebRequest $healthUrl -SkipCertificateCheck -UseBasicParsing).StatusCode -eq 200 } 120 }
Check 'server data moved to the new folder' { (Test-Path "$data\Server\server.db") -and -not (Test-Path "$legacyData\Server") }
Check 'server keeps its trust anchor' {
    (Invoke-RestMethod "https://localhost:$Port/api/v1/trust/binding" -SkipCertificateCheck).anchorPublicKey -eq $bindingBefore.anchorPublicKey
}

Check 'agent upgrades to SUSyphos' { (Invoke-Msi "/i `"$CurrentDirectory\SUSyphos.Agent.msi`"" 'upgrade-agent.log') -eq 0 }
Check 'old agent service removed, new one runs' {
    -not (Get-Service UpdateCoordinator.Agent -ErrorAction SilentlyContinue) -and (Wait-Until { (Get-Service SUSyphos.Agent).Status -eq 'Running' } 60)
}
Check 'agent keeps its address' { (Get-ItemProperty 'HKLM:\SOFTWARE\SUSyphos\Agent').CentralServerUrl -eq "https://localhost:$Port/" }
Check 'agent identity moved unchanged' { Wait-Until { (Test-Path "$data\Agent\identity.dat") -and (Get-FileHash "$data\Agent\identity.dat").Hash -eq $identityBefore } 60 }
Check 'agent keeps talking to the central server without registering again' {
    Start-Sleep -Seconds 30
    $log = Get-Content "$data\Agent\Logs\agent-*.log" -Raw
    $after = $log.Substring($log.LastIndexOf('Took over the data of the previous version'))
    -not ($after -match 'Registered at the central server|rejected the credentials|not trusted')
}

# --- Clean up ----------------------------------------------------------------------------------------------------
Check 'agent uninstalls' { (Invoke-Msi "/x `"$CurrentDirectory\SUSyphos.Agent.msi`"" 'legacy-agent-uninstall.log') -eq 0 }
Check 'server uninstalls' { (Invoke-Msi "/x `"$CurrentDirectory\SUSyphos.Server.msi`"" 'legacy-server-uninstall.log') -eq 0 }

if ($failures.Count -gt 0) {
    throw "$($failures.Count) check(s) failed: $($failures -join '; ')"
}
Write-Host 'All legacy upgrade checks passed.'
