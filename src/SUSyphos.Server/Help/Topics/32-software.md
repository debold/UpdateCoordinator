# Software {#top}

Programs installed on the servers that WinGet knows, and packages added for new installations. Every version of a package is an update of its own: start rules for software decide whether it starts by itself (by default it waits for a manual start), and it runs through the rings like a Windows update. WinGet is used from Windows Server 2019 with desktop experience; on Windows Server 2016 and Server Core the servers only report their programs.

The central server reads the package index of the WinGet community source daily. A program installed on a server becomes a package of the catalog when WinGet matches it with a package, or when its product code (the name of its uninstall key) belongs to exactly one package of the index. A new version is downloaded only when its rollout starts: the installer must match the SHA-256 of the manifest and pass the virus scan of the central server before the servers get it. Until the source of the central server offers the version, its release waits: the servers get it right after the cache has it (the cache is filled every few minutes).

The band (administrators only):

- **Add package…**: opens the [dialog to add a package](#add).
- **Check for new versions now**: loads the package index anew, matches the installed programs and records new versions; normally this runs once a day. The note on the right says when Microsoft wrote the index that is loaded and how many packages it lists.
- **Release rules**: the [start rules](help/release-rules#start-rules), where the kind "Software" decides how software versions start.
- **Switch off…** (red, on the right): opens the dialog to [switch off software management](#switch).

## The certificate of the central server {#certificate}

WinGet on the servers checks the certificate of the central server like Windows does, not through the trust anchor of the agent. With a certificate of a certification authority the servers trust (for example from AD CS) nothing is needed. With the central server's own self-signed certificate WinGet refuses the source until the agent setting **Trust the central server's own certificate for WinGet** is on ([Agent settings](help/agent-settings), section Software; off by default because it changes the trusted root certificates of every server).

A banner **Certificate** below the title says so on the overview, the Software page and the package pages:

- amber, while the central server uses a self-signed certificate and the setting is off for all servers;
- red, when servers could not install software because they do not trust the certificate, with their names; the [server page](help/server-detail) shows the same banner for its server. A later successful software installation on a server clears it there.

**Agent settings** on the right of the banner opens the setting. Before WinGet starts, the agent checks the source itself and reports an untrusted certificate with its own error code (0x800B0109) and a plain explanation in the server's history.

## Switching software management on and off {#switch}

Software management with WinGet is on by default. An administrator who does not want it switches it off with **Switch off…**; the dialog explains the consequences and **Switch off** confirms (**Cancel** keeps it on). While it is off:

- the central server does not read the WinGet community source and fills no installer cache;
- its WinGet source answers nothing, so no server installs software from it;
- no software version starts, not even by a start rule;
- the agents run no WinGet and report no software (they learn it with their next release list);
- the Software page, the package pages and the Software tab of the servers are shown blurred with a notice.

The catalog, the installed programs and the cache are kept. **Switch on software management** in the notice (administrators) switches it on again; both are recorded in the audit log. Windows updates are never affected.

## Packages {#packages}

One row per package of the catalog.

- **Package**: logo, name and WinGet ID; the name opens the [package page](help/package-detail). The central server takes the logo from the manifest or the website symbol of the product or publisher page and serves it itself; without one the initials of the package stand in a circle.
- **Publisher**: as the manifest of the package names it.
- **Installed**: on how many approved servers the package is installed, with the installed versions.
- **Newest**: the newest version the community source has, and how many servers run an older one.
- **Rollout**: the state of the newest version: *Queued* (no rollout yet), *Rolling out*, *Stopped*, *Agreements not accepted* (the package carries licence terms an administrator accepts on the package page first), *Threat found* (the virus scan found a threat in the installer; the version is never offered) or *No version*.
- **Added**: "from the inventory" for packages the servers have installed, "by …" for packages an administrator added.

## Add a package {#add}

- **Search the WinGet community source**: name, ID or tag of the package, for example `7zip` or `editor`; **Search** lists up to 25 matches with their newest version.
- **Add**: takes the package with its newest version into the catalog; a package already in the catalog shows "in the catalog".

**Cancel** closes the dialog. Without a loaded package index the dialog says so; **Check for new versions now** loads it.
