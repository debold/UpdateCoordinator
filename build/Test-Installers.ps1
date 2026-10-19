<#
.SYNOPSIS
    Installs, upgrades and uninstalls both MSI packages and checks the result end to end.

.DESCRIPTION
    Runs on a disposable CI machine only: it installs services, a self-signed certificate and firewall rules.
    Installs the central server and the agent (version 1) with a self-signed certificate, checks services, registry,
    service recovery, firewall and the health endpoint, waits until the agent registered, upgrades both to version 2
    (settings and data must survive) and uninstalls both (nothing may remain except the data under ProgramData).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version1Directory,
    [Parameter(Mandatory)] [string] $Version2Directory,
    [string] $LogDirectory = (Join-Path $PSScriptRoot '..\artifacts\install-logs'),
    [int] $Port = 9443
)

$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -and $env:UPDATECOORDINATOR_INSTALL_TEST -ne 'yes') {
    throw 'This test changes the machine (services, certificates, firewall). It runs only on CI machines.'
}

$Version1Directory = (Resolve-Path $Version1Directory).Path
$Version2Directory = (Resolve-Path $Version2Directory).Path
foreach ($directory in $Version1Directory, $Version2Directory) {
    foreach ($name in 'UpdateCoordinator.Server.msi', 'UpdateCoordinator.Agent.msi') {
        if (-not (Test-Path (Join-Path $directory $name))) { throw "Installer not found: $(Join-Path $directory $name)" }
    }
}
New-Item -ItemType Directory -Force -Path $LogDirectory | Out-Null
$failures = [System.Collections.Generic.List[string]]::new()

function Check([string] $Name, [scriptblock] $Condition) {
    try { $ok = [bool](& $Condition) } catch { $ok = $false; Write-Host "  $($_.Exception.Message)" }
    if ($ok) { Write-Host "PASS $Name" } else { Write-Host "FAIL $Name"; $failures.Add($Name) }
}

function Invoke-Msi([string] $Arguments, [string] $Log) {
    $process = Start-Process msiexec.exe -ArgumentList "$Arguments /qn /norestart /l*v `"$(Join-Path $LogDirectory $Log)`"" -Wait -PassThru
    return $process.ExitCode
}

function Wait-Until([scriptblock] $Condition, [int] $Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        try { if (& $Condition) { return $true } } catch { }
        Start-Sleep -Seconds 2
    }
    return $false
}

function Get-ProductVersion([string] $Name) {
    Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' |
        ForEach-Object { Get-ItemProperty $_.PSPath } |
        Where-Object { $_.DisplayName -eq $Name } |
        Select-Object -ExpandProperty DisplayVersion -First 1
}

$serverKey = 'HKLM:\SOFTWARE\UpdateCoordinator\Server'
$agentKey = 'HKLM:\SOFTWARE\UpdateCoordinator\Agent'
$healthUrl = "https://localhost:$Port/health"
$certificate = New-SelfSignedCertificate -DnsName 'localhost' -CertStoreLocation 'Cert:\LocalMachine\My' -NotAfter (Get-Date).AddDays(7)

# --- Version 1: fresh installation ------------------------------------------------------------------------------
Check 'server installs' { (Invoke-Msi "/i `"$Version1Directory\UpdateCoordinator.Server.msi`" CERTTHUMBPRINT=$($certificate.Thumbprint) HTTPSPORT=$Port" 'server-install.log') -eq 0 }
Check 'server service runs' { Wait-Until { (Get-Service UpdateCoordinator.Server).Status -eq 'Running' } 60 }
Check 'server settings in registry' { (Get-ItemProperty $serverKey).CertificateThumbprint -eq $certificate.Thumbprint -and (Get-ItemProperty $serverKey).HttpsUrl -eq "https://*:$Port" }
Check 'server restarts on failure' { (sc.exe qfailure UpdateCoordinator.Server | Out-String) -match 'RESTART' }
Check 'firewall rule for the port' { (Get-NetFirewallRule -DisplayName 'UpdateCoordinator Central Server (HTTPS)' | Get-NetFirewallPortFilter).LocalPort -eq "$Port" }
Check 'health endpoint answers' { Wait-Until { (Invoke-WebRequest $healthUrl -SkipCertificateCheck -UseBasicParsing).StatusCode -eq 200 } 90 }

Check 'agent installs' { (Invoke-Msi "/i `"$Version1Directory\UpdateCoordinator.Agent.msi`" SERVERURL=https://localhost:$Port/" 'agent-install.log') -eq 0 }
Check 'agent service runs' { Wait-Until { (Get-Service UpdateCoordinator.Agent).Status -eq 'Running' } 60 }
Check 'agent address in registry' { (Get-ItemProperty $agentKey).CentralServerUrl -eq "https://localhost:$Port/" }
Check 'agent restarts on failure, also on error exit codes' {
    ((sc.exe qfailure UpdateCoordinator.Agent | Out-String) -match 'RESTART') -and ((sc.exe qfailureflag UpdateCoordinator.Agent | Out-String) -match ':\s*TRUE')
}
Check 'agent registers at the central server' {
    Wait-Until { Select-String -Path "$env:ProgramData\UpdateCoordinator\Agent\Logs\agent-*.log" -Pattern 'Registered at the central server' -Quiet } 120
}

# --- Version 2: upgrade without options keeps settings and data ------------------------------------------------
Check 'server upgrades' { (Invoke-Msi "/i `"$Version2Directory\UpdateCoordinator.Server.msi`"" 'server-upgrade.log') -eq 0 }
Check 'server version 2 installed once' { (Get-ProductVersion 'UpdateCoordinator Central Server') -eq '0.2.0' }
Check 'server keeps certificate setting' { (Get-ItemProperty $serverKey).CertificateThumbprint -eq $certificate.Thumbprint }
Check 'server keeps its database' { Test-Path "$env:ProgramData\UpdateCoordinator\Server\server.db" }
Check 'server runs after upgrade' { Wait-Until { (Invoke-WebRequest $healthUrl -SkipCertificateCheck -UseBasicParsing).StatusCode -eq 200 } 90 }

Check 'agent upgrades' { (Invoke-Msi "/i `"$Version2Directory\UpdateCoordinator.Agent.msi`"" 'agent-upgrade.log') -eq 0 }
Check 'agent version 2 installed once' { (Get-ProductVersion 'UpdateCoordinator Agent') -eq '0.2.0' }
Check 'agent keeps its address' { (Get-ItemProperty $agentKey).CentralServerUrl -eq "https://localhost:$Port/" }
Check 'agent keeps its identity' { Test-Path "$env:ProgramData\UpdateCoordinator\Agent\identity.dat" }
Check 'agent runs after upgrade' { Wait-Until { (Get-Service UpdateCoordinator.Agent).Status -eq 'Running' } 60 }

# --- Uninstall: services, files, settings and firewall rule are gone; data stays ---------------------------------
Check 'agent uninstalls' { (Invoke-Msi "/x `"$Version2Directory\UpdateCoordinator.Agent.msi`"" 'agent-uninstall.log') -eq 0 }
Check 'server uninstalls' { (Invoke-Msi "/x `"$Version2Directory\UpdateCoordinator.Server.msi`"" 'server-uninstall.log') -eq 0 }
Check 'services removed' { -not (Get-Service UpdateCoordinator.Agent, UpdateCoordinator.Server -ErrorAction SilentlyContinue) }
Check 'program files removed' { -not (Test-Path "$env:ProgramFiles\UpdateCoordinator") }
Check 'installer settings removed' { -not (Test-Path $agentKey) -and -not (Test-Path $serverKey) }
Check 'firewall rule removed' { -not (Get-NetFirewallRule -DisplayName 'UpdateCoordinator Central Server (HTTPS)' -ErrorAction SilentlyContinue) }
Check 'data under ProgramData kept' { Test-Path "$env:ProgramData\UpdateCoordinator\Server\server.db" }

Remove-Item -Path "Cert:\LocalMachine\My\$($certificate.Thumbprint)"

if ($failures.Count -gt 0) {
    throw "$($failures.Count) installer checks failed: $($failures -join ', ')"
}

Write-Host 'All installer checks passed.'
