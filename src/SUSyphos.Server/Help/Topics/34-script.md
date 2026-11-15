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

Of the approved revision, as signed: **Runs on**, **On failure**, **Timeout**, **Runs as** (SYSTEM, Windows PowerShell 5.1), its **SHA-256**, the **Signing key** and when it was **Approved**.

## Content {#content}

- **Script**: the PowerShell text. Exit code 0 is success, 3010 asks for a restart (it waits for the maintenance window), any other code is a failure; 1641 counts like 3010. The agent passes `SUSYPHOS_ACTION` (Install, Upgrade, Uninstall), `SUSYPHOS_PACKAGE_ID`, `SUSYPHOS_VERSION`, `SUSYPHOS_PREVIOUS_VERSION`, `SUSYPHOS_ARCHITECTURE` and `SUSYPHOS_INSTALLER_EXIT_CODE` as environment variables; values of parameters are read with `Get-SUSyphosParameter Name`.
- **Runs on** (before and after installation only): **Installations and upgrades**, **New installations only** or **Upgrades only**.
- **On failure**: before the installation **Do not install** (default) or **Install anyway**; before the uninstallation **Do not uninstall**; after it, the result counts as failed (the software stays installed or removed).
- **Timeout**: 1 to 30 minutes (default 5); after it the agent ends the script with every process it started (result *Timed out*, 0x800705B4).

Things to know: never put secrets into the script text (Script Block Logging of Windows may log it); use a secret parameter instead. Do not restart the server from the script, return 3010. 32-bit programs write their registry keys under `WOW6432Node`. A group policy that sets the PowerShell execution policy *AllSigned* or switches scripts off, WDAC or AppLocker (ConstrainedLanguage) and some EDR products stop scripts; the output names the language mode and the execution policy. A local administrator of a server can always change what runs there; the signature protects against a changed central server database, changes in transit and older revisions played back.

## Parameters {#parameters}

One row per parameter, the last row adds a new one: **Name** (a letter, then letters, digits or underscores), **Type** (**Text**, or **Secret**: stored encrypted, never shown again, masked as ******** in every output), **Required**, **Default** (not for secrets) and **Description**. Values are set per assignment on the package page (**Script values…**); without an assignment the default applies.

## Revisions {#revisions}

Every approved revision with when and by whom, its virus scan and SHA-256; *runs* marks the one agents run. **Download** (.ps1) saves the revision as signed. An agent never runs a revision older than one it ran before.

## Approve and sign {#approve}

The dialog says which revision it replaces, on how many servers it runs as SYSTEM and what changed (lines added and removed). **I have read the whole script** must be ticked. **Approve and sign** scans the script with AMSI (a finding refuses the approval while the scan mode is Enforce), signs it as the next revision and announces it to the agents; **Cancel** closes the dialog. Recorded in the audit log with the SHA-256 and the key.

## Delete {#delete}

**Delete script** in the confirmation deletes the script, its revisions and its values (audited); **Cancel** keeps it.
