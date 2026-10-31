# SUSyphos {#top}

The versions of SUSyphos itself: the agents on the servers and the central server, and the installer for new agents. New agent versions come from the GitHub releases of the project and reach the servers only after an administrator released them, ring by ring or for all rings at once. Operators see the page; administrators check for and release versions. The page was called Agents before; /agents still leads here.

## Check for new versions {#check}

**Check for new versions now** (administrators) asks GitHub for newer releases right away; otherwise the central server checks once a day. New agent packages are downloaded and verified against the checksums published with the release; a newer central server is only noted (see [Central server](#server)). The check also reads the release notes of each release, shown under "What's new". The text next to the button says when it ran and what it found.

## Key figures {#figures}

- **Agents on the servers**: a donut of the approved servers by agent version (the newest released version green, older ones in other colours; each opens the server list filtered to it). Once Active Directory was read (see [AD coverage](help/coverage)) it adds **No agent** (enabled server accounts with Windows Server 2016 or newer without an agent) and **Unsupported** (server accounts older than Windows Server 2016).
- **Agent version**: a pill with the state and the released version. **Update available**: a newer version was downloaded and is not released yet ("v0.7.13 → v0.7.14"). **Rolling out**: the newest version is released to some rings only. **Update failed**: agents could not update themselves (see [Failed agent updates](#failed)). **Up to date**: released to all rings. The footer says what to do or on how many servers it runs; **Show ›** opens the list of versions.
- **Central server**: Up to date or Update available, with the running and the newer version; **Show ›** opens the box [Central server](#server).

## Agent versions {#versions}

Every agent package the central server has:

- **Version**, with its source and the day it was added below: "with central server" (shipped in the central server installer) or "from GitHub" (found by the check).
- **Released for**: the rings the version is released to, as ring tags; "–" when it is not released.
- **Servers**: how many approved servers run this version (opens the server list).
- **Status**: Released, Not released, Older, in use (older than the newest released version, still on servers) or Older, unused (on no server).
- ▾ (**Details**) opens what is new in the version: the notes of its GitHub release and a link to the release.

Actions (administrators):

- **Release to** a ring: releases the version to the next ring in order. Agents of the released rings update themselves to the newest version released for their ring, never to an older one.
- **Release to all rings**: releases it everywhere at once.
- **Withdraw**: stops further updates to this version; agents that already updated keep it.

Versions older than the newest released one cannot be released any more (agents never go back to an older version); they keep only **Withdraw** while released. **View** (see [Working with lists](help/tables#views)) offers **Current versions** (default; hides older versions no server runs), **On servers** and **All versions**.

The packages are not code-signed yet, so nothing is released automatically. An agent below the minimum version gets the package shipped with the central server even without a release, because it might not understand the central server any more.

## Central server {#server}

The box shows the running version and, when the check found a newer release, the available one with a pill "Update available". A newer version also appears on the overview under "Needs attention", next to the version at the bottom of the navigation, and in a notification.

- **What's new in …** opens the notes of the release, with a link to the release on GitHub.
- **Download SUSyphos.Server-X.Y.Z.msi** downloads the installer.

The central server does not install itself. To upgrade: check the package against SHA256SUMS.txt of the release, then run it as administrator on the central server. The upgrade keeps database, trust anchor, settings, port and certificate. The dashboard is unavailable for about a minute while the service restarts; agents keep working and report again afterwards. Upgrade the central server before releasing agents of the same version.

## Install new agents {#install}

Downloads the agent installer for new servers; the pill names the agent version it contains:

1. Check the **Central server address for the agents**: the address under which the servers reach this central server, for example https://susyphos.corp.example.com:8443/. It is pre-filled with the address you opened the dashboard with.
2. **Download installer**. The file is the unchanged agent MSI; its file name carries the address and the fingerprint of the trust anchor, so the agent trusts only this central server from the very first contact.
3. Run it on the server interactively (the address is pre-filled) or silently with `msiexec /i <file>.msi /qn`. With another file name, give the address as `SERVERURL=…`.

New agents then appear under [Approvals](help/approvals) unless their domain is approved automatically. Downloads are recorded in the [audit log](help/audit-log).

## Failed agent updates {#failed}

Only shown when an agent could not update itself: the server, its current agent version and the error. The agent tries again after six hours; the msiexec log is on the server.
