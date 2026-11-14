# Package details {#top}

One package of the [software catalog](help/software): what it is, its versions and the servers it is installed on. The line under the title shows the publisher and the WinGet ID.

**Create release rule…** (administrators) opens a new [start rule for software](help/release-rules#start-rule-dialog) with the ID of this package, so new versions of it start the way you want.

## Rollout {#rollout}

How new versions of the package reach the servers, at a glance:

- **Start rule**: the start rule that decides for the newest version: "automatic" (with its delay) or "manual", with its conditions; **Edit rule →** opens it (administrators). A package whose agreements are not accepted never starts automatically, whatever the rule says. "none" when no start rule matches: new versions wait for a manual start on their update page.
- **Assignments**: how many groups or servers the package is assigned to for new installations; **Assignments →** jumps to the list. Without assignments only servers that have the package installed get its new versions.
- **Uninstall**: only after **Uninstall from servers…**, while it runs and for a week after: *uninstalling* with "N of M servers done", how many wait for their software window or the next inventory, failed or still list the package after the agent reported success, who started it and when; **Servers →** jumps to [Installed on](#installed). A server counts as done once its inventory no longer lists the package.

## Uninstall from servers {#uninstall}

**Uninstall from servers…** (administrators, red on the right of the band; greyed while no server with WinGet working has the package) removes the package with WinGet (Marc, 2026-11-14):

- **Where**: **All servers that have it** (only servers where WinGet works are counted), **Rings** or **Server groups** (with the number of servers that have it), or **One server**.
- **When**: **In the next software window** (default; a needed restart waits for a maintenance window), **Now, restart in the next window** or **Now, restart right after**. Software takes no lease of a coordinated group.
- **End the assignments**: on by default when the package is assigned; otherwise the assignments install it again with the next release.
- **I understand**: needed when more than one server is chosen.

A warning names runtimes other programs may need. **Uninstall from servers** queues a remote action on each server (recorded in the audit log); **Cancel** closes the dialog. The **Uninstall** line under [Rollout](#rollout) shows the progress, and **Installed on** marks each server as *Uninstalling* until it is done.

## About this package {#about}

The texts come from the manifest of the newest version in the WinGet community source.

- **Publisher** and **License**: as the manifest names them.
- **Links**: product page, publisher, release notes and licence, where the manifest gives an https address; they open in a new tab.
- **Tags**: keywords of the manifest.
- **Agreements** (a status row above the details): whether the installation accepts licence terms of the publisher. Such a package never starts automatically until an administrator accepts its agreements once; **Accept agreements** (administrators only, at the right of the row) does that and is recorded in the audit log. Read the terms on the publisher's page first: every server installs the package accepting them.
- **In the catalog**: whether the package came from the software inventory of the servers or was added by an administrator, and when the central server last looked for new versions.

## Versions {#versions}

Every known version, newest first. The version opens its update page, where its rollout is started, followed and stopped like that of a Windows update.

- **Released**: the release date of the manifest.
- **Rollout**: as on the [Software page](help/software#packages); *Installed everywhere* (green) when every server with the package has this version or a newer one.
- **Virus scan**: *Clean*, *Threat found* (never offered, the files are deleted), *Not scanned (failed)* (the scan failed; with the mode Enforce it is not offered; installers above the AMSI limit go through the Windows check for downloaded files) or *Not scanned* (no rollout yet, scanning switched off, or no antivirus product registered on the central server). The tooltip shows what the scanner reported.
- **In the source**: *yes* with since when the WinGet source of the central server offers the version, or *no*; the newest two versions per package and every version still rolling out stay cached.

## Assignments {#assignments}

New installations (administrators): the servers of an assigned group, or an assigned server, get the package when its version is released for their ring. Servers that have the package installed already keep their version and get new versions like every other server with the package. Servers that reported that WinGet is not supported there are left out.

- **Target**: the group or server.
- **Version**: "newest released" or the fixed version of the assignment.
- **On removal**: "keep installed" (default) or "uninstall": what happens on the servers when the assignment is removed.
- **Assigned**: by whom and when.
- **Assign package…** opens the [dialog to assign the package](#assign); **More** (⋯) has **Remove assignment…**, which opens a confirmation. **On the servers** offers **Keep installed** (the package stays and keeps getting new versions) or **Uninstall in the next software window**, preselected as chosen when the assignment was created, with the number of servers that would uninstall it and of those that keep it through another assignment. **Remove assignment** ends the assignment and, with uninstall, queues a remote action on each of those servers; **Cancel** keeps the assignment.

## Assign the package {#assign}

- **Group or server**: one group or one approved server on which WinGet can run.
- **Version**: "Newest released version", or a fixed version of the catalog.
- **Uninstall when the assignment is removed**: off by default; on, removing the assignment later uninstalls the package from the servers it covered.

**Assign** stores the assignment (recorded in the audit log); **Cancel** closes the dialog. The installation waits for the rollout of the version: start it on the version's update page, or release it to all rings at once. Holds of the version win over assignments.

## Installed on {#installed}

Approved servers that report the package installed, with the installed version, its **State** against the newest version (*up to date*, *older*, *newer than the source*, or *Uninstalling* while an uninstallation waits) and how it was matched (tag): by **WinGet** on the server, or by the **Product code** in the community index. The server name opens its Software tab.

## Remove from the catalog {#remove}

**Remove from catalog…** (administrators, red on the right of the band) stops managing a package that was added by hand: the package and its versions leave the catalog, with their rollouts and installation history. It works only without assignments and without a version rolling out; otherwise the button is greyed and its tooltip says why. A package from the inventory would come back with the next inventory. Servers that have it keep it as an installed program, unless **Also uninstall it from N servers** is ticked in the dialog (off by default): then they uninstall it with WinGet in their next software window, as with **Uninstall from servers…**. The removal is recorded in the audit log, and the package can be added again any time.
