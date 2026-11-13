# Server groups {#top}

Server groups bundle servers, for example the domain controllers or the nodes of a cluster, and give them their own maintenance windows, reboot policy and settings, and optionally coordinate their installations so that not all members install and restart at the same time. Members without group settings use the system defaults from [Maintenance windows](help/maintenance-windows). Administrators only.

A new group is empty. Servers join it in one of three ways: select them in the [server list](help/servers#actions) and use **Add to group**, tick the group on a server's [details page](help/server-detail#groups), or let an [assignment rule](help/assignment-rules) add every matching server (by role, domain or host name; it also catches servers that register later). A server can be in several groups. In the list, a group without members shows the links **add servers** and **create a rule** (the rule dialog opens with the group already chosen).

## Group list {#list}

- **New group**: opens the [short dialog](#dialog).

The table shows each group with its name (opens the [group page](help/group-detail)), **Description**, number of **Members** (opens the server list filtered to the group), its **Maintenance windows** and **Reboots** as tags (*inherited* when the group sets none), **Coordination** (*Coordinated* with share and maximum below, or *Not coordinated*) and the **State**: *Stopped* in red when its installations were stopped after failed leases, or which server holds the lease and until when.

- **Open** (the pencil at the end of the row): opens the group page. **More** (⋯) has **Show members** (the server list filtered to the group), **Next 30 days** (the [maintenance windows, one-time windows and blackouts](help/maintenance-windows#preview) of the group on the next 30 days, with its holds), **Resume group** for a stopped group, and:
- **Delete group…**: opens a confirmation. With **Delete group**, members leave the group and fall back to the system defaults; assignment rules that point to the group are removed too.

## New group {#dialog}

![The dialog to create a group](help-images/groups-dialog.png)

- **Name**: unique name of the group, at most 64 characters; cannot be changed later.
- **Description**: free text, for example what the servers have in common.
- **Coordinated**: only part of the group installs and restarts at the same time (25 %, at most 1 at once; changeable on the group page).

**Create and open** stores the group and opens its [page](help/group-detail), where members, windows, reboots and agent settings are set; everything starts as inherited. **Cancel** closes the dialog.

## Coordination (leases) {#coordination}

How coordination works and how it is set: see [Coordination](help/group-detail#coordination) on the group page.

## Agent settings of a group {#agent-settings}

See [Agent settings](help/group-detail#agent-settings) on the group page.