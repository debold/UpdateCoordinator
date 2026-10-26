# Servers {#top}

All servers that registered with the central server, approved or not (deleted servers are left out). Click a server name to open its [details](help/server-detail). Search, grouping, sorting and column filters work as described in [Working with lists](help/tables).

## Actions for selected servers {#actions}

Operators and administrators see a band above the list. Tick the servers in the list (**Select**), then choose in a drop-down button:

**Updates** (blue):

- **Search for updates**: the agents search Windows Update right away instead of waiting for their next scheduled search (every 4 hours by default) and report the result.
- **Sync inventory and history**: the agents collect and send their complete inventory again (operating system, roles, installed updates, Windows Update history) and search, for example after a change on the server that the dashboard does not show yet.

**Assign** (administrators; greyed out for operators):

- **Move to ring** with the ring: the selected servers are assigned to that ring by hand; assignment rules no longer move them, their groups and maintenance windows stay. To give a server back to the rules, set its ring to "Automatic" on its [details page](help/server-detail#ring).
- **Add to group** with the group: the selected servers become members of that server group by hand (also if a rule added them before); their other groups stay. See [Server groups](help/groups).

**Refresh** loads the list again.

Actions for the agents are queued and each agent runs them with its next heartbeat (every 10 seconds while the agent is running). Agents that are offline run them when they come back. Servers that are not approved get no actions. At most 500 servers are taken per click. Every action is recorded in the [audit log](help/audit-log) and shown on the server's detail page.

## Rings {#rings}

The strip above the list shows the [rings](help/rings) in rollout order, each as a ring tag with its number of approved servers. Between two rings it shows the wait of the transition ("2–7 days", counted per update from its release in the ring before) and "manual gate" when a person must release the update to the next ring.

A ring tag switches that ring on or off in the **Ring** filter of the list: several rings can be on at once, and the tags of the rings in the filter are highlighted. The column filter of **Ring** and the chips above the list show the same filter.

## Servers of an assignment rule {#rule}

Opened from the **Servers** number of an assignment rule, the list shows only the approved servers whose role, domain and host name match that rule. The chip "Assignment rule" with its priority says so; its **Ã—** shows all servers again.

## Columns {#columns}

| Column | Meaning |
|---|---|
| **Server** | Host name, with the DNS domain below it. The filter of this column filters by domain. |
| **Operating system** | Windows version and edition, with the build number including the update revision (UBR) below it, for example 10.0.20348.4294. Sorted by build. |
| **Support** (below the operating system) | The support phase of the Windows version, shown when it needs a look: "Ending soon" (amber, from 180 days before the end of support, `Lifecycle:WarningDays`), after the end "ESU (key)", "ESU (Azure)" or "ESU, inferred from updates" (amber: Extended Security Updates found by the agent, or the server keeps getting the security updates released after the end), "Support ended" (amber: no ESU found yet) and "No ESU" (red: the first patch day after the end passed without its updates). "Supported" (mainstream support) and "Security only" (extended support) are not shown in the list. The tooltip gives the end date and the days left; the overview links to the list filtered by these phases. The dates come from a table shipped with every release. |
| **Ring** | The ring the server belongs to, as ring tag. "manual" below it: the ring was set by hand on the server's page and no assignment rule changes it. "–": no ring yet. Below it the server groups (coordination of installations, see [Server groups](help/groups)); the filter "Group" is in the chips and in "Group by". |
| **Agent** | Version of the SUSyphos agent on the server (full version in the tooltip) with a mark before it: a green check means current (no newer version released for its ring), a blue arrow up means an update is available (it installs itself), a red dash means the last update failed, and grey dots mean the version is unknown or too old to update itself. The tooltip of the mark explains it; the filter offers the versions and these states. |
| **Status** | See below. |
| **Security** | The server against the security baseline of its Windows version: "Current", "1 behind", "2+ behind" or "Unknown" (no security data from Microsoft, see [Operating systems](help/operating-systems#data)), with "Exploited" below when a vulnerability Microsoft knows to be attacked is open. The tooltip names build, baseline and the open vulnerabilities; the filter offers the states and "Exploited". The server groups now stand below the ring. |
| **Updates** | The updates of the server by state as a small ring (installed, pending, failed, queued), with the number of updates still pending next to it and the counts in the tooltip. Sorted by the pending number; its filter offers "Updates pending" and "Up to date". |
| **Last heartbeat** | Time of the last heartbeat with a dot before it: green up to 5 minutes old, amber up to an hour, red older (the server counts as offline). Its filter offers "Current", "Older" and "Long ago". |

## Status {#status}

One status per server; the first that applies wins:

- **Not approved**: registered but not (yet) approved, blocked or rejected. See [Approvals](help/approvals).
- **Offline**: no heartbeat for more than one hour.
- **Failed**: the last installation of an update the server still misses failed, or a pre-flight check failed (for example the Windows Update service is disabled). Once the update search no longer reports the update, or it was installed some other way (Windows itself, by hand), the failure no longer counts; it stays in the update history. "Already installed" (0x80070103, a driver that is already there) is no failure.
- **Updating**: the agent is downloading or installing updates right now.
- **Reboot pending**: Windows waits for a restart to finish installed updates. When a restart is announced or held for a window, the time follows below the status ("restart 2026-10-25 14:23").
- **Warning**: a pre-flight check warns (for example low disk space), the server gets its updates from WSUS, or Windows installs updates on its own (Automatic Updates).
- **OK**: none of the above.
