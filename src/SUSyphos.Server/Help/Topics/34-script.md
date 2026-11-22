# Scripts of a package {#top}

A script is a PowerShell script of a WinGet package that the agent runs as SYSTEM before or after it installs, upgrades or uninstalls the package with WinGet (Marc, 2026-11-13): for example to stop a service before an upgrade, to write a setting after the installation or to remove what an uninstaller left behind. Each package has at most one script per phase: **Before installation**, **After installation**, **Before uninstallation** and **After uninstallation**. Agents run only approved revisions, signed with the central server's own script key, which the trust anchor certifies; a server runs none while scripts are switched off under [Agent settings](help/agent-settings#scripts). Everybody who may read the dashboard reads scripts; only administrators change and approve them.

The page is reached from the **Scripts** box of the [package page](help/package-detail#scripts).

## Action band {#actions}

- **Approve and sign…** (administrators, needs a saved draft): opens [the approval](#approve).
- **Save draft**: stores the content, the options and the parameters as a draft. A draft never reaches an agent; the approved revision keeps running.
- **Discard draft**: throws the draft away (a script without an approved revision goes with it).
- **Delete script…** (red): deletes the script with all its revisions and the values of its parameters; from their next release list no server runs it.

## Status {#status}

**Approved**: the revision agents run, who approved it and when. **Draft**: whether a draft waits and who changed it. **Virus scan**: the result of the AMSI scan of the approved revision (*Clean*, *NotScanned* when no antivirus product answered). **Local veto**: a server with `AllowScripts = 0` under `HKLM\SOFTWARE\Policies\SUSyphos\Agent` (for example set by group policy) runs no script and does not install a package that has one; it reports *Blocked locally*.

## Details {#details}

Of the approved revision, as signed: **Runs on**, **On failure**, **Timeout**, **PowerShell** (Windows PowerShell 5.1, or PowerShell 7 with fallback to 5.1); scripts always run as SYSTEM (the subtitle says so), its **SHA-256**, the **Signing key** and when it was **Approved**.

## Content {#content}

- **Script**: the PowerShell text. Exit code 0 is success, 3010 asks for a restart (it waits for the maintenance window), any other code is a failure; 1641 counts like 3010. The agent passes `SUSYPHOS_ACTION` (Install, Upgrade, Uninstall), `SUSYPHOS_PACKAGE_ID`, `SUSYPHOS_VERSION`, `SUSYPHOS_PREVIOUS_VERSION`, `SUSYPHOS_ARCHITECTURE`, `SUSYPHOS_INSTALLER_EXIT_CODE` and, when the script brings [files](#files) along, `SUSYPHOS_FILES` as environment variables (a new script explains them in a comment at its top); values of parameters are read with `Get-SUSyphosParameter Name`.
- **Runs on** (before and after installation only): **Installations and upgrades**, **New installations only** or **Upgrades only**.
- **On failure**: before the installation **Do not install** (default) or **Install anyway**; before the uninstallation **Do not uninstall**; after it, the result counts as failed (the software stays installed or removed).
- **Timeout**: 1 to 30 minutes (default 5); after it the agent ends the script with every process it started (result *Timed out*, 0x800705B4).
- **PowerShell**: *PowerShell 7, else 5.1* (the default for new scripts since 0.9.0-rc.8: Microsoft deprecated Windows PowerShell 5.1 on 2026-11-17, and Windows Server 2025 is the last version with it switched on by default) or *Windows PowerShell 5.1* (present on every server up to Windows Server 2025; scripts written before rc.8 keep it). PowerShell 7 runs from `C:\Program Files\PowerShell\7\pwsh.exe` where it is installed, never from the PATH; elsewhere the script runs in Windows PowerShell 5.1 and the first line of its output says so. To install PowerShell 7 on the servers, use **Recommended** on the [Software page](help/software#recommended) (package **Microsoft.PowerShell**); the Status box names the servers that run this package without PowerShell 7. The choice is part of the signed revision: changing it needs a new approval, and agents before 0.9.0-rc.7 refuse a PowerShell 7 revision instead of running it in 5.1.

Things to know: never put secrets into the script text (Script Block Logging of Windows may log it); use a secret parameter instead. Do not restart the server from the script, return 3010. 32-bit programs write their registry keys under `WOW6432Node`. A group policy that sets the PowerShell execution policy *AllSigned* or switches scripts off, WDAC or AppLocker (ConstrainedLanguage) and some EDR products stop scripts; the output names the language mode and the execution policy. A local administrator of a server can always change what runs there; the signature protects against a changed central server database, changes in transit and older revisions played back.

## Parameters {#parameters}

One row per parameter, the last row adds a new one: **Name** (a letter, then letters, digits or underscores), **Type** (**Text**, or **Secret**: stored encrypted, never shown again, masked as ******** in every output), **Required**, **Default** (not for secrets) and **Description**. Values are set per assignment on the package page (**Script values…**); without an assignment the default applies.

## Files {#files}

A script can bring files along as one ZIP (Marc, 2026-11-22), for example the configuration file of a program. For every run the agent unpacks them into the folder `files` of the run, which only SYSTEM and administrators reach; `$env:SUSYPHOS_FILES` names it, and the folder is deleted after the run, so the script copies what has to stay.

- **Add files** / **Replace the files** (administrators): choose a ZIP of at most 50 MB, at most 200 MB and 1000 files unpacked, with relative paths only (no `..`, drive letters, alternate data streams or links). **Upload** checks it, scans the ZIP and every file in it for malware and adds it to the draft; the next approval signs its SHA-256 with the script. Save changes to the script text first: uploading keeps the saved draft, not text you have not saved yet.
- The box lists every file with its size and SHA-256, the scan result and who uploaded it; **Download ZIP** saves the ZIP. While the draft has other files than the approved revision, a note says that agents still get those of the approved revision.
- **Remove files** takes the files out of the draft; agents keep getting those of the approved revision until the next approval.

The agent downloads the ZIP once per content and keeps it for 30 days; before unpacking it checks it against the signed SHA-256 and the same rules as the upload, so changed or foreign files never reach a script. Agents before 0.9.0-rc.8 refuse a revision with files instead of running it without them. Never put secrets into the files: use a secret parameter.

## Example: BGInfo with its configuration {#example-bginfo}

BGInfo (WinGet package **Microsoft.Sysinternals.BGInfo**) shows the server's data on the desktop background. To roll it out with your own layout:

1. Save your layout in BGInfo as `config.bgi` and put it into a ZIP, for example `bginfo.zip`.
2. On the [package page](help/package-detail) of BGInfo open the script **After installation**, paste the script below, **Save draft**, then **Add files** with the ZIP and **Approve and sign…**.
3. Open the script **After uninstallation**, paste the second script, save and approve it.
4. Assign the package (for example **Recommended** or **Assign package…**) and switch scripts on under [Agent settings](help/agent-settings).

After installation: copies the configuration to ProgramData and starts BGInfo with it at every sign-in of a user.

```powershell
# Runs after WinGet installed or upgraded BGInfo, as SYSTEM.
$target = Join-Path $env:ProgramData 'BGInfo'
New-Item -ItemType Directory -Path $target -Force | Out-Null
Copy-Item -Path (Join-Path $env:SUSYPHOS_FILES 'config.bgi') -Destination (Join-Path $target 'config.bgi') -Force

# WinGet puts the program of a portable package for all users under Program Files\WinGet\Links.
$exe = @('Bginfo64.exe', 'Bginfo.exe') |
    ForEach-Object { Join-Path $env:ProgramFiles "WinGet\Links\$_" } |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $exe) { Write-Error 'BGInfo was not found under Program Files\WinGet\Links.'; exit 1 }

$command = '"{0}" "{1}" /timer:0 /silent /nolicprompt' -f $exe, (Join-Path $target 'config.bgi')
Set-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name 'BGInfo' -Value $command
Write-Output "BGInfo starts at sign-in: $command"
exit 0
```

After uninstallation: removes the entry and the configuration again.

```powershell
# Runs after WinGet uninstalled BGInfo, as SYSTEM.
Remove-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name 'BGInfo' -ErrorAction SilentlyContinue
Remove-Item -Path (Join-Path $env:ProgramData 'BGInfo') -Recurse -Force -ErrorAction SilentlyContinue
Write-Output 'BGInfo entry and configuration removed.'
exit 0
```

Both scripts run in PowerShell 7 and in Windows PowerShell 5.1. BGInfo is a portable package: WinGet installs it only from Windows Server 2022 on (Windows 10.0.18362 and later); on older servers the version shows as *Not supported*.

## Revisions {#revisions}

Every approved revision with when and by whom, its virus scan and SHA-256; *runs* marks the one agents run. **Download** (.ps1) saves the revision as signed. An agent never runs a revision older than one it ran before.

## Approve and sign {#approve}

The dialog says which revision it replaces, on how many servers it runs as SYSTEM and what changed (lines added and removed). **I have read the whole script** must be ticked. **Approve and sign** scans the script with AMSI (a finding refuses the approval while the scan mode is Enforce), signs it as the next revision and announces it to the agents; **Cancel** closes the dialog. Recorded in the audit log with the SHA-256 and the key.

## Delete {#delete}

**Delete script** in the confirmation deletes the script, its revisions and its values (audited); **Cancel** keeps it.
