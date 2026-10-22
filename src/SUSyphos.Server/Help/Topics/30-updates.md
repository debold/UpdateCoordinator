# Updates {#top}

All updates that approved servers reported as missing, or that are in a rollout. Each update runs through the rings on its own ([How SUSyphos works](help/concepts#rollouts)). Click a title to open the [update details](help/update-detail). Search, grouping, sorting and column filters work as described in [Working with lists](help/tables).

## Actions for selected updates {#actions}

Operators and administrators tick updates in the list (**Select**) and then use:

- **Start rollout**: starts the rollout of queued or stopped updates. The update is released to the first ring and then follows the transition rules.
- **Release to all rings**: releases the updates to every ring at once, skipping wait times, rates and gates; servers install them in their next maintenance window. Meant for urgent security updates.
- **Stop rollout**: withdraws all releases of the updates that are not installed yet. Nothing is installed any more until the rollout is started again.

The other buttons:

- **Release rules** (administrators): opens the [start and transition rules](help/release-rules).
- **Show archived updates** / **Show current updates**: switches to the updates no server has needed for 90 days, and back. An archived update returns to the list when a server reports it as missing again; it is deleted one year after archiving.

All actions are recorded in the update's timeline and the [audit log](help/audit-log).

## Superseded and completed updates {#toggles}

To keep the list on what still needs attention, two kinds of updates are hidden by default; the buttons next to the search show them, with their number:

- **Show superseded** / **Hide superseded**: updates a newer known update replaces and that have no running rollout (Windows Update offers the newer one instead).
- **Show completed** / **Hide completed**: updates released to every ring with nothing pending, failed or queued on any server; the rollout column shows "Completed".

Both choices are part of the address, like search and filters.

## Columns {#columns}

| Column | Meaning |
|---|---|
| **Title** | Title from Windows Update; the KB number follows when it is not part of the title. |
| **Classification** | Security Updates, Critical Updates, Updates, Update Rollups, Definition Updates, Drivers and so on. |
| **Severity** | Microsoft's severity (Critical, Important, Moderate, Low) where Microsoft gives one. |
| **Restart** | Restart behaviour as Windows Update reports it: *No restart*, *May restart*, *Restart required*, or *Unknown* (reported before agents sent it). Start rules can use it as a condition. |
| **First reported** | When a server reported it as missing for the first time. |
| **Rollout** | *Queued* (no rollout yet), *Started* (waiting for the first ring), *Rolling out* (released up to the ring named below), *Released to all rings*, *Completed* (released to every ring, nothing pending or failed; hidden by default), *Waiting at gate* (a manual gate waits for approval), *Alarm* (stopped before a ring, with the reason), *Stopped*, *Blocked* (locked by an operator, see the update page), *Withdrawn by Microsoft* (Windows Update no longer offers it) or *Superseded* (a newer update replaces it; hidden by default, "superseded by" names and links the newer update with its version). A superseded update with a running rollout keeps its state and shows "superseded by …" below it. |
| **Status** | The ring of successful installations among all servers concerned, with "installed / total"; the tooltip adds how many servers still miss it and how many failed. The colours are explained below the list. |
