# Servers {#top}

All servers that registered with the central server, approved or not (deleted servers are left out). Click a server name to open its [details](help/server-detail). Search, grouping, sorting and column filters work as described in [Working with lists](help/tables).

## Actions for selected servers {#actions}

Operators and administrators see a band above the list. Tick the servers in the list (**Select**), then:

- **Search for updates now**: the agents search Windows Update right away instead of waiting for their next scheduled search (every 4 hours by default) and report the result.
- **Sync inventory**: the agents collect and send their complete inventory again (operating system, roles, installed updates, Windows Update history), for example after a change on the server that the dashboard does not show yet.

The action is queued and each agent runs it with its next heartbeat (every 10 seconds while the agent is running). Agents that are offline run it when they come back. Servers that are not approved get no actions. At most 500 servers are taken per click. Every action is recorded in the [audit log](help/audit-log) and shown on the server's detail page.

## Columns {#columns}

| Column | Meaning |
|---|---|
| **Server** | Host name, with the DNS domain below it. The filter of this column filters by domain. |
| **Operating system** | Windows version and edition, with the build number including the update revision (UBR) below it, for example 10.0.20348.4294. Sorted by build. |
| **Ring** | The ring the server belongs to. "manual" below it: the ring was set by hand on the server's page and no assignment rule changes it. "–": no ring yet. |
| **Groups** | The groups the server belongs to (coordination of installations, see [Server groups](help/groups)). |
| **Agent** | Version of the SUSyphos agent on the server; the full version is in the tooltip. |
| **Status** | See below. |
| **Pending** | Number of released updates the server still has to install. Its filter offers "Updates pending" and "Up to date". |
| **Last seen** | Time of the last heartbeat. |

## Status {#status}

One status per server; the first that applies wins:

- **Not approved**: registered but not (yet) approved, blocked or rejected. See [Approvals](help/approvals).
- **Offline**: no heartbeat for more than one hour.
- **Failed**: the last installation of an update that is still released failed, or a pre-flight check failed (for example the Windows Update service is disabled).
- **Updating**: the agent is downloading or installing updates right now.
- **Reboot pending**: Windows waits for a restart to finish installed updates.
- **Warning**: a pre-flight check warns (for example low disk space), the server gets its updates from WSUS, or Windows installs updates on its own (Automatic Updates).
- **OK**: none of the above.
