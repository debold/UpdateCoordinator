# Groups {#top}

Groups bundle servers, for example the domain controllers or the nodes of a cluster, and give them their own maintenance windows, reboot policy and settings, and optionally coordinate their installations so that not all members install and restart at the same time. Members without group settings use the system defaults from [Maintenance windows](help/maintenance-windows). Administrators only.

Servers join a group by hand on their [details page](help/server-detail#groups) or through [assignment rules](help/assignment-rules). A server can be in several groups.

## Group list {#list}

- **New group**: opens the [group dialog](#dialog).
- **Assignment rules**: the rules that put servers into groups automatically.

The table shows each group with **Description**, number of **Members** (the name opens the server list filtered to the group), its **Maintenance windows** ("system default" if it has none), **Reboots** ("inherit" if not set) and **Coordination** (share and maximum, "Stopped" when its installations were stopped after failures, or "not coordinated").

- **Edit**: opens the group dialog.
- **Delete**: opens a confirmation. With **Delete group**, members leave the group and fall back to the system defaults; assignment rules that point to the group are removed too.

## New group and edit {#dialog}

![The dialog to create or edit a group](help-images/groups-dialog.png)

- **Name**: unique name of the group, at most 64 characters; cannot be changed later.
- **Description**: free text, for example what the servers have in common.
- **Maintenance windows**: "Use the system default", or untick it and enter windows of the group with the [window editor](help/maintenance-windows#editor). A server in several groups may install in the windows of all its groups together; windows of the server itself replace them.
- **Reboots**: "Inherit from the system default", "Reboot inside the maintenance window" or "Never reboot (report only)". If one of a server's groups says "Never", that wins.
- **Windows Automatic Updates**: inherit, "Leave unchanged, only warn" or "Keep Windows from installing on its own" ([details](help/maintenance-windows#automatic-updates)). "Leave unchanged" in one group wins.
- **Other Microsoft products**: inherit, "Windows Update only" or "Microsoft Update" ([details](help/maintenance-windows#microsoft-update)). "Windows Update only" in one group wins.

**Create group** or **Save** stores the group; **Cancel** closes the dialog. Members get the changes with their next heartbeat.

### Coordination (leases) {#coordination}

With **Only part of the group installs at the same time** ticked, members ask the central server for an installation slot (a lease) before they install or restart:

- **Share of members**: at most this percentage of the members at once (default 25 %).
- **At most at once**: an upper limit in servers (default 5). At least one server always gets a slot, so small groups progress too.

A slot lasts two hours and is returned with the result right after the installation, or after the restart when the agent restarts the server. A server in several coordinated groups needs a slot in all of them. If slots fail or expire several times in a row (two, or the number of members if fewer), the central server stops the group: no member installs until an operator resumes it under [Approvals](help/approvals#groups).
