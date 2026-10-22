# Server groups {#top}

Server groups bundle servers, for example the domain controllers or the nodes of a cluster, and give them their own maintenance windows, reboot policy and settings, and optionally coordinate their installations so that not all members install and restart at the same time. Members without group settings use the system defaults from [Maintenance windows](help/maintenance-windows). Administrators only.

A new group is empty. Servers join it in one of three ways: select them in the [server list](help/servers#actions) and use **Add to group**, tick the group on a server's [details page](help/server-detail#groups), or let an [assignment rule](help/assignment-rules) add every matching server (by role, domain or host name; it also catches servers that register later). A server can be in several groups. In the list, a group without members shows the links **add servers** and **create a rule** (the rule dialog opens with the group already chosen).

## Group list {#list}

- **New group**: opens the [group dialog](#dialog).

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

## Agent settings of a group {#agent-settings}

The collapsed **Agent settings** section of the dialog shows every [agent setting](help/agent-settings) with the value that applies to the group and where it comes from. Settings set on the group appear as fields; empty one (or choose "Inherit") to use the system default again. **Override** picks another setting and **Value** sets it for the group (whole numbers in the unit shown, "on"/"off", or a choice such as "Debug"). If a server is in several groups that set the same setting, the lowest number wins, for other settings the group first by name; a value set on the server wins over all groups. **Save** stores it with the rest of the group.
