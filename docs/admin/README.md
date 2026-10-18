# Admin Documentation

> Guide and information on setup and communication paths. Updated with every feature.
> Status: register, heartbeat, trust anchor, inventory, update search, installation (without reboots), web administration (accounts, roles, audit log) and approval of new servers. Installers and the dashboard follow with later features.

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

The central server only listens on HTTPS. Its certificate comes from the Windows certificate store, so no certificate password is stored in configuration.

1. Import a certificate with private key into `LocalMachine\My` (any CA, also self-signed: agents do not rely on the CA but on the trust anchor). The service account needs read access to the private key.
2. Set `Trust:CertificateThumbprint` to its thumbprint and restart the service.
3. On start the central server signs a new trust binding for the certificate (next serial). Agents accept the new certificate automatically on their next contact.

Configuring the thumbprint is the approval of the certificate until the dashboard offers an approval step. Renewing a certificate with the same key does not even need a new binding.

## 4. Configuration

Both services use the standard .NET configuration: `appsettings.json` next to the executable, environment variables (`__` as section separator, e.g. `Database__Path`) and command-line arguments.

### Central server

| Key | Default | Meaning |
|---|---|---|
| `Kestrel:Endpoints:Https:Url` | `https://*:8443` | HTTPS endpoint for agents and administration |
| `Trust:CertificateThumbprint` | none (required in production) | thumbprint of the TLS certificate in `LocalMachine\My` (fallback `CurrentUser\My`) |
| `Trust:AnchorPath` | `%ProgramData%\UpdateCoordinator\Server\trust-anchor.key` | private key of the trust anchor, DPAPI-protected for the service account; **include in backups** |
| `Database:Path` | `%ProgramData%\UpdateCoordinator\Server\server.db` | SQLite database file; the folder is created and the schema migrated on startup |
| `Agents:RateLimit:RegisterPermitLimit` | `10` | registrations allowed per IP address and window |
| `Agents:RateLimit:Window` | `00:01:00` | window of the registration rate limit |
| `Installation:GlobalPause` | `false` | global pause: agents start no new installations (check, search and reporting continue); read at runtime |

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
| Agent | `%ProgramData%PDATECOORDINATORAGENTINSTALLATIONS.DAT` | INSTALL REPORTS NOT YET DELIVERED AND THE LAST ATTEMPT PER UPDATE, DPAPI-PROTECTED |
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
- **The agent never reboots the server.** If an update needs a reboot, the agent reports it and the central server marks the server as "reboot pending". Reboot handling follows with the dashboard.
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
