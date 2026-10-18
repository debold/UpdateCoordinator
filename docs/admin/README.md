# Admin Documentation

> Guide and information on setup and communication paths. Updated with every feature.
> Status: skeleton. Installation and configuration follow once the vertical slice (register and heartbeat) is in place.

## 1. Components in operation

| Component | Runs as | Where |
|---|---|---|
| Central server | Windows service `UpdateCoordinator.Server` | one central Windows server |
| Agent | Windows service `UpdateCoordinator.Agent` | every managed Windows server, 2016 or later |

## 2. Communication paths

| From | To | Protocol | When | Purpose |
|---|---|---|---|---|
| Agent | Central server | HTTPS (REST/JSON) | heartbeat every 5–15 s, plus events | registration, inventory, reports, polling the action queue |
| Admin browser | Central server | HTTPS | on demand | dashboard and administration |
| Monitoring | Central server | HTTP(S) `GET /health` | as needed | liveness check, no authentication, no payload |

The central server **never** opens a connection to an agent. No inbound port is required on the managed servers.

Ports, firewall rules and certificate setup will be added once they are implemented.

## 3. Trust between agent and central server

Summary of `docs/03-communication-protocol.md`, section 3.2:

- On first contact the agent stores the trust anchor of the central server.
- A new TLS certificate for the central server is uploaded and approved in the dashboard. Agents follow automatically.
- The trust anchor is part of the central server backup. If it is lost, all agents must be reset locally.
- Local reset on a server: change the registry value under `HKLM\SOFTWARE\Policies\<Product>\Agent` (name and path will be finalized with the implementation). The reset is logged in the event log and on the central server.
