# Group page {#top}

The page of one server group: who is in it, whether it is coordinated and who holds the lease, and every setting of the group, each section at full width and saved on its own. It opens from the group list, from a group name on a server page and after **Create and open**. Administrators only.

![The tiles of a group page](help-images/group-page.png)

The action band has **Next 30 days** (the windows, one-time windows and blackouts of the group on the next 30 days, with its holds), **Create assignment rule…** (the rule dialog with this group chosen) and **Delete group…** (red, on the right): members leave the group and fall back to the system defaults; assignment rules for the group are removed too.

The tiles show the number of **Members**, the **Coordination** (*Coordinated*, *Stopped* with a red edge, or *Not coordinated*), the **Lease** (the server holding it and until when, or *free*) and the **Maintenance windows** (own windows or *inherited*).

## Members {#members}

Every member with its **Ring**, how it **Joined** (*by hand* or *by rule*) and its part in the **Lease**: *holds it until …* (blue) or *waits for a lease* (amber, the agent reported that its restart waits for a free slot). **Show in the server list** opens the server list filtered to the group, where members are added and removed.

## Maintenance windows {#windows}

Two tabs as on the [maintenance windows page](help/maintenance-windows#software): **Windows updates** and **Software** (violet; "Inherit from the system default" or windows of the group, **Save software windows**). For Windows updates: "Use the system default", or untick it and enter windows of the group with the [window editor](help/maintenance-windows#editor). A server in several groups may install in the windows of all its groups together; windows of the server itself replace them. **Save windows** stores them; members get them with their next heartbeat.

## Reboots and Windows Update {#settings}

- **Reboots**: "Inherit from the system default", "Reboot inside the maintenance window" or "Never reboot (report only)". If one of a server's groups says "Never", that wins.
- **Windows Automatic Updates**: inherit, "Leave unchanged, only warn" or "Keep Windows from installing on its own" ([details](help/agent-settings#automatic-updates)). "Leave unchanged" in one group wins.
- **Other Microsoft products**: inherit, "Windows Update only" or "Microsoft Update" ([details](help/agent-settings#microsoft-update)). "Windows Update only" in one group wins.
- **Description**: free text.

**Save** stores this section.

## Coordination {#coordination}

With **Only part of the group installs and restarts at the same time** ticked, members ask the central server for an installation slot (a lease) before they install Windows updates or restart:

- **Share of members**: at most this percentage of the members at once (default 25 %).
- **At most at once**: an upper limit in servers. At least one server always gets a slot, so small groups progress too.

Software installs without a lease, and a failed package does not count for it. A slot lasts two hours and is returned with the result right after the installation, or after the restart while Windows still asks for one. "Install now" waits for a slot too. A server in several coordinated groups needs a slot in all of them, and its failures count in each; the [assignment rule dialog](help/assignment-rules#dialog) warns about it. If slots fail or expire several times in a row (two, or the number of members if fewer), the central server stops the group: no member installs until it is resumed with **Resume group** here, in the group list or under [Approvals](help/approvals#groups). **Save** stores the coordination.

## Agent settings {#agent-settings}

Every [agent setting](help/agent-settings) with the value that applies to the group and where it comes from; settings set on the group are in bold.

- **Edit** (pencil) opens a small dialog for that one setting: **Value** with the allowed range, and **Inherit**, which takes the system default again (the field is then locked). **Save** stores just this setting and returns to the page; **Cancel** goes back without a change.
- **Reset** (arrow) appears for settings set on the group and takes the inherited value again at once.

If a server is in several groups that set the same setting, the lowest number wins, for other settings the group first by name; a value set on the server wins over all groups.