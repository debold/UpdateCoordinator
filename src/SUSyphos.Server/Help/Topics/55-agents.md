# Agents {#top}

Install the SUSyphos agent on new servers and keep the agents up to date. New agent versions come from the GitHub releases of the project and reach the servers only after an administrator released them, ring by ring or for all rings at once. Operators see the page; administrators release versions.

## Check for new versions {#check}

**Check for new versions now** (administrators) asks GitHub for newer agent releases right away; otherwise the central server checks once a day. Downloaded packages are verified against the checksums published with the release. "Last check" shows when it ran and what it found.

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
