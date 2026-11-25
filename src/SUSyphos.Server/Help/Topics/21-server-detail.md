# Server details {#top}

Everything about one server: its state, what the agent does next, its configuration, its updates and its history. Open it from the [server list](help/servers) or any link with the server name. The line under the name shows the fully qualified name, operating system, build and ring; the line below names the groups of the server, each linked to its [group page](help/group-detail), coordinated ones marked as such (and as stopped). While a coordinated group of the server is stopped after failed leases, a warning says so with a link to the group: the server installs no Windows update until it is resumed. In **Tasks**, the installation of a server in a coordinated group says why it may wait: the group is stopped, or another server holds the lease until a given time.

![The tiles at the top of a server page: status, updates, next maintenance window and last heartbeat](help-images/server-page.png)

A red banner **Certificate** appears when the last software installation of this server failed because it does not trust the certificate of the central server; see [The certificate of the central server](help/software#certificate).

## Queued restart {#restart-queued}

A red banner at the top appears when a restart of the server is queued or planned: requested by an administrator (now or in the next maintenance window) or announced by the agent. It says when the restart happens or why it waits. Once the agent has announced the restart to Windows (the five-minute warning signed-in users see), the banner says **Restart announced** with the exact time; the System box, the Tasks box and the server list show that time too.

**Cancel restart** (administrators) withdraws it. If the agent has not picked up the request yet, it is simply cancelled; otherwise the agent is told to drop the restart it holds or has announced (a "Cancel restart" remote action). An announced Windows restart with its five-minute warning is aborted as well.

## Pending restart {#restart-pending}

An amber banner **Restart pending** appears while the server needs a restart that nobody requested yet: Windows reports one, or an installation by SUSyphos since the last start asked for one (for example an installer with exit code 3010). It names what asked for the restart and when it happens: in the next maintenance window when the policy of the server allows the agent to restart there, otherwise not by itself. **Restart now…** (administrators) opens the [restart dialog](#actions), now or in the next window. The Tasks box lists the planned restart as well. Once a restart is requested or announced, the red banner *Queued restart* takes over.

## Actions for this server {#actions}

The band below the title groups the actions by topic in drop-down buttons. Operators and administrators can send them; an entry only administrators may use is greyed out for operators, with the reason as tooltip. The agent picks each action up with its next heartbeat; the central server never connects to the server itself. The result appears under [Remote actions](#remote-actions) in the Activity tab.

**Updates** (the main group, blue):

- **Search for updates**: search Windows Update now and report the result.
- **Sync inventory and history**: send the complete inventory again (operating system, roles, installed updates, Windows Update history) and search.
- **Install all released now…** (administrators): opens the [install dialog](#install-now) for every released update; greyed out while no released update is missing. To install, hold or uninstall chosen updates, select them in the lists of the Updates tab ([Missing updates](#missing), [Installed updates](#installed)).

**Server**:

- **Restart server…** (administrators): opens the [restart dialog](#restart).
- **Restart agent service**: the SUSyphos agent service restarts itself; the server keeps running.
- **Request logs**: the agent uploads its logs of the last 3 days compressed (at most 10 MB); when they arrived, a **Download** link appears in the remote action. The ZIP file holds the agent logs, `Events/Application.txt` (Windows Installer, crashes, .NET, Windows Error Reporting and SUSyphos from the Application event log), the WinGet helper steps and installer logs (`WinGet/`), WinGet's own logs (`WinGetCOM/`), logs installers wrote straight into the temp folders of SYSTEM (`SystemTemp/`, `WindowsTemp/`, for example `dd_*.log` or `MSI*.log`), the msiexec logs of agent updates (`AgentUpdate/`) and `collection.txt`, which lists what is in it, which folders were missing and what was left out to stay within 10 MB (the agent logs come first).
- **Debug mode…** (administrators): opens the [debug mode dialog](#debug).

**Refresh** loads the page again with the latest state. **Download logs** (operators) appears once the agent uploaded its logs and downloads the newest upload while it is kept: blue while you have not downloaded these logs, green with a check once you did; its tooltip says when the agent uploaded them and when you downloaded them, and a small dot on the button shows that newer logs are requested and not here yet. While the agent works or an action waits to be picked up or for its result, the Overview, Activity and History tabs also refresh themselves every 10 seconds (the hint of the Tasks box says so) and keep their scroll position; after "Install now", an uninstallation or a search the refresh goes on for three more minutes, so the installation report, the inventory, the tiles and the tab counters show up without a click. After an action the page is loaded anew, so F5 never sends the action a second time.

**Remove** (red, on the right; administrators): **Remove server…** opens the [remove dialog](#remove).

## Status tiles {#tiles}

- **Status**: the server status as in the [server list](help/servers#status), with the agent version below. **Show ›** opens the pre-flight checks.
- **Updates**: released updates the server still has to install, and all updates it misses (also those the rings still hold back). **Show ›** opens the Updates tab.
- **Next maintenance window**: when the next window opens, with the number of updates it installs below. Windows are in the time zone of the server. "Open" means a window is open now (until the time shown); "no window" means the server has none and installs nothing; "unknown" until the agent reports its time zone.
- **Last heartbeat**: time of the last heartbeat, with the last boot below; a red edge when the server is offline.

## Tabs {#tabs}

The boxes are split into tabs below the tiles, shown as folder tabs on a surface that holds the boxes of the current tab; the tab is part of the address, so a link or a reload opens the same tab. A badge shows where something waits:

- **Overview**: Tasks and pre-flight checks; "!" when a check failed.
- **Updates**: the missing updates, with their count (red when one failed).
- **Software**: the installed programs, with the number of packages a newer version exists for.
- **Security**: the security baseline and the open vulnerabilities of the server, with their count (red when one is exploited).
- **History**: what SUSyphos installed ("Installed by SUSyphos") and every installation on the server from the Windows Update history ("All installations on the server").
- **Configuration**: the assignment (ring, reboots, Automatic Updates, update source, groups, maintenance windows, agent settings).
- **System**: the facts the agent reports; "!" when Windows installs on its own, the agent update failed or a move did not work.
- **Activity**: recent activity and remote actions, with the number of actions that wait for the agent.

## Tasks {#tasks}

What the agent is doing right now and what comes next, in time order. While the agent searches, downloads, installs (Windows updates and software), uninstalls or collects the inventory, a line marked **Running** shows the step, the progress in percent (download and installation) and the updates concerned; the page then reloads every few seconds by itself until the step ends.

The tags in the table:

- **Remote action**: a requested action that the agent runs with its next heartbeat (or runs now).
- **Update search**: the next scheduled search, with the time of the last one.
- **Inventory**: the next scheduled inventory.
- **Installation**: released updates that will be installed in the next maintenance window, with their titles; "tried again" marks an update whose last installation failed.
- **Restart**: a restart expected after the installation, or one requested by an administrator, with the reason when it waits (for example the reboot policy is "Never", a local veto, the window closes within 30 minutes). In red when Windows refused the restart the agent requested, with the exit code of the shutdown tool (for example 1722: the RPC server is unavailable); the agent tries again after 6 hours.

Heartbeats are not listed.

## Security baseline {#security}

![The Security baseline box of a server with the track of the monthly baselines](help-images/server-security.png)

In the **Security** tab. On top, the **baseline track** shows the monthly security baselines of the Windows version (the last four): each with its month and build, green "installed" or red "missing since …", the build of the server marked; the sentence below says how many baselines the server is behind, for how long, and which KB brings it to the current baseline. **Status**: the **Security baseline** ("Current", "1 behind", "2+ behind", or "Unknown" without security data from Microsoft) with the days behind and **N open CVEs →**, the **Support** phase, and what the agent found about **Extended Security Updates**: a licensed ESU key, a running Azure Arc agent (information only: Arc alone does not prove ESU, the server counts as ESU once it installs the security updates released after the end) or a virtual machine in Azure (agents from version 0.7.10). **Details**: the **Server build**, the **Current baseline** (the newest security build of the version, the month and patch day it came with) and the **Windows version**. See [Operating systems](help/operating-systems#data) for where the data comes from.

## Open vulnerabilities of the server {#vulnerabilities}

Every vulnerability fixed in a later build than the server runs: **CVE** (opens Microsoft's description), **Title**, **Severity**, **CVSS**, **Exploit** (Exploited, Disclosed) and the update that fixes it (**Fixed by**). Exploited first; search, sorting, the filters on **Severity** and **Exploit** and the pages work as described in [Working with lists](help/tables); **View** offers **All vulnerabilities** (default), **Exploited** and **Critical**. The tab shows how many are open, red when one is exploited.

## Assignment {#assignment}

The saved configuration of this server (administrators can change it; others see the effective values). Empty values inherit: from the assignment rules, the groups or the system defaults. Changes reach the agent with its next heartbeat.

**Save assignment** stores the changes; **Discard changes** reloads the saved values.

### Ring {#ring}

Which ring the server belongs to, and so when it gets updates ([Rings](help/rings)). "Automatic" lets the [assignment rules](help/assignment-rules) decide, or puts the server into the default ring when no rule matches; the current ring is shown in brackets. Choosing a ring sets it by hand: no rule changes it any more, and the server list marks it "manual".

### Agent settings {#agent-settings}

The **Agent settings** section (collapsed while the server sets none and the agent applies everything as set) lists every [agent setting](help/agent-settings) by section, in the layout of the agent settings page: the value the agent of this server gets, in a dashed field while it is inherited and a solid one when set on this server, and where it comes from ("this server", a group, the system default or the agent default).

What the agent actually applies is compared with it; agents report it with the first heartbeat after a start and whenever it changes. A grey tag **not applied yet** marks a value the agent has not received yet (it follows with the next heartbeat), an amber tag **server: …** a value the server sets itself (the registry, group policy or the agent's own configuration file wins over the central value), with the value in use; the tooltip explains it. The line after the title counts these; "not reported by the agent yet" means the agent has not reported its settings yet.

- **Edit** (pencil) opens a small dialog for that one setting: **Value** with the allowed range, and **Inherit**, which takes the value from the groups or the system default again (the field is then locked). **Save** stores just this setting at once.
- **Reset** (arrow) appears for settings set on this server and takes the inherited value again at once.

The agent applies a change with its next heartbeat; each change also appears in [Recent activity](#activity).

### Windows Automatic Updates and other Microsoft products {#update-source}

Like the reboot policy, both settings can be set for this server alone; "Inherit from groups or system" (the default) shows what currently applies.

- **Windows Automatic Updates**: "Leave unchanged, only warn" or "Keep Windows from installing on its own" (the agent turns Windows Automatic Updates off, NoAutoUpdate = 1, where no group policy controls them). See [Maintenance windows](help/maintenance-windows) for the details.
- **Other Microsoft products**: "Windows Update only" or "Microsoft Update" (the agent registers the Microsoft Update service, so SQL Server, Exchange, Office and other Microsoft products are updated through the rings too).

A value set here wins over the groups and the system default; among groups "Leave unchanged" and "Windows Update only" win. The agent picks a change up with its next heartbeat.

### Reboots {#reboots}

Whether the agent may restart the server after installing updates:

- **Reboot inside the maintenance window**: when Windows needs a restart, the agent restarts the server inside its maintenance window, if at least 30 minutes of the window are left, after a five-minute warning, at most once per six hours and never during a global pause. A local veto on the server (registry value `AllowReboot` = 0) always wins.
- **Never reboot (report only)**: the agent only reports that a reboot is pending; someone restarts the server by hand (or with "Restart server…").
- **Inherit**: the setting of the server's groups, else the system default under [Maintenance windows](help/maintenance-windows#reboots). The effective value is shown in brackets.

### Groups {#groups}

Groups the server belongs to by hand; tick them in the drop-down.

![The drop-down of the groups field with the groups to tick](help-images/server-groups-dropdown.png)
 Groups added by an [assignment rule](help/assignment-rules) are shown with "(rule)" and cannot be removed here. Groups give servers common settings (reboots, maintenance windows, Microsoft Update) and coordinate installations, see [Server groups](help/groups).

### Maintenance windows {#windows}

When the server may install updates. "Inherit from groups or the system default" uses the windows of its groups, else the system default; untick it to give the server windows of its own. The editor is described under [Maintenance windows](help/maintenance-windows#editor). **Next 30 days** shows the effective weekly windows, the one-time windows and blackouts of the server on the next 30 days and the updates on hold for it, see [Next 30 days](help/maintenance-windows#preview).

## Software windows {#software-windows}

Below the assignment (administrators): when software packages may install on this server, as on the [maintenance windows page](help/maintenance-windows#software). **Inherit from groups or the system default** uses the software windows of its groups or of the system default, or the maintenance windows when none are set; untick it for windows of the server itself, then **Save software windows**. Readers see the effective software windows in the assignment details.

## System {#system}

![The System box of a server: status rows first, then the details](help-images/server-system.png)

What the agent reported about the server, in two parts. **Status** lists what may need attention, problems first, each with its state, a short value, a grey explanation and a link on the right; **Details** lists the plain facts.

Status:

- **Restart**: whether a restart is pending and why the agent waits, with the reboot policy.
- **Outside SUSyphos**: installations of the last 90 days that Windows or an administrator started, not SUSyphos; **History →** opens the Windows Update history.
- **Automatic updates**: whether Windows installs or restarts on its own, outside rings and maintenance windows (Warning), or not (OK), with the current setting, what to do about it and **Pre-flight check →**.
- **Last update search**: OK with its time, or failed; **Details →** opens the error under missing updates.
- **Agent settings**: whether the agent applies the central agent settings as set, how many it has not received yet, or how many the server sets differently itself (registry or group policy), with the agent version; **Settings →** opens the [agent settings](#agent-settings) in the Configuration tab, where each one is marked.
- **Agent update** and **Agent version**: a failed agent update with its error, or an agent older than the minimum version (only when it applies).
- **Update source**: only while a changed setting ("set: …") waits for the agent; it applies with the next heartbeat and shows after the next inventory.
- **Move**: shown while a [move to a new server](help/backup#move) is announced and this agent could not move, with the reason; **Move to a new server →** opens the move box.
- **Support**: the support phase of the Windows version in Microsoft's lifecycle words, with the date and days left of the current phase: "Mainstream" and "Extended" in green, "Ending" in amber from 180 days before the end, after the end "ESU" in blue-violet (with its source: licensed key, Azure virtual machine, or inferred from the security updates installed after the end) or "Ended" in amber, and "Unsupported" in red once the first patch day after the end passed without its updates; "Unknown" when the shipped table does not know the version. **Security →** opens the Security tab.

Details: **Operating system** (with the edition), **Build**, **Agent** (the installed version; it opens all servers with this version), **PowerShell 7** (the version under Program Files, *not installed*, or *unknown* for agents before 0.9.0-rc.8; scripts of software packages that ask for PowerShell 7 run in Windows PowerShell 5.1 without it), **Domain**, **Roles** (detected roles such as domain controller, SQL Server, Hyper-V), **Update source** (Microsoft Update for Windows and other Microsoft products when the agent registered the Microsoft Update service, Windows Update, or WSUS with its address, a warning: the server should not depend on WSUS), **Time zone** (maintenance windows are local to the server), **Uptime** and **Registered**.
## Installed software {#software}

The programs the agent reports with its inventory ("Programs and Features" and packaged apps for the machine). The hint of the box says whether WinGet works on the server; on Windows Server 2016 and Server Core the programs are listed, but SUSyphos does not update them.

- **Program**: name and publisher as the program writes them.
- **Installed**: the installed version, in WinGet's notation where WinGet knows the program.
- **Package**: the WinGet package of the program; it opens the [package page](help/package-detail). "not known to WinGet" for programs WinGet cannot update.
- **Newest**: the newest version the catalog knows.
- **State**: *Up to date*, *Newer version available*, *Known to WinGet* (no newer version known yet), *Newer version not supported here* (amber: the newer version cannot be installed on this server; the reason stands below, see [Not in the WinGet source](help/update-detail#source)), *Newer version blocked by the virus scan* (red: the scan of the central server found a threat in its installer, so no server gets it; an administrator can **Scan again** on the page of the version), *Newer version not in the WinGet source yet* (its rollout runs, the central server still downloads and scans the installer, or the scan failed and is tried again; the reason stands below). The Software tab follows the same rule as the Updates tab: only *Newer version available* counts in the number on the tab, *Older entry, newer installed* (an uninstall entry an upgrade left behind: the same package and architecture is installed in a higher version, which is what counts; such an entry stays when the installer of the new version had to wait for a restart and could not remove the old one), *Uninstalling* (an uninstallation waits on the server) or *Not managed*.

**View** (see [Working with lists](help/tables#views)) offers **Known to WinGet** (default: programs SUSyphos can update and uninstall; the others are hidden), **Newer version available** and **All programs**.

Packaged apps (MSIX) that are installed for all users of the server are listed as well, from Windows Server 2022 on; SUSyphos installs MSIX packages that way, and uninstalling one removes it for every user of the server.

The bar above the list (administrators): **Install software…** opens [Install software](#install-software); select programs with their checkboxes (only programs WinGet knows and SUSyphos may uninstall can be selected), then **Uninstall…** opens [Uninstall](#uninstall) with them.



## Install software {#install-software}

Assigns packages of the catalog to this server without going to each package page. **Search the catalog** narrows the list to names and IDs (at most 25 are shown; installed and already assigned packages are left out; a package the server cannot install says why). **Choose** one or more packages; **Uninstall when the assignment is removed** works as on the package page; **Install now** releases the newest version for this server alone and installs it right away (restart in the maintenance window), otherwise it installs when the version is released for the ring of the server. **Assign to** the server stores the assignments (audit log); **Close** closes the dialog. Packages that are not in the catalog yet are added on the Software page first.

## Assigned, not installed yet {#assigned}

Packages assigned to this server, one of its groups or all servers that it does not have yet: **Package**, **Version** (fixed, or the newest released), **Assigned to** and the state: *Assigned, waiting* (it installs when the version is released for the ring) or *Not supported here* with the reason.

## Missing updates {#missing}

Updates the last search of the agent found missing, Windows updates and software (marked *Software*: newer versions of programs WinGet manages). Where the title carries a KB number, the KB links to Microsoft's release notes and **Catalog** to the Microsoft Update Catalog (also in both histories). **Status** (see [Deployment states](help/overview#states)): *Waiting for release* (not released to the server's ring yet), *Ready to install* (released, waits for the maintenance window), *Failed* (last installation failed), *Restart pending* (installed, Windows reports it missing until the restart that completes it; the agent does not install it again meanwhile); "on hold for …" below it names a hold of this server, its ring or a group. An update installed before and reported missing again (uninstalled) is *Ready to install* again with the note "Missing again after the installation of …". **Earliest installation** is the time the release allows the installation from (in the past: in the next maintenance window); **Result** shows the error code of a failed installation with a plain explanation.

![Missing updates with one update selected and the bar of actions above the list](help-images/server-update-selection.png)

**Select** the updates with the checkbox in front of each row (**Select all** in the header ticks every row shown, see [Selecting rows](help/tables#select)), then choose an action in the bar above the list. With the dashboard script the bar counts the selection and its buttons wait for one. Each action opens a short confirmation that lists what it applies to and, below **Not to …**, what it skips and why:

- **Install now…** (administrators): opens [Install selected updates now](#install-selection).
- **Hold…** (operators and administrators): opens [Hold for this server](#hold-selection).
- **End hold…** (operators and administrators): ends the holds of this server for the selected updates; holds of the ring or a group are ended on the update page.

## Installed updates {#installed}

What can be uninstalled on this server: the installed Windows updates (Defender signatures and its platform, the Malicious Software Removal Tool and cumulative updates for .NET Framework are left out, Windows Update cannot remove them) and the programs WinGet knows (marked *Software*), newest installation first. **Can be uninstalled**: *yes*, *only through DISM, may fail* (Windows Update reports it as not uninstallable; the agent tries DISM by the KB number) or why not (for example "Its uninstallation already waits on the server."). Administrators see the checkboxes:

- **Hold…**: holds the selected Windows updates for this server, so they are not installed again ([Hold for this server](#hold-selection)); software is kept away by ending its assignment on the package page.
- **Uninstall…** (red): opens [Uninstall](#uninstall).

## Install selected updates now {#install-selection}

Installs the selected missing updates with the next heartbeat, also outside the maintenance window and before their planned time, for example a single fix after a security incident. An update that is **not released for the ring yet** is released for this server alone (recorded in the audit log as Update.ReleasedForServer); the ring keeps its own date and the release ends once the server has the update. Skipped are updates on hold ("end the hold first"), updates that wait for the restart that completes them, and software the WinGet source of the central server does not offer yet or on servers where WinGet does not work.

- **Install, restart in the next window**: a needed restart waits for the next maintenance window that stays open for at least 30 minutes.
- **Install and restart right after**: a needed restart follows after a five minute warning.
- **Cancel** closes the dialog without queuing anything.

The agent installs exactly the selected updates; agents before version 0.9.0-rc.5 would install every released update, so the dialog refuses them (update the agent, or use **Install all released now…**). A global pause holds the installation, in a coordinated group Windows updates wait for a free slot (software takes no lease), and the local veto (AllowReboot) prevents the restart. The remote action lists how many updates it installs.

## Hold for this server {#hold-selection}

Holds the selected updates for this server only (like a hold of the server on the update page): the release planner does not release them to it, "Install now" skips them, they stay missing. **Reason** (optional, at most 200 characters on one line, e.g. a ticket number) and **Hold until** (optional date and time of the central server; empty: until someone ends the hold) apply to every selected update. **Hold** sets them; updates already on hold for this server are named as skipped. **End hold…** in the bar of Missing updates ends them again; the updates are then released as their ring allows. Every hold and its end are recorded in the audit log.

## Pre-flight checks {#checks}

Before each update run the agent checks: **Disk space**, **Windows Update service**, **Update source** (WSUS) and **Windows Automatic Updates**. Each is OK, Warning or Failed, with details. A failed check stops installations on this server until it is fixed. For **Windows Automatic Updates** a line "Fix:" says what to do: choose "Keep Windows from installing on its own" under [Maintenance windows](help/agent-settings#automatic-updates) (for all servers or a group of the server), or, where a group policy sets Automatic Updates, set "Configure Automatic Updates" to Disabled in that policy; Windows Update for Business deferrals are removed from the group policy. If the last update search failed, its error code and explanation are shown below the table.

## Installed by SUSyphos {#history}

Installations and uninstallations of software done by SUSyphos: when they finished, which update or program, its **Category** (classification, with a filter; programs installed, updated or removed with WinGet show *Software*), the **Operation** (*↓ Install*, light, or *↩ Uninstall*, dark and filled, so the two are told apart at a glance; an uninstallation links to the package page; *Software* is a violet tag in the category), **Started by** (always SUSyphos here), the **Result** and the **Restart**. Results and restarts are coloured tags, the same on every page: green for Succeeded, Already installed (0x80070103, nothing to do), No restart and Restarted (with the time); yellow for Succeeded with errors and Restart pending; red for Failed (with the error code and a plain explanation next to it, for software also the exit code of the installer); grey for Aborted.

Below the result of a software installation, a link per script that ran around it (**Before installation output**, **After installation output** and so on, the term *Script output* in short) opens the complete output of that run, the same page as **Show output** in [Script runs](#script-runs). The logs the agent uploads (**Download logs**) contain the outputs too, in the folder *Scripts* of the archive.

Both histories hide antivirus definition updates by default, which arrive several times a day and would bury the other installations. **View** (see [Working with lists](help/tables#views)) offers here **Without definition updates** (default), **Failed or with errors**, **Software only** and **All installations**; each list has its own view. The ⋯ at the end of a row offers **Uninstall…** for updates the server can uninstall: it opens [Uninstall an update](#uninstall) with that update chosen (administrators only).

## Script runs {#script-runs}

Shown once a script of a WinGet package ran on the server: when it finished, the **Package**, the **Phase** (with Install, Upgrade or Uninstall), the **Result** as a tag (the exit code or the reason in its tooltip) and **Show output**, which opens the [script output](help/script-output). The 50 newest runs.

## All installations on the server {#windows-history}

The Windows Update history Windows itself keeps for the last 90 days, sent with the inventory: every installation on the server, whoever started it. **Category** is the classification of the update (Security Updates, Definition Updates and so on; "Unknown" when the catalog does not know it). **Operation** (Installation or Uninstallation) and **Result** are tags as in "Installed by SUSyphos"; **Started by** tells installations by SUSyphos (green) from those started outside SUSyphos (yellow) (Windows Automatic Updates, Settings, Defender, wusa or another tool, named where Windows records it). Antivirus definition updates are grey **expected**: Defender or Windows often installs them before SUSyphos does, which is normal; they do not count as outside installations. Installations outside SUSyphos bypass rings and maintenance windows; they are a reason to check the Automatic Updates setting. Its views: **Without definition updates** (default), **Failed or with errors**, **Started outside SUSyphos**, **Definition updates only** and **All entries**.

## Recent activity {#activity}

What the agent did and reported, newest first, kept for 90 days: registration, approval, inventory, update searches, installations, remote actions, restarts, agent updates and changed agent settings the agent applies (with the values and their source). Heartbeats are not listed. The kind of each entry is a coloured pill like in the tasks: update searches, restarts and agent updates blue, installations and approvals green, remote actions amber, a rejection red, the rest grey.

## Remote actions {#remote-actions}

All actions requested for this server: when, which, by whom, the state (Queued: waits for the next heartbeat; Delivered: the agent has it; Running: **Install now** while it installs, since its result comes with the outcome, for example "2 succeeded, 0 failed" (agents from 0.9.0-rc.9); Waiting (window or lease): an uninstallation waits for its window or the lease of its group; Succeeded; Failed; Rejected: the agent refused it, for example a restart vetoed locally; Cancelled; Expired: not picked up or not confirmed in time) and details. Log uploads offer a **Download** link. A queued action can be selected with its checkbox (**Select**) and cancelled with **Cancel** (red, above the list) until the agent picks it up. **View** offers **All actions** (default), **Open** (Queued or Delivered) and **Failed** (Failed, Rejected or Expired).

## Install all released now {#install-now}

For a server that should not wait for its next maintenance window, for example after a security incident. The dialog lists the updates that are **released for the ring of the server** and still missing; updates that are not released yet are not installed (to choose single updates, also ones not released yet, select them under [Missing updates](#missing)). The agent installs them after its next heartbeat, also outside the maintenance window and before their planned time. A global pause holds the installation until it ends, and in a coordinated group the agent waits for a free slot.

**What to install** appears when the list holds both Windows updates and software (marked *Software*):

- **Windows updates and software** (default): everything listed.
- **Windows updates only**: the software waits for the maintenance window as planned.
- **Software only**: the programs are updated with WinGet now, the Windows updates wait for the maintenance window.

The list of remote actions names the choice, for example "Install now, restart right after (software only)".

- **Install and restart right after**: if Windows needs a restart afterwards, the server restarts at once after a five minute warning to signed-in users.
- **Install, restart in the next window**: a needed restart waits for the next maintenance window that stays open for at least 30 minutes, like "Restart in the next maintenance window".
- **Cancel** closes the dialog without queuing anything.

The local veto (registry value AllowReboot) still prevents the restart; the updates are installed anyway and the server shows "reboot pending". The action and its result appear under [Remote actions](#remote-actions).

## Uninstall {#uninstall}

Removes the selected installed updates and programs from this server, for example a faulty update. It opens from **Uninstall…** of [Installed updates](#installed), from the ⋯ of a row of the histories or of the Software tab (with that update or program), and from the update page for one server. The dialog lists what it uninstalls (Windows updates Windows Update cannot remove are marked *only through DISM, may fail*, programs *Software*) and, below **Not to uninstall**, what it skips and why. It warns for runtimes other programs may need (Visual C++ Redistributable, .NET, Java, WebView2 and the like).

- **When**: **In the next maintenance window** (programs alone: **In the next software window**; uninstall and restart in the next window that stays open long enough), **Now, restart in the next window** (uninstall with the next heartbeat, also outside the window; a needed restart waits for the window) or **Now, restart right after** (a needed restart follows after a five minute warning). In a coordinated group the server first waits for the lease of its group for Windows updates; software takes no lease. The remote action shows "Waiting (window or lease)" meanwhile.
- **Hold the updates for this server** (on by default when Windows updates are selected): holds them for this server, so they are not installed again after the restart; end the hold under [Missing updates](#missing). Without it the update shows as missing again with the next search and, while it stays released, is installed again in the next maintenance window. To keep an update away from all servers, block it on its update page.
- **Uninstall** queues one remote action per update or program (recorded in the audit log); **Cancel** closes the dialog without queuing anything. At most 10 actions can wait per server.

The agent uninstalls Windows updates through Windows Update, or with DISM by the KB number where Windows Update cannot; some packages cannot be removed at all, and the remote action then reports why. Programs are removed with WinGet, silently and as SYSTEM; the agent sends its inventory right after, and the uninstallation counts as done once the inventory no longer lists the program. An assignment of the package that covers the server installs it again: end it on the [package page](help/package-detail#assignments). The local veto (registry value AllowReboot) prevents the restart.

## Restart dialog {#restart}

![The restart dialog with its two choices](help-images/server-restart-dialog.png)

- **Restart now**: the agent restarts the server with its next heartbeat, also outside the maintenance window, after a five-minute warning.
- **Restart in the next maintenance window**: the agent restarts the server once a window is open for at least 30 more minutes, also if its reboot policy is "Never"; in a coordinated group it waits for a free slot. The planned time appears in the tasks and in the red banner.
- **Cancel** closes the dialog without doing anything.

A local veto on the server (`AllowReboot`) applies to both. A queued restart can be cancelled until it happens.

## Remove or delete a server {#remove}

![The dialog to remove a server](help-images/server-remove-dialog.png)

The server leaves SUSyphos: open releases, actions and installation slots are cancelled and its credentials stop working.

- **History**: **Keep the history** keeps its installations and activity in the reports; **Delete with the history** deletes the server and all its data for good, which cannot be undone.
- **Uninstall the agent** (ticked for approved servers): the agent gets the remote action "Uninstall agent (removal)" with its next heartbeat, confirms it and uninstalls its own package. The server is removed once the agent confirmed; if the agent does not answer within a day (switched off, unreachable), it is removed anyway. If the agent refuses (it was not installed with its MSI package), the server stays and the remote action says why. While SUSyphos waits, a red **Removal requested** banner shows who asked and when; **Cancel removal** in it stops the removal before the agent picked it up. Without this option a running agent registers again as a new pending server.
- **Remove server** removes the server or starts the agent uninstallation; **Cancel** closes the dialog.

A removed server that registers again (for example because its agent was offline) is never approved by an approval rule: it waits under [Approvals](help/approvals) for a manual decision. The agent's data folder (%ProgramData%\SUSyphos\Agent) stays after the uninstallation; delete it if the server will not come back.

## Debug mode {#debug}

For a while the agent collects what its normal logs leave out: it logs at Debug, WinGet writes its verbose log (for example why no installer fits the server), and **Request logs** adds WindowsUpdate.log (made with Get-WindowsUpdateLog), CBS.log and the DISM log. Choose the **Duration** (1, 4, 24 or 72 hours; 24 is preselected) and **Turn on**; the agent takes it with its next heartbeat and ends it by itself at its end time, also when it cannot reach the central server. Nothing is written to the server's settings: the agent passes WinGet's log level to its WinGet helper directly.

While it runs, a **Debug mode** banner on the server page says until when, how much time is left and who switched it on; **Extend…** opens the dialog again (a new duration starts now) and **Turn off** ends it at once. The overview lists servers in debug mode under **Needs attention**. Every change is in the audit log. Debug logs contain no secrets (client secret, passwords, tokens) either.
