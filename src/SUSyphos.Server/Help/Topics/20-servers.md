# Servers {#top}

All servers that registered with the central server, approved or not (deleted servers are left out). Click a server name to open its [details](help/server-detail). Search, grouping, sorting and column filters work as described in [Working with lists](help/tables).

## Actions for selected servers {#actions}

Operators and administrators see a band above the list. Tick the servers in the list (**Select**), then:

- **Search for updates now**: the agents search Windows Update right away instead of waiting for their next scheduled search (every 4 hours by default) and report the result.
- **Sync inventory**: the agents collect and send their complete inventory again (operating system, roles, installed updates, Windows Update history), for example after a change on the server that the dashboard does not show yet.

Administrators can also put the selected servers into a server group: choose the group next to **Add to group** and press the button. They become members by hand (also if a rule added them before); their other groups stay. See [Server groups](help/groups).

The action is queued and each agent runs it with its next heartbeat (every 10 seconds while the agent is running). Agents that are offline run it when they come back. Servers that are not approved get no actions. At most 500 servers are taken per click. Every action is recorded in the [audit log](help/audit-log) and shown on the server's detail page.

## Servers of an assignment rule {#rule}

Opened from the **Servers** number of an assignment rule, the list shows only the approved servers whose role, domain and host name match that rule. The chip "Assignment rule" with its priority says so; its **Ã—** shows all servers again.

## Columns {#columns}

| Column | Meaning |
|---|---|
| **Server** | Host name, with the DNS domain below it. The filter of this column filters by domain. |
| **Operating system** | Windows version and edition, with the build number including the update revision (UBR) below it, for example 10.0.20348.4294. Sorted by build. |
| **Ring** | The ring the server belongs to. "manual" below it: the ring was set by hand on the server's page and no assignment rule changes it. "–": no ring yet. |
| **Groups** | The groups the server belongs to (coordination of installations, see [Server groups](help/groups)). |
| **Agent** | Version of the SUSyphos agent on the server (full version in the tooltip) with a mark before it: a green check means current (no newer version released for its ring), a blue arrow up means an update is available (it installs itself), a red dash means the last update failed, and grey dots mean the version is unknown or too old to update itself. The tooltip of the mark explains it; the filter offers the versions and these states. |
| **Status** | See below. |
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
