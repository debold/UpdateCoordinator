# Agents {#top}

Install the SUSyphos agent on new servers and keep the agents up to date. New agent versions come from the GitHub releases of the project and reach the servers only after an administrator released them, ring by ring or for all rings at once. Operators see the page; administrators release versions.

## Check for new versions {#check}

**Check for new versions now** (administrators) asks GitHub for newer releases right away; otherwise the central server checks once a day. New agent packages are downloaded and verified against the checksums published with the release; a newer central server is only noted (see [Central server](#server)). "Last check" shows when it ran and what it found.

## Central server {#server}

The version this central server runs (**Running**, the full version with commit as tooltip) and the **Newest release** found by the check: "up to date", or the newer version with the time it was found. A newer version also appears on the overview under "Needs attention", next to the version at the bottom of the navigation, and in a notification.

The central server does not install itself. When a newer version is available:

- **Release notes** opens the release on GitHub.
- **Download SUSyphos.Server-X.Y.Z.msi** downloads the installer. Check it against SHA256SUMS.txt of the release, then run it as administrator on the central server.

The upgrade keeps database, trust anchor, settings, port and certificate. The dashboard is unavailable for about a minute while the service restarts; agents keep working and report again afterwards. Upgrade the central server before releasing agents of the same version.

## Agent versions {#versions}

Every agent package the central server has:

- **Version** and **Source**: "with central server" (shipped in the central server installer) or "GitHub" (found by the check).
- **Added**: when the package arrived.
- **Released for**: the rings the version is released to, "all rings", or "not released".
- **Servers**: how many approved servers run this version (opens the server list).

Actions (administrators):

- **Release to** a ring: releases the version to the next ring in order. Agents of the released rings update themselves to the newest version released for their ring, never to an older one.
- **Release to all rings now**: releases it everywhere at once.
- **Withdraw**: stops further updates to this version; agents that already updated keep it.

Versions older than the newest released one cannot be released any more (agents never go back to an older version); they keep only **Withdraw** while released. Older versions that no server runs are hidden; **Show older versions** lists them, **Hide older versions** hides them again.

The packages are not code-signed yet, so nothing is released automatically. An agent below the minimum version gets the package shipped with the central server even without a release, because it might not understand the central server any more.

## Install new agents {#install}

Downloads the agent installer for new servers:

1. Check the **Central server address for the agents**: the address under which the servers reach this central server, for example https://susyphos.corp.example.com:8443/. It is pre-filled with the address you opened the dashboard with.
2. **Download installer**. The file is the unchanged agent MSI; its file name carries the address and the fingerprint of the trust anchor, so the agent trusts only this central server from the very first contact.
3. Run it on the server interactively (the address is pre-filled) or silently with `msiexec /i <file>.msi /qn`. With another file name, give the address as `SERVERURL=…`.

New agents then appear under [Approvals](help/approvals) unless their domain is approved automatically. Downloads are recorded in the [audit log](help/audit-log).

## Failed agent updates {#failed}

Only shown when an agent could not update itself: the server, its current agent version and the error. The agent tries again after six hours; the msiexec log is on the server.

## Agent versions on the servers {#on-servers}

How many approved servers run each version, with its state: **current**, **older than the newest released version** (will update when the version is released for its ring) or **below the minimum version** (too old; it updates with the package shipped with the central server).
