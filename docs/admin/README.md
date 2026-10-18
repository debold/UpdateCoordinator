# Admin Documentation

> Guide and information on setup and communication paths. Updated with every feature.
> Status: register, heartbeat and trust anchor. Installers and the dashboard follow with later features.

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

Invalid configuration stops the service at startup with a clear error in the event log instead of running with wrong settings.

## 5. Data on disk

| Component | Path | Content |
|---|---|---|
| Central server | `Database:Path` | SQLite database. Client secrets are stored only as SHA-256 hashes. |
| Central server | `Trust:AnchorPath` | private key of the trust anchor, DPAPI-protected for the service account |
| Agent | `%ProgramData%\UpdateCoordinator\Agent\identity.dat` | client GUID and client secret, DPAPI-protected for the service account |
| Agent | `%ProgramData%\UpdateCoordinator\Agent\trust.dat` | pinned trust anchor, accepted certificate key and highest binding serial, DPAPI-protected |

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
