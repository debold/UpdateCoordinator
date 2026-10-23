# Overview {#top}

The start page shows at a glance whether updates reach the servers, what is going wrong and what waits for a decision. All numbers count approved servers only; servers waiting for approval appear under [Approvals](help/approvals).

The time under the title ("As of …") is when the page was built, in the time zone of the central server. The page does not refresh by itself: reload it to see new numbers.

## Installation status and global pause {#pause}

The band below the title shows whether agents may install:

- **Installations allowed within maintenance windows**: normal operation. Each server installs released updates in its own maintenance window.
- **Global pause: no new installations**: an operator stopped all installations. Agents finish nothing new: an installation that is already running ends, but no new one starts, and no server is restarted for updates. Searches, inventory and heartbeats continue.
- **Blackout until …**: a blackout period from [Maintenance windows](help/maintenance-windows#blackouts) is active, with its reason. Like the pause, it stops new installations until it ends.

**Pause all installations** (operators and administrators) starts the global pause at once for every server. **Resume installations** ends it; agents continue in their next maintenance window. Both are recorded in the [audit log](help/audit-log). Use the pause when a released update turns out to be harmful and you need time to stop its rollout.

## Key figures {#figures}

Each tile opens the server list filtered to the servers it counts.

| Tile | What it counts |
|---|---|
| **Servers reporting** | Approved servers that sent a heartbeat within the last hour, of all approved servers. A server that stays silent longer is "Offline". |
| **Servers with pending updates** | Servers that still miss at least one update released for their ring. The detail line gives the number of missing installations; they install in the next maintenance window of each server. |
| **Servers with failures** | Servers whose last installation or pre-flight check failed (for example not enough disk space or a stopped Windows Update service). |
| **Servers with warnings** | Servers with a warning: update source WSUS, Windows installs updates on its own (Automatic Updates), low disk space, or a reboot that is pending. |
| **Patch compliance** | Share of approved servers that have every update released for their ring installed. |
| **Fully patched** | Share of approved servers whose update search reports no missing update at all, also none the rings still hold back. The difference to patch compliance is what the rings deliberately hold back. |
| **AD coverage** | Only when the directory was read: share of the supported server accounts in Active Directory that run an agent. Opens the [AD coverage](help/coverage) list. |

## Deployment by ring {#rings}

One row per ring, in rollout order. Click a ring to open it ([Rings](help/rings)).

- **Servers**: approved servers in the ring.
- **Active updates**: updates whose rollout currently includes this ring.
- **Status (server × update)**: one bar segment per installation state, counting each server once per active update: successful (green), pending (blue, released and not installed yet), queued (amber, not released to this ring yet) and failed (red).
- **State**: *Healthy* (nothing open), *Rolling out* (installations pending), *Failures* (at least one failed installation), *Awaiting approval* (a release gate waits for a person) or *Alarm* (the rollout stopped before this ring because a threshold was exceeded).

## Needs attention {#attention}

Everything that waits for a human decision or points to a problem, each with a button that leads to where it is resolved:

- **Alarm**: a rollout stopped before a ring, with the reason (for example too many failures in the previous ring). Open the update to look at the failures and decide.
- **Release gate**: an update waits for a manual release to the next ring.
- **Clone**: servers were blocked because the same agent credentials were used by two hosts in turn (a cloned virtual machine). Review them under [Approvals](help/approvals#blocked).
- **Registration**: new servers wait for approval.
- **Lease**: the installations of a group failed or expired several times in a row; the central server issues no new installation slots for the group until an operator resumes it.
- **Failed** and **Offline**: servers with a failed installation or without a heartbeat for over an hour.
- **Update**: a newer version of the central server is available; **Show** opens it on the [Agents](help/agents#server) page with the download.

"Nothing waits for a human decision" means the list is empty.

## Next installations {#next}

Released updates that are not installed everywhere yet, ordered by the earliest time one of the servers will install them. **Earliest installation** is the start of the next maintenance window of the first server that still needs the update, or "due now" when a window is already open. **Servers** counts the servers that still need it, **Rings** the rings they are in.

## Statistics {#statistics}

**Show** picks the statistic, **Period** the last 30, 90 or 365 days (up to 90 days one value per day, beyond that per week); with script the chart changes at once. Every mark shows its value as a tooltip, two or more series have a legend, and **Show as table** lists the same values.

- **Patch compliance and fully patched** (default): both shares per day as lines. A drop after a release is normal and closes as the servers install in their windows; a curve that stays low points to servers that never get a chance to install (no maintenance window, offline, failures).
- **Servers by status**: approved servers per day by status (OK, updating, reboot pending, warning, failed, offline) as stacked bars.
- **New updates by classification**: updates reported for the first time, stacked by classification; each classification keeps its colour.
- **Installations**: installations by SUSyphos (succeeded, failed) and outside SUSyphos from the Windows Update history (kept for 90 days).
- **Open updates: released and held back**: open server × update pairs per day, released and waiting for installation, or held back by the rings.
- **Time from release to installation**: median days from the release in a ring to the installation by SUSyphos, one line per ring.
- **Restarts**: server starts per day, those after a SUSyphos installation that needed one or a requested restart apart from the others (the activity list keeps 90 days).
- **Most frequent error codes**: the ten most frequent error codes of failed installations in the period, with their explanation.

The central server records the daily values (status, fully patched, open updates) from version 0.7.7 on, so those charts fill from then.
