# Updates {#top}

All updates that approved servers reported as missing, or that are in a rollout. Each update runs through the rings on its own ([How SUSyphos works](help/concepts#rollouts)). Click a title to open the [update details](help/update-detail). Search, grouping, sorting and column filters work as described in [Working with lists](help/tables).

![The update list with search, grouping, the "Show:" switches and the rings column](help-images/update-list.png)

The search takes a KB number, words of the title or a CVE number, in full or in part (for example CVE-2026-100201): with a CVE it lists the updates that fix it, according to Microsoft's security data (see [Operating systems](help/operating-systems#data)), and names the matching CVEs below their titles.

## Actions for selected updates {#actions}

Operators and administrators tick updates in the list (**Select**) and then use the drop-down button **Rollout** (blue) or the red button on the right:

- **Start rollout**: starts the rollout of queued or stopped updates. The update is released to the first ring and then follows the transition rules.
- **Release to all rings**: releases the updates to every ring at once, skipping wait times, rates and gates; servers install them in their next maintenance window. Meant for urgent security updates.
- **Stop rollout** (red, on the right): withdraws all releases of the updates that are not installed yet. Nothing is installed any more until the rollout is started again.

**Release rules** (administrators) opens the [start and transition rules](help/release-rules).

All actions are recorded in the update's timeline and the [audit log](help/audit-log).

## Hidden updates {#toggles}

To keep the list on what still needs attention, these kinds of updates are hidden by default. The switches after **Show:** next to the search show them, with their number; a highlighted switch is on, select it again to hide them:

- **superseded**: updates a newer known update replaces and that have no running rollout (Windows Update offers the newer one instead).
- **completed**: updates installed on every server that needs them with nothing pending, failed or queued: released to every ring (the rollout column shows "Completed"), or withdrawn by Microsoft after every server installed them.
- **withdrawn**: updates that look withdrawn by Microsoft (see [Release rules](help/release-rules#withdrawn)).
- **installed outside SUSyphos**: updates approved servers installed outside SUSyphos according to their Windows Update history (Windows Automatic Updates, Settings, wusa or another tool) that are missing nowhere and have no rollout. Antivirus definition updates are left out: Defender installing them on its own is expected. The rollout column shows "Installed outside SUSyphos" with the number of servers and who started it; the title opens the update page when the catalog knows the update. They cannot be selected for rollout actions.
- **archived**: switches to the updates no server has needed for 90 days, and back (the other switches are not offered there). An archived update returns to the list when a server reports it as missing again; it is deleted one year after archiving.

The choices are part of the address, like search and filters.

## Columns {#columns}

| Column | Meaning |
|---|---|
| **Title** | Title from Windows Update. Below it the KB number links to Microsoft's release notes, **Catalog** to the Microsoft Update Catalog; both open in a new tab. |
| **Classification** | Security Updates, Critical Updates, Updates, Update Rollups, Definition Updates, Drivers and so on. |
| **Severity** | Microsoft's severity (Critical, Important, Moderate, Low) where Microsoft gives one. |
| **Restart** | Restart behaviour as Windows Update reports it: *No restart*, *May restart*, *Restart required*, or *Unknown* (reported before agents sent it). Start rules can use it as a condition. |
| **First reported** | When a server reported it as missing for the first time. |
| **Rollout** | *Queued* (no rollout yet), *Started* (waiting for the first ring), *Rolling out* (released up to the ring named below), *Released to all rings*, *Completed* (released to every ring, nothing pending or failed; hidden by default), *Waiting at gate* (a manual gate waits for approval), *Alarm* (stopped before a ring, with the reason), *Stopped*, *Blocked* (locked by an operator, see the update page), *Withdrawn by Microsoft* (Windows Update no longer offers it) or *Superseded* (a newer update replaces it; hidden by default, "superseded by" names and links the newer update with its version). A superseded update with a running rollout keeps its state and shows "superseded by …" below it. |
| **Rings** | The stage of the update in each ring, as the ring number outlined in the colour of the ring and filled by the stage: green *Released* (done in the ring), blue *Rolling out* (servers still install it), red *Failures* or *Alarm*, amber *Awaiting approval* (a manual gate), grey *Waiting* or *Not started*. The tooltip names ring and stage; the filter offers "ring: stage", for example "Production: Awaiting approval". |
| **Status** | The ring of successful installations among all servers concerned, with "installed / total"; the tooltip adds how many servers still miss it and how many failed. The colours are explained below the list. |
