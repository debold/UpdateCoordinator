# Admin Documentation

> Guide and information on setup and communication paths. Updated with every feature.
> Status: register and heartbeat. Installers, certificate setup and the dashboard follow with later features.

## 1. Components in operation

| Component | Runs as | Where |
|---|---|---|
| Central server | Windows service `UpdateCoordinator.Server` | one central Windows server |
| Agent | Windows service `UpdateCoordinator.Agent` | every managed Windows server, 2016 or later |

## 2. Communication paths

| From | To | Protocol | When | Purpose |
|---|---|---|---|---|
| Agent | Central server | HTTPS, `POST /api/v1/agents/register` | once, on first start | registration; returns client GUID and client secret |
| Agent | Central server | HTTPS, `POST /api/v1/agents/heartbeat` | every 10 s (configurable); with back-off up to 5 min while unreachable | liveness, registration state, later polling of the action queue |
| Admin browser | Central server | HTTPS | on demand | dashboard and administration (later feature) |
| Monitoring | Central server | HTTP(S) `GET /health` | as needed | liveness check, no authentication, no payload |

The central server **never** opens a connection to an agent. No inbound port is required on the managed servers.

Agents authenticate every request except registration with HTTP Basic authentication (client GUID and client secret). The registration endpoint is anonymous and therefore rate limited per IP address.

Ports, firewall rules and certificate setup will be added with the installers.

## 3. Configuration

Both services use the standard .NET configuration: `appsettings.json` next to the executable, environment variables (`__` as section separator, e.g. `Database__Path`) and command-line arguments.

### Central server

| Key | Default | Meaning |
|---|---|---|
| `Database:Path` | `%ProgramData%\UpdateCoordinator\Server\server.db` | SQLite database file; the folder is created and the schema migrated on startup |
| `Agents:RateLimit:RegisterPermitLimit` | `10` | registrations allowed per IP address and window |
| `Agents:RateLimit:Window` | `00:01:00` | window of the registration rate limit |

### Agent

| Key | Default | Meaning |
|---|---|---|
| `CentralServer:BaseAddress` | none (required) | address of the central server, must be `https://` |
| `CentralServer:HeartbeatInterval` | `00:00:10` | interval between heartbeats (1 s to 10 min) |
| `CentralServer:MaxBackoff` | `00:05:00` | longest wait between attempts while the central server is unreachable |

Invalid configuration stops the service at startup with a clear error in the event log instead of running with wrong settings.

## 4. Data on disk

| Component | Path | Content |
|---|---|---|
| Central server | `Database:Path` | SQLite database. Client secrets are stored only as SHA-256 hashes. |
| Agent | `%ProgramData%\UpdateCoordinator\Agent\identity.dat` | client GUID and client secret, encrypted with DPAPI for the service account |

If the agent identity file is deleted or unreadable, the agent registers again and appears as a new pending client.

## 5. Registration states

| State | Meaning | Agent may send |
|---|---|---|
| `Pending` | registered, waiting for approval | heartbeat (later also inventory, unregister) |
| `Approved` | approved by an admin or a rule | all messages |
| `Rejected` | rejected by an admin | nothing (`403`) |
| `Blocked` | clone suspicion, waiting for an admin | nothing (`403`) |
| `Deleted` | unregistered or soft-deleted | nothing (`403`) |

Approval is not yet available in the user interface; it follows with the dashboard.

## 6. Trust between agent and central server

Summary of `docs/03-communication-protocol.md`, section 3.2 (implemented in a later feature):

- On first contact the agent stores the trust anchor of the central server.
- A new TLS certificate for the central server is uploaded and approved in the dashboard. Agents follow automatically.
- The trust anchor is part of the central server backup. If it is lost, all agents must be reset locally.
- Local reset on a server: change the registry value under `HKLM\SOFTWARE\Policies\<Product>\Agent` (name and path will be finalized with the implementation). The reset is logged in the event log and on the central server.
