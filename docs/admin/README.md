# Admin Documentation

> Guide and information on setup and communication paths. Updated with every feature.
> Status: register, heartbeat, trust anchor, inventory, update search, installation with optional reboots inside maintenance windows, web administration with rings, rollouts, remote actions and leases, approval of new servers, and MSI installers for both components.

## 1. Components in operation

| Component | Runs as | Where |
|---|---|---|
| Central server | Windows service `SUSyphos.Server` | one central Windows server |
| Agent | Windows service `SUSyphos.Agent` | every managed Windows server, 2016 or later |

## 2. Communication paths

| From | To | Protocol | When | Purpose |
|---|---|---|---|---|
| Agent | Central server | HTTPS, `GET /api/v1/trust/binding` | first contact, after a certificate change or a local trust reset | verifies the identity of the central server (trust anchor); nothing is sent |
| Agent | Central server | HTTPS, `POST /api/v1/agents/register` | once, on first start | registration; returns client GUID and client secret |
| Agent | Central server | HTTPS, `POST /api/v1/agents/heartbeat` | every 10 s (configurable); with back-off up to 5 min while unreachable | liveness, registration state, later polling of the action queue |
| Agent | Central server | HTTPS, `POST /api/v1/agents/inventory` | after registration, on start, when changed, at least daily | OS, domain, roles, update source, installed updates |
| Agent | Central server | HTTPS, `POST /api/v1/agents/updates` | every 4 h (approved agents) | pre-flight checks and available updates |
| Agent | Central server | HTTPS, `POST /api/v1/agents/activity` | while a search, download, installation or inventory runs: on change (at most every heartbeat interval), at least every 30 s, and once when it ended; idle agents send nothing | live status (step, progress, updates); counts as a sign of life |
| Agent | Central server | HTTPS, `GET /api/v1/agents/releases` | when the release version in the heartbeat changes (approved agents) | released updates with earliest installation time |
| Agent | Central server | HTTPS, `POST /api/v1/agents/installations` | after each installation run, or later if the central server was unreachable | result per update, reboot needed |
| Agent | Central server | HTTPS, `GET /api/v1/agents/package/{version}` | when the heartbeat announces a released newer agent version | download of the agent package for the self-update (section 22) |
| Central server | Backup folder (local disk or SMB share) | file system / SMB, as LocalSystem (computer account) | daily at the configured time, if the scheduled backup is on | encrypted backup archives (section 19) |
| Central server | GitHub (`api.github.com`, `github.com` and its download storage) | HTTPS | daily and on "Check for new versions now" (`AgentPackages:CheckEnabled`) | new agent versions of the project; downloaded and verified, never released automatically |
| Agent | Windows Update (or WSUS) | HTTPS, via the Windows Update Agent API | during the update search | search for available updates; the agent itself contacts no other internet address |
| Admin browser | Central server | HTTPS | on demand | dashboard and administration |
| Central server | SMTP server | SMTP with STARTTLS (587), TLS (465) or plain (internal relay) | when new events occur (checked every minute) | e-mail notifications (section 20) |
| Central server | Webhook receiver | HTTPS `POST`, no redirects | when new events occur | JSON notifications, optionally HMAC-signed (section 20) |
| Monitoring | Central server | HTTPS `GET /health` | as needed | liveness check, no authentication, no payload |

The central server **never** opens a connection to an agent. No inbound port is required on the managed servers. On the central server, allow inbound TCP **8443** (default HTTPS port) from the managed servers.

Agents authenticate every request except registration and the trust binding with HTTP Basic authentication (client GUID and client secret). The registration endpoint is anonymous and therefore rate limited per IP address.

## 3. TLS certificate of the central server

The central server only listens on HTTPS. Its certificate comes from the Windows certificate store, so no certificate password is stored in configuration. Agents never rely on a CA: they accept exactly the certificate the trust anchor has signed, so every certificate change below is followed by all agents automatically on their next contact.

**First start.** With `CERTTHUMBPRINT` (or a selection in the dashboard) the central server uses that certificate. Without one it creates a self-signed certificate for itself:

- CN is the fully qualified name of the machine, the SAN contains the fully qualified name and the host name.
- RSA 3072 key in the machine key store, not exportable; friendly name `SUSyphos self-signed`; stored in `LocalMachine\My`.
- Valid for 180 days (`Certificate:SelfSignedLifetime`). After a restart the existing certificate is reused.

Browsers do not trust a self-signed certificate and warn on every rollover. For the dashboard a certificate from an internal CA is recommended (step by step below); agents work with either.

**Using a CA certificate.**

1. Enroll a server authentication certificate with private key into `LocalMachine\My`, ideally through autoenrollment. LocalSystem (the service account) can read machine keys.
2. Select it under Administration → TLS certificate (role `Admin`, with confirmation), or set it at installation with `CERTTHUMBPRINT`. The selection is stored in `HKLM\SOFTWARE\SUSyphos\Server\CertificateThumbprint`; `Trust:CertificateThumbprint` in the configuration works as well.
3. The central server presents the new certificate at once (no restart) and signs a new trust binding for it (next serial).

Selecting the certificate is its approval; the change is recorded in the audit log.

**Renewal and rollover.** Every 15 minutes (`Certificate:CheckInterval`) the central server checks the store:

| Situation | Reaction | Audit entry |
|---|---|---|
| A renewal of the current certificate appears: same subject and issuer, at least the same DNS names, later expiry (autoenrollment or a manual renewal at the CA) | switches to it without restart, stores its thumbprint, signs a new binding | `Certificate.Renewed` |
| The own self-signed certificate expires within 30 days (`Certificate:RenewBefore`) | creates a new one with a new key and switches to it | `Certificate.RolledOver` |
| A CA certificate expires within 30 days and no renewal is in the store | warning in the log once a day; the certificate stays in use | none |
| At start the configured certificate is missing | error in the log, the own self-signed certificate is used; the setting is kept | `Certificate.FallBack` |

A renewal that happened while the service was stopped is found at the next start (also when Windows archived the old certificate). An expired CA certificate stays in use, since agents pin its key and keep working; browsers warn until a valid certificate is selected. A foreign self-signed certificate with the same subject is never taken over automatically, it must be selected in the dashboard.

## 4. Configuration

Both services use the standard .NET configuration: `appsettings.json` next to the executable, environment variables (`__` as section separator, e.g. `Database__Path`) and command-line arguments.

### Central server

| Key | Default | Meaning |
|---|---|---|
| `Kestrel:Endpoints:Https:Url` | `https://*:8443` | HTTPS endpoint for agents and administration |
| `Trust:CertificateThumbprint` | none | thumbprint of the TLS certificate in `LocalMachine\My`; without it the central server uses its own self-signed certificate (section 3). In development `CurrentUser\My` or the ASP.NET Core development certificate. |
| `Certificate:SelfSignedLifetime` | `180.00:00:00` | validity of the own self-signed certificate (7 days to 10 years); shorter is possible since the rollover is automatic |
| `Certificate:RenewBefore` | `30.00:00:00` | rollover of the own certificate and expiry warning for a CA certificate this long before expiry (at most half the lifetime) |
| `Certificate:CheckInterval` | `00:15:00` | how often the store is checked for a renewed certificate |
| `Trust:AnchorPath` | `%ProgramData%\SUSyphos\Server\trust-anchor.key` | private key of the trust anchor, DPAPI-protected for the service account; **include in backups** |
| `Database:Path` | `%ProgramData%\SUSyphos\Server\server.db` | SQLite database file; the folder is created and the schema migrated on startup |
| `Agents:RateLimit:RegisterPermitLimit` | `10` | registrations allowed per IP address and window |
| `Agents:RateLimit:Window` | `00:01:00` | window of the registration rate limit |
| `Installation:GlobalPause` | `false` | forces the global pause on (agents start no new installations; check, search and reporting continue), independent of the switch on the overview page; read at runtime. Normally the pause is switched in the dashboard. |

### Agent

Agent settings are normally set in the registry (string values):

| Registry key | Written by | Priority |
|---|---|---|
| `HKLM\SOFTWARE\Policies\SUSyphos\Agent` | group policy | highest: overrides every other setting |
| `HKLM\SOFTWARE\SUSyphos\Agent` | installer (`SERVERURL=https://…`, later also from the download file name) | overrides `appsettings.json`, environment and command line |

| Registry value | Meaning |
|---|---|
| `CentralServerUrl` | address of the central server, must be `https://` |
| `TrustResetToken` | local trust reset: every change of this value makes the agent forget the pinned trust anchor once (see section 7) |

Registry values are read when the agent service starts. The product name in the key paths is a placeholder until the project name is decided.

| Key | Default | Meaning |
|---|---|---|
| `CentralServer:BaseAddress` | none (required) | address of the central server; set via `CentralServerUrl` |
| `CentralServer:HeartbeatInterval` | `00:00:10` | interval between heartbeats (1 s to 10 min) |
| `CentralServer:MaxBackoff` | `00:05:00` | longest wait between attempts while the central server is unreachable |
| `Resources:MaxPrivateMemoryMegabytes` | `200` | private memory limit (64 to 4096 MB): above it, after a full garbage collection, the agent logs an error and restarts its service through service recovery. Measured: about 30 MB idle in a test process, about 70 MB while packing 80 MB of logs for upload; the CI install test checks the real agent service after registration and inventory |
| `Resources:LowPriority` | `true` | runs the agent process with below-normal priority. The Windows Update search and installation run in the Windows Update service, not in the agent, so their CPU use is governed by Windows. |

| `Inventory:CollectInterval` | `06:00:00` | how often the inventory is collected (5 min to 1 day) |
| `Inventory:MaxSendInterval` | `1.00:00:00` | the inventory is sent at least this often, otherwise only when it changed (1 h to 7 days) |
| `Inventory:RoleRules` | none | additional role rules (see below) |
| `Updates:SearchInterval` | `04:00:00` | how often pre-flight check, update search and report run on approved agents (1 h to 1 day) |
| `Updates:MinFreeDiskSpaceMegabytes` | `4096` | free space required on the system drive; below it no search or installation runs |
| `Updates:Source` | `System` | `System`: search where the server is configured (Windows Update, or WSUS by policy); `WindowsUpdate`: always Windows Update directly |
| `Installation:Enabled` | `true` | `false` disables installations on this server; search and reporting continue |
| `Installation:Windows` | system default | maintenance windows in local server time; default Mon–Fri 19:00–05:00 and Sat/Sun all day (see below) |
| `Installation:PullForwardTolerance` | `00:30:00` | releases due within this time join a batch that is already due (0 to 4 h) |
| `Installation:RetryDelay` | `06:00:00` | an attempted update is not retried before this time (1 h to 7 days) |
| `AgentUpdate:Enabled` | `true` | `false` switches the self-update off on this server |
| `AgentUpdate:RetryDelay` | `06:00:00` | a failed self-update of one version is tried again after this time (10 min to 7 days) |
| `AgentUpdate:MaxPackageSize` | `524288000` | largest agent package accepted (bytes) |
| `AgentUpdate:InstallTimeout` | `00:30:00` | how long msiexec may run before the attempt counts as failed |

Invalid configuration stops the service at startup with a clear error in the event log instead of running with wrong settings.

#### Additional role rules

Built-in roles: `DomainController`, `Exchange`, `SqlServer`, `ClusterNode`, `HyperVHost` (details in `docs/05-agent-design.md`). Further roles are added in `appsettings.json` of the agent, for example:

```json
{
  "Inventory": {
    "RoleRules": [
      {
        "Role": "Custom.BackupServer",
        "AnyOf": [
          { "Kind": "ServiceInstalled", "Path": "VeeamBackupSvc" },
          { "Kind": "RegistryValue", "Path": "SOFTWARE\\Contoso\\Backup", "ValueName": "Role", "EqualTo": "Server" }
        ]
      }
    ]
  }
}
```

`Kind` is `RegistryKeyExists`, `RegistryValue` or `ServiceInstalled`; registry paths are relative to `HKLM` (without hive). A rule with the name of a built-in role adds conditions to it. Role names: letters, digits, `.` and `-`, starting with a letter; prefix custom roles with `Custom.`.

### Logging (both components)

Each component writes rolling log files for troubleshooting; warnings and errors also go to the Windows event log (source: service name).

| Key | Default | Meaning |
|---|---|---|
| `Logging:File:Directory` | `%ProgramData%\SUSyphos\Agent\Logs` or `...\Server\Logs` | folder of the log files (`agent-YYYYMMDD.log`, `server-YYYYMMDD.log`) |
| `Logging:File:MinimumLevel` | `Information` | lowest level in the file (`Trace`, `Debug`, `Information`, `Warning`, `Error`) |
| `Logging:File:RetainedFileCountLimit` | `31` | number of files kept; older files are deleted |
| `Logging:File:FileSizeLimitBytes` | `10485760` (10 MB) | size at which a new file is started within the same day |

A new file is started every day and when the size limit is reached. Client secrets and keys are never written to the logs. For a detailed trace, set `Logging:File:MinimumLevel` to `Debug` (environment variable `Logging__File__MinimumLevel=Debug`) and restart the service. The HTTP requests of every heartbeat (`System.Net.Http.HttpClient`) are logged only from level `Warning`; set `Logging:LogLevel:System.Net.Http.HttpClient` to `Information` to see them.

## 5. Data on disk

| Component | Path | Content |
|---|---|---|
| Central server | `Database:Path` | SQLite database. Client secrets are stored only as SHA-256 hashes. |
| Central server | `Trust:AnchorPath` | private key of the trust anchor, DPAPI-protected for the service account |
| Agent | `%ProgramData%\SUSyphos\Agent\identity.dat` | client GUID and client secret, DPAPI-protected for the service account |
| Agent | `%ProgramData%\SUSyphos\Agent\trust.dat` | pinned trust anchor, accepted certificate key and highest binding serial, DPAPI-protected |
| Agent | `%ProgramData%\SUSyphos\Agent\installations.dat` | install reports not yet delivered (at most 50), the last attempt per update and a held lease, DPAPI-protected |
| Agent | `%ProgramData%\SUSyphos\Agent\agent-update.json` | last self-update attempt (version, time, error); deleted after a successful update |
| Agent | `%ProgramData%\SUSyphos\Agent\Update` | downloaded agent package and msiexec log of the last self-update; recreated for every update with access for SYSTEM and administrators only |
| Central server | `AgentPackages:Directory` (`%ProgramData%\SUSyphos\Server\AgentPackages`) | agent packages: the one shipped with the central server and those downloaded from GitHub |
| Both | `Logging:File:Directory` | rolling log files |

If the agent identity file is deleted or unreadable, the agent registers again and appears as a new pending client. If the trust file is deleted, the next contact is a first contact again.

If the trust anchor file of the central server exists but cannot be read (e.g. different service account), the central server refuses to start instead of creating a new anchor, because a new anchor would lock out every agent.

## 6. Registration states

| State | Meaning | Agent may send |
|---|---|---|
| `Pending` | registered, waiting for approval | heartbeat (later also inventory, unregister) |
| `Approved` | approved by an admin or a rule | all messages |
| `Rejected` | rejected by an admin | nothing (`403`) |
| `Blocked` | clone suspicion, waiting for an admin | nothing (`403`) |
| `Deleted` | unregistered or soft-deleted | nothing (`403`) |

New servers are approved on the dashboard page Approvals (section 10) or by rule.

## 7. Trust between agent and central server

Details in `docs/03-communication-protocol.md`, section 3.2.

- On first contact the agent pins the trust anchor of the central server. An agent installed from the installer downloaded on the dashboard page Agents (section 22) knows the fingerprint of the anchor from the file name and pins only this central server. Otherwise install new agents in a trusted network: whoever answers the first contact gets pinned.
- A new TLS certificate is approved on the central server (section 3). Agents follow automatically, also after being offline.
- The trust anchor file is part of the central server backup. If it is lost, every agent must be reset locally.
- If an agent does not trust the central server, it sends nothing and logs "Central server not trusted" in the event log every retry.
- **Local reset on a server:** set or change the string value `TrustResetToken` (e.g. to the current date) under `HKLM\SOFTWARE\Policies\SUSyphos\Agent` by group policy or manually, then restart the agent service. The agent forgets the pinned anchor once, pins the central server again on the next contact, writes a warning to the event log and reports the reset to the central server. The same value again does not trigger another reset.

## 8. Installations

- Agents install only updates released to them by the central server, only inside a maintenance window, only if the pre-flight check passes and never during a global pause.
- **Reboots** are off by default: the agent reports a pending reboot and the server shows "reboot pending". An administrator allows reboots inside the maintenance window per system default (Administration → Maintenance windows), group (Groups) or server (server detail); among several groups "Never" wins. The agent then restarts only while a window is open for at least 30 more minutes, warns signed-in users 5 minutes ahead and requests at most one restart per 6 hours. A local veto always wins: set the string or DWORD value `AllowReboot` = `0` under `HKLM\SOFTWARE\Policies\SUSyphos\Agent` (or the installer key); only `1`/`true` allows. The development configuration of the agent forbids reboots.
- **Why a pending reboot waits:** agents from version 0.7 report their reboot decision with every heartbeat; the server detail page shows it in the System box under "Restart", e.g. "waits: the reboot policy is "Never"", "waits for the next maintenance window", "waits: the open window closes within 30 minutes", "waits for a free slot in its coordinated group" or "the agent is restarting the server".
- **Installations outside SUSyphos:** the server detail page lists the Windows Update history of the last 90 days (sent with the inventory) with who started each operation: "SUSyphos" or "outside SUSyphos" with the application Windows recorded, e.g. Windows Automatic Updates, the update orchestrator (Settings, "Check for updates") or Microsoft Defender. The System box counts these installations. If Windows installs on its own, let SUSyphos manage Automatic Updates (section 21).
- **Tasks:** the server detail page lists what the agent does next, sorted by time and tagged by kind: queued remote actions, the next update search and inventory (reported by agents from 0.7.1 with every heartbeat), the installation in the next maintenance window with the updates waiting for it, and a planned or possible restart. Heartbeats are not listed.
- **Live status:** while an agent searches, downloads, installs or collects its inventory, the "Tasks" box on the server detail page shows "Running" with the step, the progress in percent (download and installation), the updates of the batch and the start time, and reloads every 10 seconds (not after a form was submitted, so unsaved input is kept). During a download or installation the server status is "Updating" instead of an older "Reboot pending". The agent sends this between its heartbeats (`POST /api/v1/agents/activity`), see section 2.
- Example for custom maintenance windows in the agent's `appsettings.json` (Wednesday 22:00 to Thursday 04:00):

```json
{
  "Installation": {
    "Windows": [
      { "Days": [ "Wednesday" ], "Start": "22:00:00", "End": "04:00:00" }
    ]
  }
}
```

`End` not after `Start` means the window runs over midnight; an all-day window uses `"Start": "00:00:00", "End": "1.00:00:00"`. These local windows are only a fallback: as soon as the central server delivers windows with the release list (always for approved servers, see section 11), the central windows apply.
- Error codes of failed installations are translated into plain English text on the central server (log and, later, dashboard).

## 9. Web administration (dashboard)

The dashboard runs on the same HTTPS endpoint as the agent API (default `https://<central server>:8443/`). It loads no external resources; its only script is its own file `app.js`, and every page works without it. The navigation shows the SUSyphos logo and, at the bottom, the version of the central server (the full version with the commit as tooltip). Browsers show the SUSyphos icon (`/favicon.ico`, `/susyphos-icon.svg`).

**Help:** every page explains itself: the "?" next to a page title, box or dialog title opens the matching section of the built-in help in a new tab; "Help" in the navigation (or `/help`) lists all help pages. The help is served by the central server itself, needs no internet access and is also reachable before sign-in.

**First start:** as long as no account exists, the central server writes a one-time setup token to `Administration:SetupTokenPath` (default `%ProgramData%\SUSyphos\Server\setup-token.txt`). The file is readable only by SYSTEM, local administrators and the service account; its path is written to the service log and shown on the sign-in and setup pages. The interactive installer creates the token itself, shows it on its port page and opens `/setup#token=â€¦` from its last page, so the field is filled in; the token after `#` never reaches the server or its logs, and the page removes it from the address. The installer hands the token over in `HKLM\SOFTWARE\SUSyphos\Server\Setup` (readable by SYSTEM and administrators only, hidden in the installer log), which the central server deletes once the first administrator exists. `SETUPTOKEN=<32 to 128 characters: letters, digits, - and _>` sets the token for an automated installation. Open `/setup`, enter the token and create the first administrator. The token file is deleted and the setup is closed for good; a restart with existing accounts never reopens it.

**Accounts and roles:** local accounts with the hierarchical roles `Reader` (read), `Operator` (operational actions) and `Admin` (administration). Passwords need at least 12 characters (passphrases welcome, no composition rules). After 5 failed sign-ins an account is locked for 15 minutes. The session cookie is HTTPS-only, `SameSite=Strict` and expires after 8 hours without activity. Every form is protected against cross-site request forgery.

**Audit log:** sign-ins (successful, failed, locked out), sign-outs and the setup are recorded with time, actor, action, target and details (Administration → Audit log, Admin role). The log is append-only: the database rejects changes and deletions, and each entry carries a SHA-256 hash over its content and the previous entry. The audit page checks the chain on every visit and reports where it was broken if the database file was modified outside the application.

| Key | Default | Meaning |
|---|---|---|
| `Administration:SetupTokenPath` | `%ProgramData%\SUSyphos\Server\setup-token.txt` | one-time setup token file (only while no account exists) |

## 10. Approving new servers

New agents register without any secret and stay `Pending` until they are approved. Pending servers send heartbeats and their inventory but receive no releases and install nothing.

**Manual:** Pending approvals (role `Operator` or `Admin`) lists host name, domain, source IP address, operating system, detected roles and times. Approve or reject; every decision is recorded in the audit log. A server can be decided only once; rejected servers cannot send anything.

**Rule-based (optional):** add DNS domains on the page Approvals, box "Automatic approval" (role `Admin`, audited), or list them in `Approval:AutoApproveDomains` (those can only be removed in the configuration). Every `Approval:CheckInterval` (default 1 minute) the central server checks each pending server of such a domain itself:

1. the computer account `HOST$` exists in Active Directory and is enabled (LDAP with signing and sealing, using the service account of the central server; the central server must be a domain member);
2. the IP address the registration came from is one of the addresses DNS returns for `host.domain`.

Only if both checks pass is the server approved, recorded in the audit log as actor `rule`. Values reported by the agent are never trusted on their own. If Active Directory cannot be reached, the server stays pending and is checked again later; servers that do not pass stay pending for a manual decision.

| Key | Default | Meaning |
|---|---|---|
| `Approval:AutoApproveDomains` | empty | DNS domains whose verified members are approved automatically, in addition to those added on the dashboard (at most 50) |
| `Approval:CheckInterval` | `00:01:00` | how often pending servers are checked (10 s to 1 h) |

## 11. Rings, groups and maintenance windows

**Rings:** every server is in exactly one ring. Out of the box the rings are `Pilot`, `Test` and `Production`. Administration → Release rules (role `Admin`) adds rings (always before the final ring, at most 10), renames them and deletes rings without servers and without assignment rules. The last ring is the **final ring**: it is deployed last, cannot be deleted and is the fallback for servers without manual or rule-based ring.

**Groups:** Administration → Groups (role `Admin`). A group bundles servers, e.g. domain controllers, with its own maintenance windows, reboot policy and coordination (leases). New and edit use the same dialog.

**Assignment rules:** Administration → Assignment rules (role `Admin`). A rule has a priority, a target (a ring or a group) and up to three conditions, all of which must match: detected roles (chosen from a list; the rule matches servers with any of the selected roles), DNS domain and host name pattern (`*` any characters, `?` one character, case-insensitive). Rules can be edited.

- Ring: a manual ring on the server wins; otherwise the matching ring rule with the lowest priority number; otherwise the final ring.
- Groups: every matching group rule adds its group; manual memberships set on the server stay until they are removed there.
- Assignment is recalculated when a server registers, after every inventory, after every change in the administration and when the central server starts.

**Maintenance windows:** Administration → Maintenance windows sets the system default; groups and single servers (server detail page) can override it. Windows are put together in a picker: weekdays, start and end in local server time of the server, with a week preview and a plain-language summary. A window whose end is not after its start runs over midnight. Each window can be limited to weeks of the month: "every week", "only in" the 1st, 2nd, 3rd, 4th and/or last week (e.g. only the 2nd Tuesday), or "all except" some of them (e.g. every Saturday except the last); the occurrence of the weekday the window starts on counts, and "last" is the last such weekday of the month. With week rules the picker lists the next openings. At most 14 windows per level. The most specific level wins: server windows, otherwise the windows of all groups of the server together, otherwise the system default. Agents pick up a change with the next heartbeat.

Every change is recorded in the audit log.

**Blackout periods** (Administration → Maintenance windows, box "Blackout periods", role `Admin`): start and end (date and 24-hour time in the time zone of the central server, at most 90 days) and reason. While one is active, the heartbeat tells all agents to start no installation, like the global pause; the overview shows it. Search, reporting and ring transitions continue. Changes are audited.

**Spread per ring** (Release rules → ring → Edit, "Spread installations over (hours)", 0 to 168): the earliest installation time of each server in the ring is the release time plus a fixed offset within the spread, derived from server and update. A spread lowers the feedback rate at the start of a ring, so allow for it in the transition's minimum wait. It applies to releases created after the change.

## 12. Rollouts (ring logic)

**When start rules apply:** when a server reports an update, when a start rule is saved, and every minute with the ring logic to all waiting updates (no rollout yet, needed by an approved server). The title pattern must match the whole title; * stands for any text, ? for one character. The rule dialog shows the matching known updates ("Preview matches"). A start rule decides only once, when the rollout starts: the update page shows the rule that started the rollout as it was then (priority, delay, rings) and, separately, the rule that would decide now; changing a rule later does not change running rollouts ("Release to all rings" does that for one update). New rules get the next free priority after the existing ones (below the catch-all rule); two rules cannot share a priority. For a manual start, delay and "Starts in" do not apply and are greyed out.

Every update runs through the rings (by default Pilot → Test → Production) on its own. A server gets an update only when it is released for the server's ring and the server reported it as missing; it installs it only inside its maintenance windows.

**Start rules** (Administration → Release rules, role `Admin`) decide how a newly reported update enters the rings. The matching rule with the lowest priority number applies; conditions are classification (e.g. `Security Updates`), MSRC severity (e.g. `Critical`) and title pattern (e.g. `*Cumulative Update*`), and all filled conditions must match. "Automatic" starts the rollout, optionally with a delay in hours before Pilot servers may install the update (the wait time for Pilot → Test starts after the delay). "Manual" keeps matching updates **queued** on the Updates page until an `Operator` starts them; updates without a matching rule stay queued as well. A rule can also release the update in further rings at once ("Starts in"), e.g. definition updates in every ring; rings not selected follow their transition rules. By default one rule without conditions starts every update automatically and without delay in the first ring. Rules are created and edited in a dialog with lists for classification and severity; "Create release rule from this update" on an update page opens it pre-filled.

**Transitions:** Administration → Release rules (role `Admin`) sets per transition the minimum and maximum wait time (days), the feedback rate (share of targeted servers that reported a result) and the success rate (share of successful results), and whether a manual gate applies.

| Transition | Minimum wait | Maximum wait | Feedback | Success | Manual gate |
|---|---|---|---|---|---|
| Pilot → Test | 2 days | 7 days | 80 % | 95 % | off |
| Test → Production | 3 days | 10 days | 80 % | 95 % | on |

After the minimum wait time the next ring is released when both rates are met. At the maximum wait time a good success rate is enough. A poor success rate, or no result at all at the maximum wait time, raises an **alarm**: nothing rolls on until an operator decides.

**Approvals** (role `Operator`) lists updates waiting at a manual gate or stopped by an alarm. Approve (or "release anyway" for an alarm) releases the update for the ring; "Stop rollout" withdraws it.

**Updates** page (role `Reader`, actions `Operator`): one table of all updates with classification, severity, rollout state, servers missing them and a status donut; filter, sort and page through it, and start, stop or "Release to all rings" for the selected updates (manual override, e.g. for a critical security update; servers still install only inside their maintenance windows). Stopping withdraws the update from every server that has not installed it; install results arriving afterwards are ignored.

Every start, stop, override, approval and rule change is recorded in the audit log.

| Key | Default | Meaning |
|---|---|---|
**Archive:** an update that no approved server has reported as missing for 90 days, and that no server still has to install, leaves the update lists ("Show archived updates" lists it; its history stays). If a server reports it as missing again, it returns. One year after archiving it is deleted together with its rollout, releases and installation history. Updates that Microsoft replaced drop out of the agents' search results and are archived the same way; Windows Update does not report the replacement itself.

| `Rollout:EvaluationInterval` | `00:01:00` | how often the central server evaluates rollouts (10 s to 1 h) |
| `Updates:ArchiveAfter` | `90.00:00:00` | archive an update no server needed for this long (1 to 730 days) |
| `Updates:DeleteAfter` | `365.00:00:00` | delete an archived update after this long (30 days to 10 years) |
| `Updates:Interval` | `06:00:00` | how often the archive check runs |

## 13. Dashboard pages

All times are shown and entered in the time zone of the central server (its Windows time zone, named at the bottom of the navigation) in 24-hour format; agents and the database keep UTC. Fields that only matter when a switch is on (e-mail and webhook settings, coordination share, scheduled backup, delay and rings of an automatic start rule) or off ("Inherit" maintenance windows) are greyed out and cannot be used; their values are kept. The dashboard loads one small script from the central server itself (`app.js`, Content-Security-Policy `script-src 'self'`, no inline code): it makes these fields inert, adds a search box to long column filters and redraws the week preview of maintenance windows on every change. Every page also works without it (then the window picker shows a "Refresh preview" button).

Every page follows one pattern: page header, an **action band** with all actions of the page (primary first, destructive ones at the end, confirmed in a dialog), then boxed content. Lists sort by clicking a column, filter through the funnel icon in a column header (clickable values with counts), search, group (where offered) and page through 25 rows at a time; the view is part of the address, so a filtered list can be bookmarked or shared. New and edit open a dialog over the list.

- **Overview** (role `Reader`): key figures as tiles; each tile opens the server list filtered to exactly what it counts. Items that wait for a human decision, deployment per ring and the patch compliance trend. Operators pause and resume all installations in the action band; the pause is announced to the agents with their next heartbeat and recorded in the audit log.
- **Servers** (role `Reader`, actions `Operator`): operating system as a readable name with the build, filters for domain, operating system, ring, group, agent version (short form such as 0.6.0; the full version is the tooltip), status and pending updates, grouping, and "Search for updates now" or "Sync inventory" for the selected servers.
- **Server detail** (role `Reader`): status tiles, assignment as one saved configuration (ring, reboot policy, groups, maintenance windows; role `Admin`), system facts (uptime, last update search with its result; the agent version opens the server list filtered to it), missing updates, update history, Windows Update history, recent activity (registration, approval, inventory, update searches, installations, remote action results, restarts and agent updates, without heartbeats; kept 90 days), pre-flight checks and remote actions.
- **Rings** (role `Reader`): one page per ring with its updates as a table (state, day, feedback, success), the ring status donut, the servers of the ring as their own tab and the status of a selected update per server as a second tab.
- **Updates** and **update detail** (role `Reader`, actions `Operator`): see section 12; the detail page shows progress per ring, all servers with filters, and the timeline.
- A server counts as **offline** after one hour without heartbeat. **Patch compliance** is the share of approved servers without any released update left to install; the central server stores one value per day for 400 days.

## 14. Remote actions

On the server detail page an `Operator` queues actions for an approved server: **Search for updates now**, **Full resync** (inventory and search), **Restart agent service** and **Request agent logs**. An `Admin` can also **restart the server** and chooses in the dialog: **now** (also outside the maintenance window; the server warns signed-in users 5 minutes ahead) or **in the next maintenance window** (the agent holds the request and restarts once a window is open for at least 30 more minutes, also with reboot policy "Never"; in a coordinated group it waits for a free slot). The local veto `AllowReboot` applies to both (the agent then rejects the action). While a restart is queued or held, a red "Restart queued" bar at the top of the server page shows who requested it, when the agent plans it and why it waits; "Cancel restart" cancels a queued request or tells the agent to drop it (and aborts a restart already announced to signed-in users). The agent picks them up with its next heartbeat (the central server never connects to the agent), runs them in order and reports the result, which the page shows with the state of each action. A queued action can be cancelled until the agent fetched it; actions expire after 24 hours unfetched or one hour without result. Every request is recorded in the audit log.

Restarting the agent service relies on the service recovery settings: the agent ends with exit code 1 after confirming the action, so the service must be configured to restart on failure (including non-crash failures). The installer will set this; until then configure it with `sc.exe failure` and `sc.exe failureflag <service> 1`.

The server detail page also shows the time zone the agent reports and the next maintenance window as a date in server time and UTC.

**Agent logs:** the agent packs its own log files (`agent-*.log`) of the last 3 days into a ZIP archive of at most 10 MB (older files are left out if needed) and uploads it. The central server checks that it is a ZIP archive, stores it under `Administration:AgentLogDirectory` (default `%ProgramData%\SUSyphos\Server\AgentLogs`, readable only by SYSTEM, administrators and the service account) and deletes it after `Administration:AgentLogRetentionDays` (default 14). Operators download it from the server detail page; every download is audited.

## 15. Coordinated groups (leases)

For groups whose members must not all install or restart at the same time (e.g. domain controllers), tick **coordinated** on the Groups page and set the share in percent and the maximum (default 25 %, at most 5, always at least 1). Members then install and reboot only while they hold a lease from the central server. A lease ends when the agent reports the outcome, at the latest after 2 hours. After 2 failed or expired leases in a row (fewer in smaller groups) the group stops; the overview shows it, and an Operator resumes it under Pending approvals after checking the servers. All changes and resumes are audited.

## 16. AD coverage

If the central server is a domain member, it reads the enabled server computer accounts of its domain every 6 hours with its service account and compares them with the registered agents. Only Windows Server 2016 or newer counts; older servers appear separately as not supported. The overview shows the coverage rate; **AD coverage** lists every supported server without agent.

| Key | Default | Meaning |
|---|---|---|
| `Coverage:Enabled` | `true` | turns the AD comparison off |
| `Coverage:Domain` | own domain | domain to read |
| `Coverage:Interval` | `06:00:00` | how often AD is read (10 minutes to 7 days) |

## 17. Installation (MSI)

Both components ship as MSI packages (WiX Toolset 7) for Windows Server 2016 or newer. They are self-contained: no .NET runtime needs to be installed. They can be distributed by group policy, Configuration Manager or Intune, or installed manually.

**Central server** (run as administrator; a CA certificate must be in `LocalMachine\My` with its private key, or leave out `CERTTHUMBPRINT` and the central server creates its own self-signed certificate, section 3):

```powershell
msiexec /i SUSyphos.Server.msi CERTTHUMBPRINT=<thumbprint> HTTPSPORT=8443 /qn /l*v server-install.log
```

- Installs to `%ProgramFiles%\SUSyphos\Server` and registers the service `SUSyphos.Server` (LocalSystem, automatic start, restart on failure).
- `CERTTHUMBPRINT` and `HTTPSPORT` (1 to 65535, default 8443) are stored under `HKLM\SOFTWARE\SUSyphos\Server` (`CertificateThumbprint`, `HttpsPort`, `HttpsUrl`) and override `appsettings.json`. An upgrade keeps both unless given again on the command line. The service is started right away, with or without a thumbprint.
- Opens the HTTPS port in the Windows firewall for the service.
- Interactive installation (double-click): welcome, license, the HTTPS port and the one-time setup token to copy (first installation only; the port is pre-filled with 8443 or the `HTTPSPORT` from the command line; an upgrade keeps the installed port and skips the page), ready, progress and a final page with the dashboard address and an option to open the first setup with the token filled in. Choose the port before installing agents: they keep the address they were installed with. The certificate is set on the command line when needed.
- Start menu entry "SUSyphos Dashboard" (all users, SUSyphos icon) that opens the dashboard by the fully qualified name of this machine: `https://<host>.<primary DNS suffix>:<port>/` from the TCP/IP settings of Windows, or the computer name if the machine has no DNS suffix. Both packages show the SUSyphos icon in Programs and Features.
- A startup error of the service (e.g. an unreadable trust anchor) is written to the server log as a critical entry before the service ends; service recovery restarts it after 60 seconds.
- Database, trust anchor, setup token and logs stay under `%ProgramData%\SUSyphos\Server`, also on uninstall. Back them up before removing a server for good.

**Agent:**

```powershell
msiexec /i SUSyphos.Agent.msi SERVERURL=https://central.example.local:8443/ /qn /l*v agent-install.log
```

- Installs to `%ProgramFiles%\SUSyphos\Agent` and registers the service `SUSyphos.Agent` (LocalSystem, automatic start). The service restarts on failure, also when it ends with an error exit code (needed for the remote action "restart agent service").
- `SERVERURL` is written to `HKLM\SOFTWARE\SUSyphos\Agent\CentralServerUrl`; a group policy value under `HKLM\SOFTWARE\Policies\SUSyphos\Agent` still wins. Without any address the service is installed but not started.
- **Download from the central server:** the dashboard page Agents (section 22) offers the agent installer as `SUSyphos.Agent-<version>_<host>_<port>_<fingerprint>.msi`. The file content is the unchanged MSI; the installer reads address and trust anchor fingerprint from its own file name (also with a browser suffix such as ` (1)`), so `msiexec /i <file> /qn` needs no option. The fingerprint is written to `TrustAnchorFingerprint` in the same key, and the agent then pins only the central server with that anchor. A renamed file simply carries no address. Order: `SERVERURL` on the command line, then the file name, then the installed value.
- **Interactive installation** (double-click): welcome, license, a page for the central server address (pre-filled from the command line, the file name or the installed value; checked for `https://` and a valid host) and a ready page. If the address from the file name is changed, the fingerprint is not written and the agent pins on first contact.
- No certificate or other runtime data is part of the installer.
- Identity and trust state stay under `%ProgramData%\SUSyphos\Agent` on uninstall, so a reinstalled agent keeps its registration. Delete the folder to register anew.

**Upgrades:** install the newer MSI the same way; options can be left out, the installed values are kept. Downgrades are refused.

**Installations from before the rename (UpdateCoordinator, up to 0.4.0):** they are not upgraded; uninstall them, delete `%ProgramData%\UpdateCoordinator` and install SUSyphos anew (only test installations existed, decided by Marc on 2026-10-20).

**Building the installers:** `./build/Build-Installers.ps1` publishes both components and builds the MSIs into `artifacts\installers` (versions from the git tags; `-AgentVersion`/`-ServerVersion` override them). The CI pipeline builds them on every push, installs, upgrades and uninstalls them on the disposable runner and checks services, settings, firewall, the health endpoint and the agent registration (`build/Test-Installers.ps1`, which refuses to run outside CI because it changes the machine).

## 18. Accounts and removing servers

**Users** (Administration → Users, role `Admin`): create accounts with one role, change the role, set a new password, lock, unlock and delete. Your own account cannot be locked or deleted, and the last active administrator can neither lose the Admin role nor be locked or deleted. A role change, lock or new password ends the sessions of that account within a minute. Every change is audited.

**Sign-in with Windows (Active Directory)**, next to the local accounts (Administration → Users, box "Active Directory sign-in", role `Admin`):

1. Requirements: the central server is a member of the domain and is opened by its FQDN (e.g. `https://susyphos.lab.local:8443/`). The service runs as LocalSystem, so Kerberos uses the computer account; for a DNS alias register an SPN once: `setspn -S HTTP/<alias> <COMPUTERNAME>`. The HTTPS endpoint speaks HTTP/1.1 (`Kestrel:Endpoints:Https:Protocols` = `Http1`), because Windows sign-in does not work over HTTP/2.
2. Map AD groups to roles: enter the group as `DOMAIN\Group` (or its SID) and pick Reader, Operator or Admin. The SID is stored, so renaming the group does no harm. A user in several mapped groups gets the highest role; a user in none cannot sign in.
3. Switch it on. The sign-in page then shows "Sign in with Windows". On a domain computer the browser signs in without a password when the address is allowed for integrated authentication (Intranet zone, or the Edge/Chrome policy `AuthServerAllowlist`, e.g. `*.lab.local`); otherwise the browser asks once for the Windows credentials. No password reaches the central server.

Accounts that signed in with Windows appear in the user list as `DOMAIN\name` with the tag "Windows". Their role is set from their groups at every sign-in and cannot be changed here; they have no password, but can be locked and deleted. The last active local administrator can never be demoted, locked or deleted, so a local account always gets in when the domain or the mapping fails. Emergency switch: `Authentication:Windows:Enabled` = `false` in `appsettings.json` (or the environment variable `Authentication__Windows__Enabled=false`) turns Windows sign-in off whatever the dashboard says; restart the service. Sign-ins, refused sign-ins (no mapped group, locked) and mapping changes are in the audit log.

**Removing a server** (server detail page, role `Admin`, with confirmation): *Delete* keeps the history, cancels open releases, actions and leases and ends the credentials; if the agent is still running, it registers again as a new pending server. *Delete with history* removes the server and all its data.

**Clone suspicion:** the agent reports its host name with every heartbeat. If the credentials of one server are used by two host names in turn within an hour (typically a copied virtual machine), the server is blocked and appears under Pending approvals and on the overview. If only one host uses it, an `Operator` unblocks it; otherwise an `Admin` deletes it so each copy registers on its own. A simple rename is no clone suspicion.

## 19. Backup and restore

**Backup** (Administration → Backup, role `Admin`): enter a backup password (at least 16 characters, twice) and download the archive. It contains:

| Entry | Content |
|---|---|
| `manifest.json` | format version, creation time, central server version, public key of the trust anchor |
| `server.db` | consistent copy of the database (SQLite online backup while the service keeps running): servers, history, rules, accounts, audit log |
| `trust-anchor.p8` | private key of the trust anchor, encrypted with the backup password (PKCS#8, AES-256-CBC, PBKDF2 with SHA-256 and 600,000 iterations) |

Keep archive and password apart. Without the password the trust anchor cannot be used; without the trust anchor every agent must be reset locally after a restore. Creating a backup is audited. The TLS certificate is not part of the backup: on new hardware the central server uses a CA certificate you select or creates its own, and agents follow through the restored trust anchor.

**Restore** (on the same or new hardware):

1. Install the central server (section 17) and stop the service `SUSyphos.Server`.
2. As administrator in `%ProgramFiles%\SUSyphos\Server`: `SUSyphos.Server.exe restore <backup.zip>`. The password is asked for without echo, or taken from the environment variable `SUSYPHOS_BACKUP_PASSWORD`; it is never passed on the command line.
3. Start the service. Agents keep trusting the central server, because the trust anchor is the same; the database migrates to the installed version on start.

The restore refuses to run while the service runs. It checks everything first (archive complete, format supported, password decrypts the trust anchor, anchor matches the manifest) and only then replaces the database and the trust anchor, so a failed restore leaves the current installation untouched. Since the restore runs under your account and the service as LocalSystem, the trust anchor is first staged as `trust-anchor.key.restore` (DPAPI machine scope, readable only by administrators and LocalSystem); on its next start the service protects it for its own account and deletes the staged file.

**Scheduled backup** (same page, box "Scheduled backup", role `Admin`): every day at the chosen server time the central server writes the same archive into a folder (local, e.g. `D:\Backup\SUSyphos`, or a share `\fileserver\backup\susyphos`; the service runs as LocalSystem, so grant the computer account of the central server write access to a share) as `susyphos-backup-scheduled-<UTC time>.zip`, and deletes the oldest of these files beyond the number to keep (other files in the folder are never touched). The backup password is entered once and stored DPAPI-protected for the service account in `Backup:PasswordPath` (default `%ProgramData%\SUSyphos\Server\backup-password.dat`); it is not part of any backup, so keep it somewhere else too. "Run now" writes an archive right away. A failed run is shown on the page, logged and notified until a later run succeeds; settings changes are audited.

| Key | Default | Meaning |
|---|---|---|
| `Backup:PasswordPath` | `%ProgramData%\SUSyphos\Server\backup-password.dat` | protected file with the password of the scheduled backup |
| `Backup:CheckInterval` | `00:15:00` | how often the central server looks whether the daily backup is due (1 min to 1 h) |

## 20. Notifications

Administration → Notifications (role `Admin`). Every minute the central server derives the current conditions from its data and notifies each new one once, collected in one message per check (at most 50 events; the rest follows a minute later):

| Event | Notified again |
|---|---|
| New server waiting for approval | after it was approved or rejected and appears again |
| Server blocked as suspected clone | after it was unblocked and blocked again |
| Rollout alarm before a ring | after the alarm was resolved and occurs again |
| Gate waiting for approval | for every update and ring |
| Coordinated group stopped after failures | after an operator resumed it and it stops again |
| CA certificate expires within 30 days and no renewal is in the store | for a new certificate |
| Automatic certificate change (renewal, rollover, fallback) | each change, reported for a day |

**E-mail:** SMTP server, port, encryption (STARTTLS, TLS or none for an internal relay), optional user and password, sender and up to 20 recipients. The certificate of the mail server is validated.

**Webhook:** `POST` of a JSON document to an `https://` address (redirects are not followed):

```json
{ "subject": "SUSyphos: 2 new events", "createdAtUtc": "2026-10-19T12:00:00Z",
  "events": [ { "kind": "ServerPendingApproval", "key": "client.pending:…", "text": "New server waiting for approval: srv01.lab.local", "link": "https://uc01.lab.local:8443/approvals" } ] }
```

With a signature secret, the header `X-SUSyphos-Signature: sha256=<hex>` carries the HMAC-SHA256 of the body; check it on the receiving side.

**Dashboard address** (optional) turns the events into links. **Send a test notification** checks every enabled channel and shows the error per channel.

If no channel is configured, nothing is sent and nothing is lost: the events go out once a channel is set up. If a channel fails, the events are sent again on the next check, unless another channel delivered them. The SMTP password and the webhook secret are stored DPAPI-protected in `%ProgramData%\SUSyphos\Server\notification-secrets.dat` and are **not** part of the backup; enter them again after a restore. Changes and test notifications are audited.

| Key | Default | Meaning |
|---|---|---|
| `Notifications:Enabled` | `true` | switches all notifications off |
| `Notifications:Interval` | `00:01:00` | how often the conditions are checked (10 seconds to 1 hour) |
| `Notifications:SecretsPath` | `%ProgramData%\SUSyphos\Server\notification-secrets.dat` | protected file with SMTP password and webhook secret |

## 21. Windows Automatic Updates and group policy

The coordination only works if Windows does not install updates on its own. The agent reads the Automatic Updates setting (group policy first, then the local setting) and reports it. It changes the setting only when the central server asks for it (below).

| Setting on the server | Effect | Shown as |
|---|---|---|
| Not configured | Windows Server 2016 and later does not install on its own | OK |
| Configure Automatic Updates: disabled, 2, 3 or 7 | Windows notifies or only downloads; the agent installs | OK |
| Configure Automatic Updates: 4 (auto download and schedule the install) | Windows installs on its own schedule, outside rings and maintenance windows | Warning |
| Always automatically restart at the scheduled time | Windows restarts on its own | Warning |
| Compliance deadlines (Windows Update for Business) | Windows forces installation and restart after the deadline | Warning |

Where to see it: server detail ("Automatic updates" in the System box, tag "bypasses rings"), pre-flight check `AutomaticUpdates`, server status "Warning" and the overview tile "Servers with warnings".

**Letting the agent change it** (Administration → Maintenance windows, box "Windows Automatic Updates", role `Admin`; groups can override it in the group dialog): with "Keep Windows from installing on its own" the agent sets "Configure Automatic Updates" to option 3 (`AUOptions = 3` under `HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU`, as sconfig does) on servers where no group policy controls the setting, and records this in `ManagedAutomaticUpdates` under its own key. Group policy always wins: a value it did not write is never changed. Switched off (or the server is no longer approved), the agent removes only its own value again. Among groups, one group with "Leave unchanged" wins. The server detail page then shows "(set by SUSyphos)". Uninstalling the agent leaves an applied option 3 in place; remove the value by hand or by group policy if needed. Default: off.

**Recommended group policy** for servers managed by SUSyphos (Computer Configuration > Administrative Templates > Windows Components > Windows Update):

- **Configure Automatic Updates**: enabled, option **3 – Auto download and notify for install** (Defender signatures keep updating) or **7 – Notify for install and notify for restart**.
- **Always automatically restart at the scheduled time**: not configured or disabled.
- No compliance deadlines for these servers.
- **Specify intranet Microsoft update service location**: not configured, unless WSUS is used on purpose (reported as a warning, see `Updates:Source`).

Note: on Windows Server 2016 and 2019 a group policy refresh can briefly reset the setting to the Windows default (Microsoft known issue "Windows unexpectedly installs updates when automatic updates are disabled by Group Policy"). With option 3 or 7 and no configured schedule this has no effect.

**Microsoft Update (other Microsoft products):** Administration → Maintenance windows, box "Windows Automatic Updates and update source", sets the system default "Windows Update only" or "Microsoft Update"; groups can override it, and "Windows Update only" in one group wins. With Microsoft Update the agent registers the Microsoft Update service and searches and installs through it (also on servers that use WSUS), so SQL Server, Office and other Microsoft products are updated by the rings as well. Switched off, the agent removes only a registration it made itself (registry marker `RegisteredMicrosoftUpdate` under `HKLM\SOFTWARE\SUSyphos\Agent`).

## 22. Agents: installation and self-update

Dashboard page **Agents** (role `Operator` to download, `Admin` to check and release).

**Installing new agents:** enter the address the servers reach the central server at (pre-filled with the address the dashboard is opened with; on the central server itself with its fully qualified name) and download the installer. Its file name carries the address and the fingerprint of the trust anchor (section 17), so `msiexec /i <file> /qn` installs a working agent that trusts only this central server. The installer is the newest agent package the central server holds. Downloads are audited.

**Agent packages:** the central server installer ships the agent MSI of the same release (`%ProgramFiles%\SUSyphos\Server\agent`); it is registered as the first package at start. Once a day, and on **Check for new versions now**, the central server reads the GitHub releases of the project (`AgentPackages:Repository`, tags `release/vX.Y.Z`). A release newer than every known package is downloaded and accepted only if its SHA-256 matches `SHA256SUMS.txt` of the same release. The packages are not code-signed yet, so the checksum protects against corrupt downloads, not against a manipulated release; this is why nothing is released automatically. A downloaded version waits for release and is notified ("Agent version … is available").

**Release:** per version, **Release to <ring>** adds the next ring (Pilot first), **Release to all rings now** releases it everywhere at once (also for rings added later), **Withdraw** stops further updates to it (agents already on it stay). An agent gets the newest version released for its ring that is newer than its own; never a downgrade. A server without a ring gets only versions released for all rings.

**Self-update on the agent:** the heartbeat announces version, size and SHA-256. The agent downloads the package from the central server (`GET /api/v1/agents/package/{version}`, only the announced version, only for approved agents) into a folder only SYSTEM and administrators can access, checks size and SHA-256 and runs `msiexec /i … /qn /norestart` with a log. The MSI stops the service, replaces the files and starts the new version; settings, identity and trust stay. No reboot is needed or triggered. The running version after the restart tells whether it worked. A failure (checksum, msiexec exit code, still the old version after the restart) is reported with the next heartbeat, shown on the page Agents and on the server detail page, notified, and tried again after `AgentUpdate:RetryDelay`.

| Key (central server) | Default | Meaning |
|---|---|---|
| `AgentPackages:CheckEnabled` | `true` | daily check of the GitHub releases |
| `AgentPackages:CheckInterval` | `1.00:00:00` | interval of the check (1 h to 30 days) |
| `AgentPackages:Repository` | `debold/UpdateCoordinator` | GitHub repository whose releases carry the agent MSI |
| `AgentPackages:IncludePrereleases` | `true` | also offer pre-releases (every release before 1.0 is one) |
| `AgentPackages:MinimumVersion` | empty (built-in 0.4.0) | agents below it get the package shipped with the central server without a release; can only raise the built-in minimum |
| `AgentPackages:MaxPackageSize` | `314572800` | largest package downloaded (bytes) |
| `AgentPackages:Directory` | `%ProgramData%\SUSyphos\Server\AgentPackages` | where the packages are kept |
