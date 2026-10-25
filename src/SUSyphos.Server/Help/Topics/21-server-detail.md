# Server details {#top}

Everything about one server: its state, what the agent does next, its configuration, its updates and its history. Open it from the [server list](help/servers) or any link with the server name. The line under the name shows the fully qualified name, operating system, build and ring.

## Queued restart {#restart-queued}

A red banner at the top appears when a restart of the server is queued or planned: requested by an administrator (now or in the next maintenance window) or announced by the agent. It says when the restart happens or why it waits. Once the agent has announced the restart to Windows (the five-minute warning signed-in users see), the banner says **Restart announced** with the exact time; the System box, the Tasks box and the server list show that time too.

**Cancel restart** (administrators) withdraws it. If the agent has not picked up the request yet, it is simply cancelled; otherwise the agent is told to drop the restart it holds or has announced (a "Cancel restart" remote action). An announced Windows restart with its five-minute warning is aborted as well.

## Actions for this server {#actions}

Operators and administrators can send these actions. The agent picks each one up with its next heartbeat; the central server never connects to the server itself. The result appears under [Remote actions](#remote-actions).

- **Search for updates**: search Windows Update now and report the result.
- **Sync inventory**: send the complete inventory again (operating system, roles, installed updates, Windows Update history).
- **Request logs**: the agent uploads its log files compressed; when they arrived, a **Download** link appears in the remote action.
- **Restart agent service**: the SUSyphos agent service restarts itself; the server keeps running.

Everyone can use:

- **Refresh**: loads the page again with the latest state. While the agent works or an action waits to be picked up or for its result, the page also refreshes itself every 10 seconds (the note next to the button says so) and keeps its scroll position. After an action the page is loaded anew, so F5 never sends the action a second time.

Administrators also have:

- **Install updates now…**: opens the [install dialog](#install-now).
- **Uninstall update…**: opens the [uninstall dialog](#uninstall).
- **Restart server…**: opens the [restart dialog](#restart).
- **Remove server…** and **Delete with history…**: open the [remove dialog](#remove).

## Status tiles {#tiles}

- **Status**: the server status as in the [server list](help/servers#status), with the registration state and the agent version. Opens the pre-flight checks.
- **Pending updates**: released updates the server still has to install. Opens the list of missing updates.
- **Next maintenance window**: when the next window opens. Windows are in the time zone of the server; if it differs from the central server, both times are shown. "open now" means a window is open; "no window" means the server has none and installs nothing; "unknown" until the agent reports its time zone.
- **Last heartbeat**: time of the last heartbeat, with the last boot and whether a reboot is pending.

## Tasks {#tasks}

What the agent is doing right now and what comes next, in time order. While the agent searches, downloads, installs or collects the inventory, a line marked **Running** shows the step, the progress in percent (download and installation) and the updates concerned; the page then reloads every few seconds by itself until the step ends.

The tags in the table:

- **Remote action**: a requested action that the agent runs with its next heartbeat (or runs now).
- **Update search**: the next scheduled search, with the time of the last one.
- **Inventory**: the next scheduled inventory.
- **Installation**: released updates that will be installed in the next maintenance window, with their titles; "tried again" marks an update whose last installation failed.
- **Restart**: a restart expected after the installation, or one requested by an administrator, with the reason when it waits (for example the reboot policy is "Never", a local veto, the window closes within 30 minutes).

Heartbeats are not listed.

## Assignment {#assignment}

The saved configuration of this server (administrators can change it; others see the effective values). Empty values inherit: from the assignment rules, the groups or the system defaults. Changes reach the agent with its next heartbeat.

**Save assignment** stores the changes; **Discard changes** reloads the saved values.

### Ring {#ring}

Which ring the server belongs to, and so when it gets updates ([Rings](help/rings)). "Automatic" lets the [assignment rules](help/assignment-rules) decide, or puts the server into the default ring when no rule matches; the current ring is shown in brackets. Choosing a ring sets it by hand: no rule changes it any more, and the server list marks it "manual".

### Agent settings {#agent-settings}

The **Agent settings** section (collapsed while the server sets none) lists every [agent setting](help/agent-settings) with the value the agent of this server gets and where it comes from ("this server", a group, the system default or the agent default).

- **Edit** (pencil) opens a small dialog for that one setting: **Value** with the allowed range, and **Inherit**, which takes the value from the groups or the system default again (the field is then locked). **Save** stores just this setting at once.
- **Reset** (arrow) appears for settings set on this server and takes the inherited value again at once.

The agent applies a change with its next heartbeat; what it actually applies shows under [System](#applied-settings).

### Windows Automatic Updates and other Microsoft products {#update-source}

Like the reboot policy, both settings can be set for this server alone; "Inherit from groups or system" (the default) shows what currently applies.

- **Windows Automatic Updates**: "Leave unchanged, only warn" or "Keep Windows from installing on its own" (the agent sets "Configure Automatic Updates" to option 3 where no group policy controls it). See [Maintenance windows](help/maintenance-windows) for the details.
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

When the server may install updates. "Inherit from groups or the system default" uses the windows of its groups, else the system default; untick it to give the server windows of its own. The editor is described under [Maintenance windows](help/maintenance-windows#editor).

## System {#system}

What the agent reported about the server:

- **Operating system**, **Build**, **Domain**, **Roles** (detected roles such as domain controller, SQL Server, Hyper-V).
- **Update source**: what the agent reports: Microsoft Update (Windows and other Microsoft products, when the agent registered the Microsoft Update service), Windows Update, or WSUS with its address (a warning: the server should not depend on WSUS). A tag "set: …" next to it means the setting differs from what the agent reported last; it applies with the next heartbeat and shows after the next inventory.
- **Agent update**: installed agent version (opens all servers with this version), a failed agent update with its error, or "below minimum" when the agent is older than the minimum version.
- **Agent settings**: whether the agent applies the central agent settings as set, or how many differ (usually because the server sets them itself in the registry or by group policy). **Show** opens the [agent settings on the server](#applied-settings).
- **Move**: shown while a [move to a new server](help/backup#move) is announced and this agent could not move, with the reason; the link opens the move box.
- **Automatic updates**: whether Windows could install or restart on its own, outside rings and windows (problem), or not (OK), with the current setting.
- **Restart**: whether a restart is pending and why the agent waits.
- **Outside SUSyphos**: installations of the last 90 days that Windows or an administrator started, not SUSyphos (see the Windows Update history).
- **Time zone** (maintenance windows are local to the server), **Uptime**, **Last update search** (OK or failed with the error), **Registered**.

## Agent settings on the server {#applied-settings}

Opened with **Show** next to "Agent settings" in the System box. For every agent setting: the **Central value** (from this server, a group, the system default, or the agent default when nothing is set), what the **Agent applies** and the **Source on the agent**: "central" (the central value arrived), "set on the server" (the registry, group policy or the agent's own configuration file wins over the central value) or "agent default". A tag **differs** marks a value that is not the central one. Agents report this with the first heartbeat after a start and whenever it changes; "not reported" means the agent is older than 0.7.7 or has not reported yet. Each change also appears in [Recent activity](#activity). **Close** returns to the page.

## Missing updates {#missing}

Updates the last search of the agent found missing. **Status**: *Not released* (not released to the server's ring yet), *Pending* (released, waits for the maintenance window), *Failed* (last installation failed). **Earliest installation** is the next possible time; **Result** shows the error code of a failed installation with a plain explanation.

## Pre-flight checks {#checks}

Before each update run the agent checks: **Disk space**, **Windows Update service**, **Update source** (WSUS) and **Windows Automatic Updates**. Each is OK, Warning or Failed, with details. A failed check stops installations on this server until it is fixed. If the last update search failed, its error code and explanation are shown below the table.

## Update history {#history}

Installations done by SUSyphos: when they finished, which update, **Started by** (always SUSyphos here), the **Result** and the **Restart**. Results and restarts are coloured tags, the same on every page: green for Succeeded, Already installed (0x80070103, nothing to do), No restart and Restarted (with the time); yellow for Succeeded with errors and Restart pending; red for Failed (with the error code and a plain explanation next to it); grey for Aborted.

## Windows Update history {#windows-history}

The history Windows itself keeps for the last 90 days, sent with the inventory. **Operation** (Installation or Uninstallation) and **Result** are tags as in the update history; **Started by** tells installations by SUSyphos (green) from those started outside SUSyphos (yellow) (Windows Automatic Updates, Settings, Defender, wusa or another tool, named where Windows records it). Antivirus definition updates are grey **expected**: Defender or Windows often installs them before SUSyphos does, which is normal; they do not count as outside installations. Installations outside SUSyphos bypass rings and maintenance windows; they are a reason to check the Automatic Updates setting.

## Recent activity {#activity}

What the agent did and reported, newest first, kept for 90 days: registration, approval, inventory, update searches, installations, remote actions, restarts, agent updates and changed agent settings the agent applies (with the values and their source). Heartbeats are not listed.

## Remote actions {#remote-actions}

All actions requested for this server: when, which, by whom, the state (Queued: waits for the next heartbeat; Delivered: the agent has it; Succeeded; Failed; Rejected: the agent refused it, for example a restart vetoed locally; Cancelled; Expired: not picked up or not confirmed in time) and details. Log uploads offer a **Download** link. A queued action can be withdrawn with **Cancel** until the agent picks it up.

## Install updates now {#install-now}

For a server that should not wait for its next maintenance window, for example after a security incident. The dialog lists the updates that are **released for the ring of the server** and still missing; updates that are not released yet are not installed. The agent installs them after its next heartbeat, also outside the maintenance window and before their planned time. A global pause holds the installation until it ends, and in a coordinated group the agent waits for a free slot.

- **Install and restart right after**: if Windows needs a restart afterwards, the server restarts at once after a five minute warning to signed-in users.
- **Install, restart in the next window**: a needed restart waits for the next maintenance window that stays open for at least 30 minutes, like "Restart in the next maintenance window".
- **Cancel** closes the dialog without queuing anything.

The local veto (registry value AllowReboot) still prevents the restart; the updates are installed anyway and the server shows "reboot pending". The action and its result appear under [Remote actions](#remote-actions).

## Uninstall an update {#uninstall}

Removes one update from this server, for example a faulty one. **Update** lists the updates the inventory of the server reports as installed. The agent uninstalls it with its next heartbeat, also outside the maintenance window: through Windows Update, or with DISM by the KB number where Windows Update cannot. Some packages cannot be removed at all; the remote action then reports why. To keep the update away from all servers, block it on its update page ("Block and uninstall…" also uninstalls it everywhere).

- **Uninstall and restart right after**: if Windows needs a restart, the server restarts at once after a five minute warning.
- **Uninstall, restart in the next window**: a needed restart waits for the next maintenance window that stays open long enough.
- **Cancel** closes the dialog without queuing anything.

The local veto (registry value AllowReboot) prevents the restart.

## Restart dialog {#restart}

![The restart dialog with its two choices](help-images/server-restart-dialog.png)

- **Restart now**: the agent restarts the server with its next heartbeat, also outside the maintenance window, after a five-minute warning.
- **Restart in the next maintenance window**: the agent restarts the server once a window is open for at least 30 more minutes, also if its reboot policy is "Never"; in a coordinated group it waits for a free slot. The planned time appears in the tasks and in the red banner.
- **Cancel** closes the dialog without doing anything.

A local veto on the server (`AllowReboot`) applies to both. A queued restart can be cancelled until it happens.

## Remove or delete a server {#remove}

![The dialog to remove a server](help-images/server-remove-dialog.png)

- **Remove server**: the server leaves SUSyphos, its history is kept. Open releases, actions and installation slots are cancelled and its credentials stop working. If the agent still runs, it registers again and appears as a new pending server under [Approvals](help/approvals). Uninstall the agent first if the server should stay away.
- **Delete with history**: the server and all its data (history, installations, actions) are deleted for good. This cannot be undone.
- **Cancel** closes the dialog.
