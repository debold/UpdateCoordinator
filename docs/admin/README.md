# Admin Documentation

> Guide and information on setup and communication paths. Updated with every feature.
> Status: register, heartbeat, trust anchor, inventory, update search, installation with optional reboots inside maintenance windows, web administration with rings, rollouts, remote actions and leases, approval of new servers, and MSI installers for both components.

## 1. Components in operation

| Component | Runs as | Where |
|---|---|---|
| Central server | Windows service `UpdateCoordinator.Server` | one central Windows server |
| Agent | Windows service `UpdateCoordinator.Agent` | every managed Windows server, 2016 or later |

## 2. Communication paths

| From | To | Protocol | When | Purpose |
|---|---|---|---|---|
| Agent | Central server | HTTPS, `GET /api/v1/trust/binding` | first contact, after a certificate change or a local trust reset | verifies the identity of the central server (trust anchor); nothing is sent |
| Agent | Central server | HTTPS, `POST /api/v1/agents/register` | once, on first start | registration; returns client GUID and client secret |
| Agent | Central server | HTTPS, `POST /api/v1/agents/heartbeat` | every 10 s (configurable); with back-off up to 5 min while unreachable | liveness, registration state, later polling of the action queue |
| Agent | Central server | HTTPS, `POST /api/v1/agents/inventory` | after registration, on start, when changed, at least daily | OS, domain, roles, update source, installed updates |
| Agent | Central server | HTTPS, `POST /api/v1/agents/updates` | every 4 h (approved agents) | pre-flight checks and available updates |
| Agent | Central server | HTTPS, `GET /api/v1/agents/releases` | when the release version in the heartbeat changes (approved agents) | released updates with earliest installation time |
| Agent | Central server | HTTPS, `POST /api/v1/agents/installations` | after each installation run, or later if the central server was unreachable | result per update, reboot needed |
| Agent | Windows Update (or WSUS) | HTTPS, via the Windows Update Agent API | during the update search | search for available updates; the agent itself contacts no other internet address |
| Admin browser | Central server | HTTPS | on demand | dashboard and administration (later feature) |
| Monitoring | Central server | HTTPS `GET /health` | as needed | liveness check, no authentication, no payload |

The central server **never** opens a connection to an agent. No inbound port is required on the managed servers. On the central server, allow inbound TCP **8443** (default HTTPS port) from the managed servers.

Agents authenticate every request except registration and the trust binding with HTTP Basic authentication (client GUID and client secret). The registration endpoint is anonymous and therefore rate limited per IP address.

## 3. TLS certificate of the central server

The central server only listens on HTTPS. Its certificate comes from the Windows certificate store, so no certificate password is stored in configuration. Agents never rely on a CA: they accept exactly the certificate the trust anchor has signed, so every certificate change below is followed by all agents automatically on their next contact.

**First start.** With `CERTTHUMBPRINT` (or a selection in the dashboard) the central server uses that certificate. Without one it creates a self-signed certificate for itself:

- CN is the fully qualified name of the machine, the SAN contains the fully qualified name and the host name.
- RSA 3072 key in the machine key store, not exportable; friendly name `UpdateCoordinator self-signed`; stored in `LocalMachine\My`.
- Valid for 180 days (`Certificate:SelfSignedLifetime`). After a restart the existing certificate is reused.

Browsers do not trust a self-signed certificate and warn on every rollover. For the dashboard a certificate from an internal CA is recommended (step by step below); agents work with either.

**Using a CA certificate.**

1. Enroll a server authentication certificate with private key into `LocalMachine\My`, ideally through autoenrollment. LocalSystem (the service account) can read machine keys.
2. Select it under Administration → TLS certificate (role `Admin`, with confirmation), or set it at installation with `CERTTHUMBPRINT`. The selection is stored in `HKLM\SOFTWARE\UpdateCoordinator\Server\CertificateThumbprint`; `Trust:CertificateThumbprint` in the configuration works as well.
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
| `Trust:AnchorPath` | `%ProgramData%\UpdateCoordinator\Server\trust-anchor.key` | private key of the trust anchor, DPAPI-protected for the service account; **include in backups** |
| `Database:Path` | `%ProgramData%\UpdateCoordinator\Server\server.db` | SQLite database file; the folder is created and the schema migrated on startup |
| `Agents:RateLimit:RegisterPermitLimit` | `10` | registrations allowed per IP address and window |
| `Agents:RateLimit:Window` | `00:01:00` | window of the registration rate limit |
| `Installation:GlobalPause` | `false` | forces the global pause on (agents start no new installations; check, search and reporting continue), independent of the switch on the overview page; read at runtime. Normally the pause is switched in the dashboard. |

### Agent

Agent settings are normally set in the registry (string values):

| Registry key | Written by | Priority |
|---|---|---|
| `HKLM\SOFTWARE\Policies\UpdateCoordinator\Agent` | group policy | highest: overrides every other setting |
| `HKLM\SOFTWARE\UpdateCoordinator\Agent` | installer (`SERVERURL=https://…`, later also from the download file name) | overrides `appsettings.json`, environment and command line |

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
| `Logging:File:Directory` | `%ProgramData%\UpdateCoordinator\Agent\Logs` or `...\Server\Logs` | folder of the log files (`agent-YYYYMMDD.log`, `server-YYYYMMDD.log`) |
| `Logging:File:MinimumLevel` | `Information` | lowest level in the file (`Trace`, `Debug`, `Information`, `Warning`, `Error`) |
| `Logging:File:RetainedFileCountLimit` | `31` | number of files kept; older files are deleted |
| `Logging:File:FileSizeLimitBytes` | `10485760` (10 MB) | size at which a new file is started within the same day |

A new file is started every day and when the size limit is reached. Client secrets and keys are never written to the logs. For a detailed trace, set `Logging:File:MinimumLevel` to `Debug` (environment variable `Logging__File__MinimumLevel=Debug`) and restart the service.

## 5. Data on disk

| Component | Path | Content |
|---|---|---|
| Central server | `Database:Path` | SQLite database. Client secrets are stored only as SHA-256 hashes. |
| Central server | `Trust:AnchorPath` | private key of the trust anchor, DPAPI-protected for the service account |
| Agent | `%ProgramData%\UpdateCoordinator\Agent\identity.dat` | client GUID and client secret, DPAPI-protected for the service account |
| Agent | `%ProgramData%\UpdateCoordinator\Agent\trust.dat` | pinned trust anchor, accepted certificate key and highest binding serial, DPAPI-protected |
| Agent | `%ProgramData%\UpdateCoordinator\Agent\installations.dat` | install reports not yet delivered (at most 50) and the last attempt per update, DPAPI-protected |
| Both | `Logging:File:Directory` | rolling log files |
| Agent | `%ProgramData%\UpdateCoordinator\Agent\installations.dat` | install reports not yet delivered, the last attempt per update and a held lease, DPAPI-protected |
| BOTH | `LOGGING:FILE:DIRECTORY` | ROLLING LOG FILES |

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

Approval is not yet available in the user interface; it follows with the dashboard.

## 7. Trust between agent and central server

Details in `docs/03-communication-protocol.md`, section 3.2.

- On first contact the agent pins the trust anchor of the central server. Install new agents in a trusted network: whoever answers the first contact gets pinned.
- A new TLS certificate is approved on the central server (section 3). Agents follow automatically, also after being offline.
- The trust anchor file is part of the central server backup. If it is lost, every agent must be reset locally.
- If an agent does not trust the central server, it sends nothing and logs "Central server not trusted" in the event log every retry.
- **Local reset on a server:** set or change the string value `TrustResetToken` (e.g. to the current date) under `HKLM\SOFTWARE\Policies\UpdateCoordinator\Agent` by group policy or manually, then restart the agent service. The agent forgets the pinned anchor once, pins the central server again on the next contact, writes a warning to the event log and reports the reset to the central server. The same value again does not trigger another reset.

## 8. Installations

- Agents install only updates released to them by the central server, only inside a maintenance window, only if the pre-flight check passes and never during a global pause.
- **Reboots** are off by default: the agent reports a pending reboot and the server shows "reboot pending". An administrator allows reboots inside the maintenance window per system default (Administration → Maintenance windows), group (Groups) or server (server detail); among several groups "Never" wins. The agent then restarts only while a window is open for at least 30 more minutes, warns signed-in users 5 minutes ahead and requests at most one restart per 6 hours. A local veto always wins: set the string or DWORD value `AllowReboot` = `0` under `HKLM\SOFTWARE\Policies\UpdateCoordinator\Agent` (OR THE INSTALLER KEY); ONLY `1`/`TRUE` ALLOWS. THE DEVELOPMENT CONFIGURATION OF THE AGENT FORBIDS REBOOTS.
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

The dashboard runs on the same HTTPS endpoint as the agent API (default `https://<central server>:8443/`). It uses no scripts and no external resources.

**First start:** as long as no account exists, the central server writes a one-time setup token to `Administration:SetupTokenPath` (default `%ProgramData%\UpdateCoordinator\Server\setup-token.txt`). The file is readable only by SYSTEM, local administrators and the service account; its path is also written to the service log. Open `/setup`, enter the token and create the first administrator. The token file is deleted and the setup is closed for good; a restart with existing accounts never reopens it.

**Accounts and roles:** local accounts with the hierarchical roles `Reader` (read), `Operator` (operational actions) and `Admin` (administration). Passwords need at least 12 characters (passphrases welcome, no composition rules). After 5 failed sign-ins an account is locked for 15 minutes. The session cookie is HTTPS-only, `SameSite=Strict` and expires after 8 hours without activity. Every form is protected against cross-site request forgery.

**Audit log:** sign-ins (successful, failed, locked out), sign-outs and the setup are recorded with time, actor, action, target and details (Administration → Audit log, Admin role). The log is append-only: the database rejects changes and deletions, and each entry carries a SHA-256 hash over its content and the previous entry. The audit page checks the chain on every visit and reports where it was broken if the database file was modified outside the application.

| Key | Default | Meaning |
|---|---|---|
| `Administration:SetupTokenPath` | `%ProgramData%\UpdateCoordinator\Server\setup-token.txt` | one-time setup token file (only while no account exists) |

## 10. Approving new servers

New agents register without any secret and stay `Pending` until they are approved. Pending servers send heartbeats and their inventory but receive no releases and install nothing.

**Manual:** Pending approvals (role `Operator` or `Admin`) lists host name, domain, source IP address, operating system, detected roles and times. Approve or reject; every decision is recorded in the audit log. A server can be decided only once; rejected servers cannot send anything.

**Rule-based (optional):** list DNS domains in `Approval:AutoApproveDomains`. Every `Approval:CheckInterval` (default 1 minute) the central server checks each pending server of such a domain itself:

1. the computer account `HOST$` exists in Active Directory and is enabled (LDAP with signing and sealing, using the service account of the central server; the central server must be a domain member);
2. the IP address the registration came from is one of the addresses DNS returns for `host.domain`.

Only if both checks pass is the server approved, recorded in the audit log as actor `rule`. Values reported by the agent are never trusted on their own. If Active Directory cannot be reached, the server stays pending and is checked again later; servers that do not pass stay pending for a manual decision.

| Key | Default | Meaning |
|---|---|---|
| `Approval:AutoApproveDomains` | empty (off) | DNS domains whose verified members are approved automatically |
| `Approval:CheckInterval` | `00:01:00` | how often pending servers are checked (10 s to 1 h) |

## 11. Rings, groups and maintenance windows

**Rings:** every server is in exactly one of the fixed rings `Pilot`, `Test` and `Production`. Production is deployed last and is also the fallback: new servers land in `Production` until a rule or an administrator assigns another ring.

**Groups:** Administration → Groups (role `Admin`). A group bundles servers, e.g. domain controllers, and may have its own maintenance windows.

**Assignment rules:** Administration → Assignment rules (role `Admin`). A rule has a priority, a target (a ring or a group) and up to three conditions, all of which must match: detected role (e.g. `DomainController`), DNS domain and host name pattern (`*` any characters, `?` one character, case-insensitive).

- Ring: a manual ring on the server wins; otherwise the matching ring rule with the lowest priority number; otherwise `Production`.
- Groups: every matching group rule adds its group; manual memberships set on the server stay until they are removed there.
- Assignment is recalculated when a server registers, after every inventory, after every change in the administration and when the central server starts.

**Maintenance windows:** Administration → Maintenance windows sets the system default; groups and single servers (server detail page) can override it. One window per line in local server time of the server:

```
Mon-Fri 19:00-05:00
Sat,Sun 00:00-24:00
```

A window whose end is not after its start runs over midnight. At most 14 windows per level. The most specific level wins: server windows, otherwise the windows of all groups of the server together, otherwise the system default. The effective windows are shown on the server detail page and delivered to the agent with the release list; agents pick up a change with the next heartbeat.

Every change is recorded in the audit log.

## 12. Rollouts (ring logic)

Every update runs through the rings Pilot → Test → Production on its own. A server gets an update only when it is released for the server's ring and the server reported it as missing; it installs it only inside its maintenance windows.

**Start rules** (Administration → Release rules, role `Admin`) decide how a newly reported update enters Pilot. The matching rule with the lowest priority number applies; conditions are classification (e.g. `Security Updates`), MSRC severity (e.g. `Critical`) and title pattern (e.g. `*Cumulative Update*`), and all filled conditions must match. "Automatic" starts the rollout, optionally with a delay in hours before Pilot servers may install the update (the wait time for Pilot → Test starts after the delay). "Manual" keeps matching updates **queued** on the Updates page until an `Operator` starts them; updates without a matching rule stay queued as well. By default one rule without conditions starts every update automatically and without delay.

**Transitions:** Administration → Release rules (role `Admin`) sets per transition the minimum and maximum wait time (days), the feedback rate (share of targeted servers that reported a result) and the success rate (share of successful results), and whether a manual gate applies.

| Transition | Minimum wait | Maximum wait | Feedback | Success | Manual gate |
|---|---|---|---|---|---|
| Pilot → Test | 2 days | 7 days | 80 % | 95 % | off |
| Test → Production | 3 days | 10 days | 80 % | 95 % | on |

After the minimum wait time the next ring is released when both rates are met. At the maximum wait time a good success rate is enough. A poor success rate, or no result at all at the maximum wait time, raises an **alarm**: nothing rolls on until an operator decides.

**Pending approvals** (role `Operator`) lists updates waiting at a manual gate or stopped by an alarm. Approve (or "release anyway" for an alarm) releases the update for the ring; "Stop rollout" withdraws it.

**Updates** page (role `Reader`, actions `Operator`): state of every rollout per ring, queued updates with the number of servers missing them, and the actions start/resume, stop and "Release to all rings" (manual override, e.g. for a critical security update; servers still install only inside their maintenance windows). Stopping withdraws the update from every server that has not installed it; install results arriving afterwards are ignored.

Every start, stop, override, approval and rule change is recorded in the audit log.

| Key | Default | Meaning |
|---|---|---|
| `Rollout:EvaluationInterval` | `00:01:00` | how often the central server evaluates rollouts (10 s to 1 h) |

## 13. Dashboard pages

- **Overview** (role `Reader`): items that wait for a human decision (alarms, release gates, new servers, servers with failed installations or pre-flight checks, servers without heartbeat for over an hour), key figures, deployment per ring and the patch compliance trend. Operators pause and resume all installations here; the pause is announced to the agents with their next heartbeat and recorded in the audit log.
- **Update detail** (role `Reader`, actions `Operator`): one page per update with its progress in every ring, the timeline of releases, gates, alarms and manual actions, and every affected server with status and result; operators start, stop, override and approve gates there.
- **Rings** (role `Reader`): one page per ring with the state of every server × update pair (successful, pending = released and waiting for the window, queued = missing but not released, failed), the active updates with their rates and the servers of a selected update.
- **Servers** (role `Reader`): list with health and filters for name or domain, ring and health; the detail page shows pre-flight checks, missing updates with their release state, assignment, maintenance windows and the update history with error codes in plain text.
- A server counts as **offline** after one hour without heartbeat. **Patch compliance** is the share of approved servers without any released update left to install; the central server stores one value per day for 400 days.

## 14. Remote actions

On the server detail page an `Operator` queues actions for an approved server: **Search for updates now**, **Full resync** (inventory and search), **Restart agent service** and **Request agent logs**. An `Admin` can also **restart the server** immediately, even outside the maintenance window, after ticking the confirmation box; the server warns signed-in users 5 minutes ahead, and the local veto `AllowReboot` still applies (the agent then rejects the action). The agent picks them up with its next heartbeat (the central server never connects to the agent), runs them in order and reports the result, which the page shows with the state of each action. A queued action can be cancelled until the agent fetched it; actions expire after 24 hours unfetched or one hour without result. Every request is recorded in the audit log.

Restarting the agent service relies on the service recovery settings: the agent ends with exit code 1 after confirming the action, so the service must be configured to restart on failure (including non-crash failures). The installer will set this; until then configure it with `sc.exe failure` and `sc.exe failureflag <service> 1`.

The server detail page also shows the time zone the agent reports and the next maintenance window as a date in server time and UTC.

**Agent logs:** the agent packs its own log files (`agent-*.log`) of the last 3 days into a ZIP archive of at most 10 MB (older files are left out if needed) and uploads it. The central server checks that it is a ZIP archive, stores it under `Administration:AgentLogDirectory` (default `%ProgramData%\UpdateCoordinator\Server\AgentLogs`, readable only by SYSTEM, administrators and the service account) and deletes it after `Administration:AgentLogRetentionDays` (default 14). Operators download it from the server detail page; every download is audited.

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
msiexec /i UpdateCoordinator.Server.msi CERTTHUMBPRINT=<thumbprint> HTTPSPORT=8443 /qn /l*v server-install.log
```

- Installs to `%ProgramFiles%\UpdateCoordinator\Server` and registers the service `UpdateCoordinator.Server` (LocalSystem, automatic start, restart on failure).
- `CERTTHUMBPRINT` and `HTTPSPORT` (default 8443) are stored under `HKLM\SOFTWARE\UpdateCoordinator\Server` (`CertificateThumbprint`, `HttpsUrl`) and override `appsettings.json`. The service is started right away, with or without a thumbprint.
- Opens the HTTPS port in the Windows firewall for the service.
- Database, trust anchor, setup token and logs stay under `%ProgramData%\UpdateCoordinator\Server`, also on uninstall. Back them up before removing a server for good.

**Agent:**

```powershell
msiexec /i UpdateCoordinator.Agent.msi SERVERURL=https://central.example.local:8443/ /qn /l*v agent-install.log
```

- Installs to `%ProgramFiles%\UpdateCoordinator\Agent` and registers the service `UpdateCoordinator.Agent` (LocalSystem, automatic start). The service restarts on failure, also when it ends with an error exit code (needed for the remote action "restart agent service").
- `SERVERURL` is written to `HKLM\SOFTWARE\UpdateCoordinator\Agent\CentralServerUrl`; a group policy value under `HKLM\SOFTWARE\Policies\UpdateCoordinator\Agent` still wins. Without any address the service is installed but not started.
- No certificate or other runtime data is part of the installer; the agent pins the central server on first contact.
- Identity and trust state stay under `%ProgramData%\UpdateCoordinator\Agent` on uninstall, so a reinstalled agent keeps its registration. Delete the folder to register anew.

**Upgrades:** install the newer MSI the same way; options can be left out, the installed values are kept. Downgrades are refused.

**Building the installers:** `./build/Build-Installers.ps1` publishes both components and builds the MSIs into `artifacts\installers` (versions from the git tags; `-AgentVersion`/`-ServerVersion` override them). The CI pipeline builds them on every push, installs, upgrades and uninstalls them on the disposable runner and checks services, settings, firewall, the health endpoint and the agent registration (`build/Test-Installers.ps1`, which refuses to run outside CI because it changes the machine).

## 18. Accounts and removing servers

**Users** (Administration → Users, role `Admin`): create accounts with one role, change the role, set a new password, lock, unlock and delete. Your own account cannot be locked or deleted, and the last active administrator can neither lose the Admin role nor be locked or deleted. A role change, lock or new password ends the sessions of that account within a minute. Every change is audited.

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

1. Install the central server (section 17) and stop the service `UpdateCoordinator.Server`.
2. As administrator in `%ProgramFiles%\UpdateCoordinator\Server`: `UpdateCoordinator.Server.exe restore <backup.zip>`. The password is asked for without echo, or taken from the environment variable `UPDATECOORDINATOR_BACKUP_PASSWORD`; it is never passed on the command line.
3. Start the service. Agents keep trusting the central server, because the trust anchor is the same; the database migrates to the installed version on start.

The restore refuses to run while the service runs. It checks everything first (archive complete, format supported, password decrypts the trust anchor, anchor matches the manifest) and only then replaces the database and the trust anchor, so a failed restore leaves the current installation untouched. Since the restore runs under your account and the service as LocalSystem, the trust anchor is first staged as `trust-anchor.key.restore` (DPAPI machine scope, readable only by administrators and LocalSystem); on its next start the service protects it for its own account and deletes the staged file.

A scheduled automatic backup is not implemented yet; create backups from the dashboard and keep them with your regular server backup.
