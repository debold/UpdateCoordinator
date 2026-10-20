<#
.SYNOPSIS
    Installs, upgrades and uninstalls both MSI packages and checks the result end to end.

.DESCRIPTION
    Runs on a disposable CI machine only: it installs services, a self-signed certificate and firewall rules.
    Installs the central server and the agent (version 1) with a self-signed certificate, checks services, registry,
    service recovery, firewall and the health endpoint, waits until the agent registered, upgrades both to version 2
    (settings and data must survive) and uninstalls both (nothing may remain except the data under ProgramData).
    Finally installs the central server without CERTTHUMBPRINT: it must create, use and reuse its own certificate.
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
# Windows Installer cannot resolve source paths with ".." segments in maintenance operations.
$LogDirectory = [System.IO.Path]::GetFullPath($LogDirectory)
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

function Get-ProductCode([string] $Name) {
    Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' |
        Where-Object { (Get-ItemProperty $_.PSPath).DisplayName -eq $Name } |
        Select-Object -ExpandProperty PSChildName -First 1
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
$dashboardShortcut = "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\UpdateCoordinator\UpdateCoordinator Dashboard.url"
$certificate = New-SelfSignedCertificate -DnsName 'localhost' -CertStoreLocation 'Cert:\LocalMachine\My' -NotAfter (Get-Date).AddDays(7)

# --- Version 1: fresh installation ------------------------------------------------------------------------------
Check 'server installs' { (Invoke-Msi "/i `"$Version1Directory\UpdateCoordinator.Server.msi`" CERTTHUMBPRINT=$($certificate.Thumbprint) HTTPSPORT=$Port" 'server-install.log') -eq 0 }
Check 'server service runs' { Wait-Until { (Get-Service UpdateCoordinator.Server).Status -eq 'Running' } 60 }
Check 'server settings in registry' { (Get-ItemProperty $serverKey).CertificateThumbprint -eq $certificate.Thumbprint -and (Get-ItemProperty $serverKey).HttpsUrl -eq "https://*:$Port" }
$serverCodeV1 = Get-ProductCode 'UpdateCoordinator Central Server'
Check 'server restarts on failure' { (sc.exe qfailure UpdateCoordinator.Server | Out-String) -match 'RESTART' }
Check 'firewall rule for the port' { (Get-NetFirewallRule -DisplayName 'UpdateCoordinator Central Server (HTTPS)' | Get-NetFirewallPortFilter).LocalPort -eq "$Port" }
Check 'health endpoint answers' { Wait-Until { (Invoke-WebRequest $healthUrl -SkipCertificateCheck -UseBasicParsing).StatusCode -eq 200 } 90 }
Check 'start menu entry opens the dashboard' { (Get-Content $dashboardShortcut -Raw) -match "URL=https://$($env:COMPUTERNAME):$Port/" }

Check 'agent installs' { (Invoke-Msi "/i `"$Version1Directory\UpdateCoordinator.Agent.msi`" SERVERURL=https://localhost:$Port/" 'agent-install.log') -eq 0 }
Check 'agent service runs' { Wait-Until { (Get-Service UpdateCoordinator.Agent).Status -eq 'Running' } 60 }
Check 'agent address in registry' { (Get-ItemProperty $agentKey).CentralServerUrl -eq "https://localhost:$Port/" }
$agentCodeV1 = Get-ProductCode 'UpdateCoordinator Agent'
Check 'agent restarts on failure, also on error exit codes' {
    ((sc.exe qfailure UpdateCoordinator.Agent | Out-String) -match 'RESTART') -and ((sc.exe qfailureflag UpdateCoordinator.Agent | Out-String) -match ':\s*TRUE')
}
Check 'agent registers at the central server' {
    Wait-Until { Select-String -Path "$env:ProgramData\UpdateCoordinator\Agent\Logs\agent-*.log" -Pattern 'Registered at the central server' -Quiet } 120
}

Check 'agent stays below its memory limit after registration and inventory' {
    Start-Sleep -Seconds 30
    $agentMb = [math]::Round((Get-Process UpdateCoordinator.Agent).PrivateMemorySize64 / 1MB)
    $serverMb = [math]::Round((Get-Process UpdateCoordinator.Server).PrivateMemorySize64 / 1MB)
    Write-Host "  measured private memory: agent $agentMb MB, central server $serverMb MB"
    $agentMb -lt 200
}

# --- Version 2: upgrade without options keeps settings and data ------------------------------------------------
# The upgrade runs over a crashing service that service recovery restarts every minute (seen in a lab with 0.1.0
# without certificate): an unreadable trust anchor stops the service at start until the original file is back.
$anchor = "$env:ProgramData\UpdateCoordinator\Server\trust-anchor.key"
Copy-Item $anchor "$anchor.backup"
Set-Content -Path $anchor -Value 'unreadable'
Restart-Service UpdateCoordinator.Server -ErrorAction SilentlyContinue
Check 'server crashes before the upgrade (test setup)' { Wait-Until { (Get-Service UpdateCoordinator.Server).Status -eq 'Stopped' } 60 }
# Upgraded from a copy named like a browser's second download, since the file name must never matter.
$renamed = Join-Path $LogDirectory 'renamed'
New-Item -ItemType Directory -Force -Path $renamed | Out-Null
Copy-Item "$Version2Directory\UpdateCoordinator.Server.msi" (Join-Path $renamed 'UpdateCoordinator.Server (1).msi')
Check 'server upgrades over a crashing service from a renamed file' { (Invoke-Msi "/i `"$renamed\UpdateCoordinator.Server (1).msi`"" 'server-upgrade.log') -eq 0 }
Move-Item "$anchor.backup" $anchor -Force
Start-Service UpdateCoordinator.Server -ErrorAction SilentlyContinue
Check 'server version 2 installed once' { (Get-ProductVersion 'UpdateCoordinator Central Server') -eq '0.2.0' }
Check 'server upgrade replaced the product (new product code)' { (Get-ProductCode 'UpdateCoordinator Central Server') -ne $serverCodeV1 }
Check 'server keeps certificate setting' { (Get-ItemProperty $serverKey).CertificateThumbprint -eq $certificate.Thumbprint }
Check 'server keeps its database' { Test-Path "$env:ProgramData\UpdateCoordinator\Server\server.db" }
Check 'server runs after upgrade' { Wait-Until { (Invoke-WebRequest $healthUrl -SkipCertificateCheck -UseBasicParsing).StatusCode -eq 200 } 90 }

Check 'server ships the agent package' { Test-Path "$env:ProgramFiles\UpdateCoordinator\Server\agent\UpdateCoordinator.Agent-0.2.0.msi" }

# The agent is upgraded from a file named like a download from the central server: address and anchor fingerprint
# come from the file name, without any installer option.
$binding = Invoke-RestMethod "https://localhost:$Port/api/v1/trust/binding" -SkipCertificateCheck
$fingerprint = -join ([System.Security.Cryptography.SHA256]::HashData([Convert]::FromBase64String($binding.anchorPublicKey))[0..15] | ForEach-Object { $_.ToString('x2') })
$downloaded = Join-Path $renamed "UpdateCoordinator.Agent-0.2.0_localhost_$($Port)_$fingerprint (1).msi"
Copy-Item "$Version2Directory\UpdateCoordinator.Agent.msi" $downloaded
Check 'agent upgrades from a downloaded file name' { (Invoke-Msi "/i `"$downloaded`"" 'agent-upgrade.log') -eq 0 }
Check 'agent address and anchor fingerprint from the file name' {
    (Get-ItemProperty $agentKey).CentralServerUrl -eq "https://localhost:$Port/" -and (Get-ItemProperty $agentKey).TrustAnchorFingerprint -eq $fingerprint
}
Check 'agent version 2 installed once' { (Get-ProductVersion 'UpdateCoordinator Agent') -eq '0.2.0' }
Check 'agent upgrade replaced the product (new product code)' { (Get-ProductCode 'UpdateCoordinator Agent') -ne $agentCodeV1 }
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
Check 'start menu entry removed' { -not (Test-Path $dashboardShortcut) }
Check 'data under ProgramData kept' { Test-Path "$env:ProgramData\UpdateCoordinator\Server\server.db" }

Remove-Item -Path "Cert:\LocalMachine\My\$($certificate.Thumbprint)"

# --- Without CERTTHUMBPRINT: the central server creates its own certificate --------------------------------------
function Get-OwnCertificate { Get-ChildItem 'Cert:\LocalMachine\My' | Where-Object FriendlyName -eq 'UpdateCoordinator self-signed' }
Check 'server installs without certificate' { (Invoke-Msi "/i `"$Version2Directory\UpdateCoordinator.Server.msi`" HTTPSPORT=$Port" 'server-install-own-certificate.log') -eq 0 }
Check 'server service runs without certificate' { Wait-Until { (Get-Service UpdateCoordinator.Server).Status -eq 'Running' } 60 }
Check 'server creates a self-signed certificate' { Wait-Until { @(Get-OwnCertificate).Count -eq 1 } 90 }
Check 'own certificate key cannot be exported' { [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey((Get-OwnCertificate)).Key.ExportPolicy -eq [System.Security.Cryptography.CngExportPolicies]::None }
Check 'health endpoint answers with own certificate' { Wait-Until { (Invoke-WebRequest $healthUrl -SkipCertificateCheck -UseBasicParsing).StatusCode -eq 200 } 90 }
Check 'server reuses its certificate after restart' {
    Restart-Service UpdateCoordinator.Server
    (Wait-Until { (Invoke-WebRequest $healthUrl -SkipCertificateCheck -UseBasicParsing).StatusCode -eq 200 } 90) -and @(Get-OwnCertificate).Count -eq 1
}
Check 'server uninstalls again' { (Invoke-Msi "/x `"$Version2Directory\UpdateCoordinator.Server.msi`"" 'server-uninstall-own-certificate.log') -eq 0 }
# PowerShell 7 has no Remove-Item -DeleteKey: delete the persisted CNG key explicitly.
foreach ($own in Get-OwnCertificate) {
    $key = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($own)
    Remove-Item -Path "Cert:\LocalMachine\My\$($own.Thumbprint)"
    $key.Key.Delete()
}

if ($failures.Count -gt 0) {
    throw "$($failures.Count) installer checks failed: $($failures -join ', ')"
}

Write-Host 'All installer checks passed.'
