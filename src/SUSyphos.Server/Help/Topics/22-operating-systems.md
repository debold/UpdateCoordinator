# Operating systems {#top}

The Windows versions of the approved servers: how many servers run the newest security build, which vulnerabilities are still open, how quickly the servers install a new security build, and when Microsoft ends the support of each version. The line under the title says which monthly security data from Microsoft the page uses and when it was fetched.

![The tiles of the operating systems page: security baseline, open vulnerabilities and exploited](help-images/operating-systems.png)

## Security data from Microsoft {#data}

The central server reads the security data of the last three months from the Security Update Guide of Microsoft (MSRC, `api.msrc.microsoft.com`, no account needed): once a day, and every two hours from the patch day (second Tuesday) to two days after, when Microsoft revises the data. For every vulnerability (CVE) it keeps severity, CVSS score, impact, whether it is exploited or publicly disclosed, and for every Windows Server version (2016 to 2025, Server Core included) the build and KB that fix it. A failed fetch keeps the data there is; a blue banner **Offline** appears when the fetch is switched off in the configuration (`Msrc:Enabled`, for a central server without internet access), an amber banner **Not fetched** when the last fetch failed, with the error.

**Fetch security data now** (administrators) fetches what changed right away.

The **security baseline** of a Windows version is the newest build Microsoft published security fixes for: a server on it (or later) has every published security fix. A server that misses it is "1 behind", one that misses the month before as well "2+ behind". The agent reports the build and update revision (for example 10.0.20348.5761) with every heartbeat.

## Key figures {#figures}

- **Security baseline**: servers on the baseline of their version, of all servers it can be checked for, and the share; **Show ›** opens the servers that are behind.
- **Open vulnerabilities**: distinct CVEs open on at least one server, and how many of them are critical.
- **Exploited**: CVEs Microsoft knows to be attacked that are open on a server, and on how many servers; **Show ›** opens those servers.

## Windows versions {#versions}

One row per Windows version of the approved servers:

- **Servers**: approved servers of the version; the number opens the server list filtered to it.
- **On baseline**: share of the servers on the security baseline.
- **Baseline**: the build of the newest baseline with the KB that brings it (release notes and **Catalog**) and the month.
- **Open vulnerabilities**: CVEs open on at least one server of the version, with the critical and exploited ones.
- **Patch day to installation**: median days from the release of a baseline to its installation on the servers of the version (installations by SUSyphos and in the Windows Update history).
- **Support**: the support phase of the version and its end, see [Servers](help/servers#columns).

## Open vulnerabilities {#vulnerabilities}

Every vulnerability open on at least one approved server: **CVE** (opens Microsoft's description in a new tab), **Title**, **Severity**, **CVSS** base score, **Exploit** (Exploited: attacks are known; Disclosed: published before the fix), **Servers** affected, and the update that fixes it (**Fixed by**). Exploited first, then by CVSS score. Search, sorting and filters work as described in [Working with lists](help/tables); **View** offers **All vulnerabilities** (default), **Exploited** and **Critical**.

## Patch day to installation {#duration}

The same median days as a chart, the slowest version first. Long times point to rings that wait long, maintenance windows that rarely open or servers that fail.
