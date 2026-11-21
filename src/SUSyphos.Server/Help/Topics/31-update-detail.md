# Update details {#top}

One update with its rollout through the rings and its state on every server. The line under the title shows classification, severity, revision, restart behaviour and when it was first reported; the tag next to the title shows the rollout state as in the [update list](help/updates#columns). **Release notes** opens Microsoft's support article of the KB in a new tab, **Catalog** the KB in the Microsoft Update Catalog (packages for download).

![Progress by ring and all servers on the overview tab of an update](help-images/update-progress.png)

The boxes are split into three tabs: **Overview** (start rule, progress by ring and the state of all servers), **Servers** (every server with its state, and installations outside SUSyphos; the tab shows the number of failed installations) and **Timeline**. The tab is part of the address.

## Blocked, withdrawn and superseded {#block}

Lasting states appear as banners below the title:

- **Blocked** (red): by whom and since when; **Unblock** on the right (operators) lifts the lock and leaves the rollout stopped; resume it when the update should be installed again.
- **Withdrawn** (red): probably withdrawn by Microsoft. Servers that had it released report that Windows Update no longer knows it, it is not installed on them (inventory, SUSyphos or the Windows Update history), and no newer update, revision or newer version with the same KB replaces it. Depending on the setting under [Release rules](help/release-rules#withdrawn) the rollout was stopped or only a notification sent. **Block** on the right keeps it from coming back. The banner goes away when that turns out wrong (a server gets the update offered again, has it installed, or a newer version appears), and a rollout the mark stopped resumes by itself; a rollout stopped by hand stays stopped.
- **Superseded** (blue): a newer update replaces this one (Windows Update supersedence). Each ring then gets the newer update as soon as it is released there: servers that get the newer one no longer get this one, the others keep it. Once the newer update has reached every ring this one reached, this rollout stops by itself ("Rollout stopped: superseded by …" in the timeline).

## Not in the WinGet source {#source}

A software version reaches the servers only once the WinGet source of the central server offers it: its installer downloaded, checked against the SHA-256 of the manifest and scanned for viruses (Marc, 2026-11-20). Until then a red or amber banner says why, also after **Release to all rings**: *download and virus scan pending*, *the virus scan of the installer failed* (with the reason and the last attempt; the central server tries again every few minutes while the rollout runs and releases the version as soon as a scan succeeds) or *a virus scan found a threat* (block the version). **Open package** opens the package page with the scan state of every version. The update list shows the same reason below the rollout state.

**Not supported on some servers** lists servers that would need the version but cannot install it, grouped by the reason, so it is not released to them instead of failing there (Marc, 2026-11-21): a minimum Windows version above the server's, a portable package (also inside a ZIP) on Windows Server 2016 or 2019 (WinGet allows portable packages only from Windows 10.0.18362 on), an installer that refuses administrator rights (the agent installs as SYSTEM), or only MSIX or Microsoft Store installers. The Software tab of the server shows such a program as *Newer version not supported here* with the same reason.

## Actions for this update {#actions}

Operators and administrators:

- **Approve for** a ring: a manual gate before this ring is passed and the update is released to it.
- **Release anyway for** a ring: an alarm before this ring is overruled and the update is released to it. Look at the failures in the previous ring first.

The drop-down button **Rollout** (blue unless an approval waits):

- **Start rollout** / **Resume rollout**: starts a queued update or resumes a stopped rollout.
- **Release to all rings**: releases the update to every ring at once, skipping wait times, rates and gates.
- **Hold…**: opens the [hold dialog](#hold) to hold the update for one ring, group or server.
- **Stop rollout** (red): withdraws all releases that are not installed yet; can be resumed later.

An entry that does not fit the current state is greyed out with the reason as tooltip.

**Create release rule…** (administrators) opens a new start rule prefilled from this update.

The red drop-down button **Block** on the right:

- **Block**: stops the rollout like "Stop rollout" and locks the update: neither a start rule nor an operator starts it again until someone chooses **Unblock**. For an update known to be faulty.
- **Uninstall…** (administrators): opens the [uninstall dialog](#uninstall) to remove the update from all servers, some rings or groups, or one server.

## Hold {#hold}

**Hold…** (in the **Rollout** menu, operators and administrators) holds the update for one target while the rollout goes on elsewhere, for example when it causes trouble on one kind of server.

- **For**: a **Ring**, a **Server group** or a **Server** (the approved servers that report the update). Picking one in its list chooses that option.
- **Reason** (optional): one line that the banner and the audit log show.
- **Hold until** (optional, time of the central server): date as YYYY-MM-DD (typed or chosen in the calendar that opens below the field) and time from the 24-hour list; the hold ends by itself then. Without a date it lasts until someone selects **Resume**.
- **Hold update** sets the hold; **Cancel** closes the dialog.

While a hold lasts, the servers of the target do not get the update; where it is installed already it stays (use [Uninstall](#uninstall) to remove it). A ring on hold does not count as done, so the next ring waits as at a manual gate. The banner **On hold** at the top lists every hold with target, since when, who, the reason and the end; **Resume** lifts one, and the servers get the update again. In the update list the ring of a held update is hatched in the **Rings** column. Hold, Stop rollout and Block differ: a hold is for one target and reversible, Stop withdraws the rollout everywhere, Block keeps the update away for good.

## Uninstall {#uninstall}

**Uninstall…** (in the red **Block** menu, administrators) removes the update from the servers that report it as installed, for example a faulty one. It is greyed out for updates Windows cannot remove this way: definition updates, the Defender platform, the Malicious Software Removal Tool and cumulative updates for .NET Framework.

- **Where**: **All servers that have it** (with their number), **Rings** or **Server groups** (tick one or more; each with the number of servers that have it), or **One server**. Ticking a ring or group, or picking a server, chooses that option.
- **When**: **In the next maintenance window** (the server uninstalls and restarts in its next window that stays open long enough), **Now, restart in the next window** (uninstalls with the next heartbeat, also outside the window; a needed restart waits for the window) or **Now, restart right after** (a needed restart follows after a five minute warning).
- **Block the update**: stops its rollout and keeps it from coming back; ticked when the update goes to all servers. Already blocked updates say so.
- **Uninstall update** queues the remote actions; **Cancel** closes the dialog without changing anything.

In a coordinated group every server first waits for the lease of its group, also for "now", so the servers of a group uninstall and restart one after another; the remote action shows "Waiting (window or lease)" meanwhile. The agent uninstalls through Windows Update; where Windows Update cannot remove an update (for example combined servicing stack and cumulative packages) it uses DISM with the KB number. Some packages, such as servicing stack updates, cannot be removed at all; the remote action on that server then reports why. The local veto (registry value AllowReboot) prevents the restart.


Administrators also have **Create release rule from this update…**, which opens a new start rule pre-filled with the classification, severity and title of this update ([Release rules](help/release-rules#start-rules)).

## Start rule {#start-rule}

**Status**: how the update starts under the deciding rule: "automatic" (with its delay) or "manual"; "none" with "no start rule matches" means the update stays queued until an operator starts it. **Details**: when and by which rule (or whom) the rollout started, and which start rule decides, or would decide now, with its conditions; the rule opens in the release rules. Start rules decide only once; changing a rule later does not change running rollouts.

## Fixes vulnerabilities {#vulnerabilities}

Shown when Microsoft lists Windows Server vulnerabilities this update (by its KB) fixes: **CVE** (opens Microsoft's description), **Title**, **Severity**, **CVSS** and **Exploit** (Exploited, Disclosed), exploited first, below "Progress by ring" and "All servers". Search, sorting, the filters on **Severity** and **Exploit** and the pages work as described in [Working with lists](help/tables). The data comes from the Security Update Guide, see [Operating systems](help/operating-systems#data).

## Progress by ring {#progress}

One row per ring, with the ring tag first; it opens the Servers tab filtered to the servers of that ring.

- **State**: *Waiting* (the previous ring has not met its transition rule yet), *Released*, *Awaiting approval* (a manual gate), *Stopped before this ring* with the reason (the rollout stopped before it), or *Not started* when the rollout has not reached the ring.
- **Released**: when the update was released to the ring. For rings the rollout has not reached yet it shows the **expected** time: from the release of the last reached ring plus the minimum waits of the transitions (all servers report at once) to the same plus the maximum waits (a good success rate without full feedback). **manual approval** marks a ring that needs an operator on the way. It is an estimate: low feedback, alarms and gates change it, and rings behind an alarm or an open gate show none.
- **Feedback**: share of the targeted servers of the ring that reported a result (reported / targeted).
- **Success**: share of successful installations among the results. The last result per server counts, so a successful retry replaces a failure.
- **Status (servers)**: the servers of the ring by state, as on the overview (Marc, 2026-11-20): *Installed*, *Ready to install*, *Waiting for release* and *Failed* (see [Deployment states](help/overview#states)). Every part of the bar opens the Servers tab filtered to the ring and that state; the line below names what is open, or *everything up to date*, with a full green bar when no server of the ring needs the update.

## All servers {#all-servers}

All approved servers by their state for this update: successful, pending (released, waiting for the window or installing), queued (not released to their ring yet), failed, and not needed (the server never reported it as missing: installed otherwise or not applicable).

## Servers {#servers}

Every approved server, with the **Server** and its operating system below (the funnel filters by operating system), **Ring and groups**, **Status**, **Earliest installation**, **Installed** (when, and below it by whom: by SUSyphos, by Windows Update or another tool from the Windows Update history, or found in the inventory) and **Result** (for a failed installation the error code and an explanation). The tab names the work left: "1 failed" in red when an installation failed, else "3 to do" for the servers that still have to install it. **Show ›** on the box "All servers" and **Show all servers ›** below "Progress by ring" open it. The status:

- **Failed**: the last installation failed (the result shows the error code and an explanation).
- **Ready to install**: released and due; installs in the current or next maintenance window, or right now with **Install now**.
- **Planned**: released, but the earliest installation is still ahead (a start delay or the spread of the ring).
- **Restart pending**: installed, but Windows reports it missing until the restart that completes it; the agent does not install it again meanwhile.
- **Waiting for release**: reported as missing, not released to the server's ring yet.
- **Installed**: installed, by SUSyphos or outside it (reported by the inventory).
- **Not needed**: the server does not need it.

**View** (see [Working with lists](help/tables#views)) offers **Still to do** (default; hides Installed and Not needed), **Failed**, **Installed** and **All servers**; **Group** groups the rows by ring, status, operating system or group.

Select servers with their checkboxes, then use the bar above the list (Marc, 2026-11-20: the same for all servers of one update as for all updates of one server):

- **Install now…** (administrators): the selected servers install this update with their next heartbeat, also outside the maintenance window; choose whether they restart in the next maintenance window or right after. Where the ring of a server has not got the update yet, it is released for that server alone. Servers that have it already or are held are skipped, and the result names them.
- **Hold…** (operators and administrators): opens [Hold](#hold) with *The selected servers* preselected.
- **End hold…**: ends the holds of the selected servers for this update; holds of their ring or group stay.
- **Uninstall…** (red, administrators): opens [Uninstall](#uninstall) with *The selected servers* preselected (only those that have it installed count).

The ⋯ menu at the end of each row is gone; the server page offers the same per server.

## Installed outside SUSyphos {#outside}

Shown when approved servers installed this update outside SUSyphos according to their Windows Update history: the server (opens its Windows Update history), the date and who started it (**Started by**, for example Windows Automatic Updates, Settings or wusa). Such installations bypass rings and maintenance windows; they are a reason to check the Automatic Updates setting of those servers. For a definition update the tag is grey **expected**: Defender or Windows installing signatures before SUSyphos is normal.

## Timeline {#timeline}

Everything that happened to the rollout: start, releases per ring, gates, alarms, approvals, stops and overrides, with time and who did it (a person or the rules).
