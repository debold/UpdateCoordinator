# Changelog

All notable changes are documented here. Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Agent and central server are versioned separately ([Semantic Versioning](https://semver.org/), git tags `agent/vX.Y.Z` and `server/vX.Y.Z`).

## [Unreleased]

### Agent

- Registers once at the central server and stores client GUID and client secret encrypted with DPAPI under `%ProgramData%\UpdateCoordinator\Agent`.
- Sends a heartbeat every 10 seconds (configurable) and logs changes of the registration state.
- Registers again automatically if the central server no longer accepts its credentials.
- Backs off exponentially (up to 5 minutes, configurable) while the central server is unreachable; timeouts and circuit breaker via `Microsoft.Extensions.Http.Resilience`. No error stops the service.
- Accepts only HTTPS addresses for the central server and validates every server response.
- Reads the central server address from the registry value `CentralServerUrl`; the group policy key overrides the installer key and all other configuration sources.
- Trust anchor: pins the anchor of the central server on first contact and accepts only the TLS certificate bound to it by a signed binding with increasing serial, independent of CA and host name. Sends nothing to an untrusted central server.
- Local trust reset via registry value `TrustResetToken` (once per change), logged and reported to the central server.
- An inconsistent stored trust state is treated as first contact instead of stopping the service.
- Inventory: OS (product, edition, display version, version with UBR), domain, roles, update source (WSUS reported as warning) and installed updates via the WUA API (local cache only, COM objects released explicitly). Collected every 6 hours (configurable), sent on start, on change and at least daily.
- Role detection by rules: built-in rules for domain controller, Exchange, SQL Server, cluster node and Hyper-V; additional rules via `Inventory:RoleRules` without code changes.
- Heartbeat carries the current OS version including UBR.
- Update run on approved agents every 4 hours (configurable): pre-flight checks (disk space, Windows Update service, WSUS warning), online search for available updates via the WUA API and report to the central server; search failures are reported with their error code.
- Fetches the released updates when the release version announced in the heartbeat changes.
- Installation of released updates inside maintenance windows (default Mon–Fri 19:00–05:00, Sat/Sun all day, configurable): batches with pull-forward tolerance, no retry loop (retry delay), health check first, global pause respected, one WUA download/install cycle per batch. Reboots only when the central policy allows it (see below); otherwise a needed reboot is reported.
- Install reports are buffered on disk and delivered when the central server is reachable again.
- Uses the maintenance windows delivered by the central server; local `Installation:Windows` is only the fallback. Invalid central windows are rejected.
- Reboots inside the maintenance window when the central reboot policy allows it: pending reboot detected from Windows (also after updates installed by others), at least 30 minutes left in the window, 5-minute warning via shutdown.exe, at most one request per 6 hours, never during a global pause; local veto via registry value `AllowReboot` (only 1/true allows). Heartbeat reports pending reboot and last boot time.
- Runs remote actions announced in the heartbeat and confirms each one; unknown actions are rejected. Reports its Windows time zone with the inventory.
- In coordinated groups installs and reboots only with a lease; keeps it across its own restart and releases it with the outcome.
- Reports its host name with every heartbeat (clone detection).

### Central server

- `POST /api/v1/agents/register`: issues client GUID and client secret, stores the client as `Pending`; rate limited per IP address.
- `POST /api/v1/agents/heartbeat`: authenticated with HTTP Basic (GUID + secret), records the heartbeat and returns the registration state.
- Whitelist of message types per registration state, enforced centrally for every agent endpoint.
- SQLite database via EF Core with automatic migrations on startup; client secrets stored only as SHA-256 hashes.
- Input validation for all agent messages with RFC 9457 problem details for errors.
- Health endpoint `/health`; OpenAPI description only in the development environment.
- Trust anchor (ECDSA P-256, DPAPI-protected) created on first start; an unreadable anchor stops the service instead of being replaced.
- `GET /api/v1/trust/binding`: anchor-signed binding to the current TLS certificate; a new certificate gets the next serial on start.
- HTTPS only (default port 8443); TLS certificate from the Windows certificate store via `Trust:CertificateThumbprint`.
- Logs local trust resets reported by agents.
- `POST /api/v1/agents/inventory` (also before approval): stores client attributes, roles and installed updates, builds the update catalog from agent reports, logs changes of host name and domain; at most 5,000 updates and 4 MB per request.
- `POST /api/v1/agents/updates` and `GET /api/v1/agents/releases` (approved clients only): stores pre-flight checks, last search result and available updates per client, extends the catalog with download size, reboot behavior and severity; delivers releases with a version that the heartbeat announces.
- Enum values in agent messages are validated (undefined numeric values are rejected).
- `POST /api/v1/agents/installations` (approved clients only): installation history per server and update; only results for updates released to the client are accepted; duplicate runs are stored once; reboot pending flag.
- Global pause, switched by an operator on the overview page (audited) or forced by `Installation:GlobalPause`, announced in the heartbeat.
- Web administration (Blazor, static server rendering, no scripts): local accounts with the roles Reader, Operator and Admin (ASP.NET Core Identity, 12+ character passwords, lockout after 5 failures), one-time browser setup of the first administrator with a token readable only by local administrators, sign-in and sign-out, overview page.
- Append-only audit log with SHA-256 hash chain; the database rejects changes and deletions, the audit page verifies the chain.
- Strict security headers (content security policy without scripts, no framing), antiforgery protection, local-only return URLs after sign-in.
- Approval of new servers: page "Pending approvals" (Operator) to approve or reject, each decision audited and possible only once; optional rule-based approval for configured domains, verified by the central server itself (enabled computer account in Active Directory via LDAP, source IP address matches DNS).
- Rings (Pilot, Test, Production; Production is the final and fallback ring), groups and assignment rules (role, domain, host name pattern; priority; manual overrides per server), recalculated on registration, inventory, administrative changes and start.
- Maintenance windows on three levels (system default, group, server) maintained in the dashboard; the effective windows are delivered with the release list and increase its version when they change.
- Dashboard pages: Servers and server detail (Reader), Groups, Assignment rules and Maintenance windows (Admin); every change audited.
- Ring logic per update: start rules (classification, MSRC severity, title pattern; automatic with optional delay in hours, or manual) decide how a new update enters Pilot, by default every update starts automatically without delay; operators can start queued updates; transitions Pilot → Test → Production by minimum/maximum wait time, feedback rate and success rate (evaluated separately), manual gate (default on before Production), alarm on a poor success rate; gate approval, stop/resume and manual override to all rings; every action audited.
- Releases are derived from the rollout state: an approved server gets an update released for its ring that it reported as missing, until its inventory reports it installed; ring changes move releases.
- Dashboard pages: Updates (rollouts per ring and queued updates), ring transitions under Pending approvals, Administration → Release rules.
- Dashboard redesigned after the mockup (navigation sidebar, cards, charts drawn on the server as SVG, still without scripts; usable at phone width): overview with "Needs attention", key figures, deployment by ring and patch compliance trend (daily snapshot); ring detail page; server list with health and filters; server detail with pre-flight checks, missing updates and update history.
- Update detail page: progress per ring with rates, timeline, affected servers filterable by status, and rollout actions for operators.
- Reboot policy per system default, group and server ("Never" or "inside the maintenance window", default Never; among groups "Never" wins), delivered with the release list; server detail shows effective policy, last boot and whether a required reboot happened.
- Remote action queue: operators queue "search now", "full resync", "restart agent service" and "request agent logs" (ZIP of the last 3 days, at most 10 MB, stored with restricted access for 14 days, audited download), administrators "restart server" with confirmation, per server (audited, at most 10 open, cancellable until fetched, expiry after 24 h / 1 h); the heartbeat announces pending actions, `GET /api/v1/agents/actions` and `POST /api/v1/agents/actions/{id}/result` deliver and confirm them.
- Server time zone from the inventory (known IDs only); the server detail page shows the next maintenance window as a date.
- Coordinated groups with leases: share in percent with an upper bound (default 25 %, at most 5, at least 1), 2-hour leases, all-or-nothing across several coordinated groups, stop after failed or expired leases in a row (capped by group size), resume by operators, audited.
- AD coverage: enabled server accounts of the domain (Windows Server 2016 or newer only; older ones listed separately) compared with registered agents every 6 hours; coverage rate on the overview, page with servers without agent.
- User management for administrators (create, role, password, lock, delete; the last administrator is protected), server removal (delete keeping the history, delete with history) and clone detection (credentials used by two hosts in turn block the server until an operator decides).
- TLS certificate change in the dashboard (Admin, with confirmation): lists suitable certificates from LocalMachine\My, switches without restart and signs the next trust binding, so agents accept it automatically; audited.
- Windows Update error codes are translated into plain English text centrally.
- Own TLS certificate: without a configured certificate the central server creates a self-signed one (RSA 3072, non-exportable machine key, one year) and rolls it over 30 days before expiry with a new key. Renewals of a CA certificate (autoenrollment or manual) are picked up every 15 minutes and at start, also after Windows archived the old certificate. A missing configured certificate falls back to the own one instead of stopping the service. Every change signs the next trust binding and is audited; lifetimes and interval under `Certificate:*`.

### Installers and CI

- MSI packages for agent and central server (WiX Toolset 7, self-contained publish, Windows Server 2016 or newer): Windows services under LocalSystem with restart on failure (agent also on error exit codes), options `SERVERURL` (agent) and `CERTTHUMBPRINT`/`HTTPSPORT` (central server, read from `HKLM\SOFTWARE\UpdateCoordinator\Server`), firewall rule for the HTTPS port, upgrades keep options and data, data under ProgramData kept on uninstall. The central server service also starts without `CERTTHUMBPRINT` (own certificate); the CI install test covers that path.
- `build/Build-Installers.ps1` builds both MSIs; the GitHub Actions workflow builds and tests every push and installs, upgrades and uninstalls both MSIs on the runner (`build/Test-Installers.ps1`).

### Shared

- Project skeleton on .NET 10 with shared message contracts.
- Test projects with xUnit v3 on Microsoft.Testing.Platform, including end-to-end tests of the agent client against the in-memory central server.
- Shared build settings: warnings as errors, .NET analyzers, central package versions, NuGet audit against known vulnerabilities.
- Versions derived from git tags via MinVer.
- `UpdateCoordinator.Common`: DPAPI-protected file storage shared by agent and central server.
- Rolling file logs for agent and central server (Serilog): one file per day and per 10 MB, 31 files kept, configurable via `Logging:File`; warnings and errors also in the Windows event log.
- Negative tests for every input path: HTTP messages, server responses, registry values (including invalid central server addresses), configuration and stored files.
- End-to-end tests over a real TLS connection (certificate pinning, certificate change, impostor server).

### Decided

- Supported from Windows Server 2016 on the latest .NET.
- Trust in the central server via a trust anchor with trust on first use and a local registry override; no runtime data in the client installer (`docs/03-communication-protocol.md`, section 3.2).
- Client secret issued at registration in addition to the GUID (`docs/03-communication-protocol.md`, section 3.1).
- All project artifacts (code, logs, documentation) are written in English.
- The agent reports no SID; rule-based approval will verify domain membership on the central server against AD and DNS.
- Central server address via registry, installer option `SERVERURL` and installer file name on download from the central server (`docs/03-communication-protocol.md`, section 3.3).
