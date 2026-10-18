# Windows Update Coordinator – Architecture Overview

> Entry document. Details are in the follow-up documents:
> `02-guidelines.md`, `03-communication-protocol.md`, `04-central-server.md`, `05-agent-design.md`, `06-dashboard.md`.
> Status: design from the spec session on 2026-10-17. Implementation is done with agentic AI that proposes its own implementation approaches.

## 1. Goal

Central coordination of Windows updates for Windows servers with ring deployment, central configuration and reporting. The project is a deliberate alternative to WSUS (classified as deprecated by Microsoft in September 2024) and is meant to be usable in arbitrary customer environments later on. Onboarding of new agents is secured exclusively through an approval mechanism (manual or rule-based), without pre-shared secrets, tokens or certificates on the client side. Clients are identified by a centrally issued GUID and authenticate with a client secret issued at registration. The agent verifies the central server through a trust anchor pinned on first use, so certificate changes can be approved centrally (see `03-communication-protocol.md`).

## 2. Components

| Component | Responsibility |
|---|---|
| **Agent** | Lightweight Windows service (C#) on every managed server: inventory, update search, installation, reporting, execution of remote actions |
| **Central server** | Native Windows application (service): web API for agents, web dashboard/administration, ring and release logic, data storage, backup |

The central server runs natively on Windows. Agent communication and administration use a web API / web interface.

## 3. Core principles

1. **The agent initiates every connection.** The central server never connects to an agent. Remote actions use a per-client queue that the agent polls with a short heartbeat (guideline 5–15 s).
2. **Central control, local execution.** The central server decides per ring which updates a server may install from when on (package of update ID + earliest installation time). The agent decides locally when to install within the allowed windows and forms its own batches.
3. **The agent never goes to the internet for agent updates.** It only asks the central server. This minimizes internet access per server.
4. **Reuse before building.** Established components and standards (REST/JSON/HTTPS, ASP.NET Core, SQLite) instead of home-grown protocols.
5. **The ring logic also applies to the project itself** (its own fixed, predefined rings for agent updates).
6. **Security, tests and documentation from the start** (see `02-guidelines.md`).

## 4. Technology decisions

| Topic | Decision |
|---|---|
| Agent language | C#, modern Windows service, no PowerShell scripting |
| Update access | WUA API (Windows Update Agent API, COM), not the PowerShell layer |
| Supported servers | Windows Server 2016 and later (decided 2026-10-18) |
| .NET version | Agent and central server on the latest .NET (currently .NET 10) |
| API format | REST + JSON over HTTPS via ASP.NET Core (OpenAPI included) |
| Data storage | SQLite (no separate SQL Server installation); relational because the dashboard statistics need SQL aggregation |
| High availability | Not planned (multi-master would mean an architecture change or an external dependency with SQLite) |
| Backup | Self-built, file-based backup controlled by the service (e.g. ZIP); Litestream was rejected |
| Central server installation | Classic Windows installer; configuration is preserved on updates; schema/config migration is automatic |
| Versioning | Agent and central server have separate semantic versions, derived from git tags `agent/vX.Y.Z` and `server/vX.Y.Z` (MinVer). The protocol version is part of the API route (`/api/v1`). Compatibility is ensured by the minimum agent version announced by the central server (decided 2026-10-18) |
| Update source per server | Windows Update or (undesired) WSUS, controlled via `ServerSelection` of `IUpdateSearcher` |

## 5. Update catalog

The WUA API only returns updates for the local OS. The central server therefore cannot fetch a catalog for all OS versions itself. The catalog is built exclusively by **aggregating agent reports** (optionally supplemented by WSUS as a source, if in use).

Updates installed outside our own maintenance (manually or through other mechanisms) are detected and shown in the dashboard.

## 6. High-level flow

1. The agent is installed, **registers** (Register) and then sends its **inventory**.
2. The server is approved (manually by an admin or rule-based) and assigned to a ring and a group.
3. Each cycle the agent runs a fixed pipeline: **pre-flight health check → update search → reporting → installation** (only if released by the central server).
4. The central server evaluates feedback **per update within a ring** and decides on ring transitions.
5. The agent continuously reports live status, heartbeat and results, including a post-install health check.

## 7. Implementation order (recommendation)

1. Minimal vertical slice: agent + central server with only **register and heartbeat** (proves communication, authentication and transport format end to end).
2. **Inventory**.
3. Actual update functions (search, installation, feedback).
4. Finally ring logic, dashboard, more complex approval processes.

## 8. Open / later

- Notification channel and escalation when a manual approval is outstanding
- AD/LDAP integration for dashboard login and role assignment
- Further message types (the list is a first draft)
