# Updates {#top}

All updates that approved servers reported as missing, or that are in a rollout. Each update runs through the rings on its own ([How SUSyphos works](help/concepts#rollouts)). Click a title to open the [update details](help/update-detail). Search, grouping, sorting and column filters work as described in [Working with lists](help/tables).

![The update list with search, the View and Group buttons, the filter chip of the default view and the rings column](help-images/update-list.png)

The search takes a KB number, words of the title or a CVE number, in full or in part (for example CVE-2026-100201): with a CVE it lists the updates that fix it, according to Microsoft's security data (see [Operating systems](help/operating-systems#data)), and names the matching CVEs below their titles.

## Actions for selected updates {#actions}

Operators and administrators tick updates in the list (**Select**) and then use the drop-down button **Rollout** (blue) or the red button on the right:

- **Start rollout**: starts the rollout of queued or stopped updates. The update is released to the first ring and then follows the transition rules.
- **Release to all rings**: releases the updates to every ring at once, skipping wait times, rates and gates; servers install them in their next maintenance window. Meant for urgent security updates.
- **Stop rollout** (red, on the right): withdraws all releases of the updates that are not installed yet. Nothing is installed any more until the rollout is started again.

**Release rules** (administrators) opens the [start and transition rules](help/release-rules).

All actions are recorded in the update's timeline and the [audit log](help/audit-log).

## Views {#views}

**View** (see [Working with lists](help/tables#views)) chooses what the list shows. Each view sets the **Rollout** filter (and for security updates also **Severity**); the number of updates is next to each view:

- **Open updates** (default): everything still being worked on. It hides the following rollout values, which the filter chip lists as "Rollout: not Superseded, Completed, +3".
- **Needs attention**: Alarm, Stopped, Blocked, Withdrawn by Microsoft or Waiting at gate.
- **Open security updates**: open updates rated Critical or Important.
- **Completed**: updates with nothing pending, failed or queued on any server: released to every ring, also when no approved server needs them any more (0/0, for example after the only server that missed it was removed), or withdrawn by Microsoft after every server installed them. An update uninstalled from a server is pending there again and leaves this view.
- **Superseded or withdrawn**: updates a newer known update replaces and that have no running rollout (Windows Update offers the newer one instead), and updates that look withdrawn by Microsoft (see [Release rules](help/release-rules#withdrawn)).
- **Installed outside SUSyphos**: updates approved servers installed outside SUSyphos according to their Windows Update history (Windows Automatic Updates, Settings, wusa or another tool) that are missing nowhere and have no rollout. Antivirus definition updates are left out: Defender installing them on its own is expected. The rollout column shows "Installed outside SUSyphos" with the number of servers and who started it; the title opens the update page when the catalog knows the update. They cannot be selected for rollout actions.
- **Archived**: updates no server has needed for 90 days (the rollout column shows since when). An archived update returns to the open updates when a server reports it as missing again; it is deleted one year after archiving.
- **All updates**: no filter, archived updates included.

The view is part of the address, like search and filters.

## Columns {#columns}

| Column | Meaning |
|---|---|
| **Title** | Title from Windows Update (the KB number is not repeated when the title names it). Below it two link chips: the KB number opens Microsoft's release notes (for Microsoft Defender updates, which have no support article, Microsoft's page on Defender updates), **Catalog** the Microsoft Update Catalog; both open in a new tab. |
| **Classification** | Security Updates, Critical Updates, Updates, Update Rollups, Definition Updates, Drivers and so on. |
| **Severity** | Microsoft's severity (Critical, Important, Moderate, Low) where Microsoft gives one. |
| **Restart** | Restart behaviour as Windows Update reports it: *No restart*, *May restart*, *Restart required*, or *Unknown* (reported before agents sent it). Start rules can use it as a condition. |
| **First reported** | When a server reported it as missing for the first time. |
| **Rollout** | *Queued* (no rollout yet), *Started* (waiting for the first ring), *Rolling out* (released up to the ring named below), *Released to all rings*, *Completed* (released to every ring, nothing pending or failed; hidden by default), *Waiting at gate* (a manual gate waits for approval), *Alarm* (stopped before a ring, with the reason), *Stopped*, *Blocked* (locked by an operator, see the update page), *Withdrawn by Microsoft* (Windows Update no longer offers it) or *Superseded* (a newer update replaces it; hidden by default; the details of the row name and link the newer update). A superseded update with a running rollout keeps its state. |
| **Rings** | The stage of the update in each ring, as the ring number outlined in the colour of the ring and filled by the stage: green *Released* (done in the ring), blue *Rolling out* (servers still install it), red *Failures* or *Alarm*, amber *Awaiting approval* (a manual gate), grey *Waiting* or *Not started*. The tooltip names ring and stage; the filter offers "ring: stage", for example "Production: Awaiting approval". A ring outlined dashed holds the update ([Hold](help/update-detail#hold)). |
| **Status** | The ring of successful installations among all servers concerned, with "installed / total"; the tooltip adds how many servers still miss it and how many failed. The colours are explained below the list. |
| **Details** (▾ at the end of the row) | Opens a row below with the links, the newer update that supersedes it, the vulnerabilities it fixes, the servers that still miss it and the update ID with its revision; select it again to close the row. |
