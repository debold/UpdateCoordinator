# Package details {#top}

One package of the [software catalog](help/software): what it is, its versions and the servers it is installed on. The line under the title shows the publisher and the WinGet ID.

## About this package {#about}

The texts come from the manifest of the newest version in the WinGet community source.

- **Publisher** and **License**: as the manifest names them.
- **Links**: product page, publisher, release notes and licence, where the manifest gives an https address; they open in a new tab.
- **Tags**: keywords of the manifest.
- **Agreements**: whether the installation accepts licence terms of the publisher. Such a package never starts automatically until an administrator accepts its agreements once; **Accept agreements** (administrators only) does that and is recorded in the audit log. Read the terms on the publisher's page first: every server installs the package accepting them.
- **In the catalog**: whether the package came from the software inventory of the servers or was added by an administrator, and when the central server last looked for new versions.

## Versions {#versions}

Every known version, newest first. The version opens its update page, where its rollout is started, followed and stopped like that of a Windows update.

- **Released**: the release date of the manifest.
- **Rollout**: as on the [Software page](help/software#packages).
- **Virus scan**: *Clean*, *Threat found* (never offered, the files are deleted), *Not scanned (failed)* (the scan failed or the installer was larger than the scan limit; with the mode Enforce it is not offered) or *Not scanned* (no rollout yet, scanning switched off, or no antivirus product registered on the central server). The tooltip shows what the scanner reported.
- **In the source**: since when the WinGet source of the central server offers the version; the newest two versions per package and every version still rolling out stay cached.

## Assignments {#assignments}

New installations (administrators): the servers of an assigned group, or an assigned server, get the package when its version is released for their ring. Servers that have the package installed already keep their version and get new versions like every other server with the package. Servers that reported that WinGet is not supported there are left out.

- **Target**: the group or server.
- **Version**: "newest released" or the fixed version of the assignment.
- **Assigned**: by whom and when.
- **Assign package…** opens the [dialog to assign the package](#assign); **More** (⋯) has **Remove assignment…**, which opens a confirmation, and **Remove assignment** there ends the assignment. The package stays installed where it is; **Cancel** keeps the assignment.

## Assign the package {#assign}

- **Group or server**: one group or one approved server on which WinGet can run.
- **Version**: "Newest released version", or a fixed version of the catalog.

**Assign** stores the assignment (recorded in the audit log); **Cancel** closes the dialog. The installation waits for the rollout of the version: start it on the version's update page, or release it to all rings at once. Holds of the version win over assignments.

## Installed on {#installed}

Approved servers that report the package installed, with the installed version and how it was matched: by **WinGet** on the server, or by the **product code** in the community index. The server name opens its Software tab.
