# Changelog

All notable changes are documented here. Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Agent and central server are versioned separately ([Semantic Versioning](https://semver.org/), git tags `agent/vX.Y.Z` and `server/vX.Y.Z`).

## [Unreleased]

### Agent

- Registers once at the central server and stores client GUID and client secret encrypted with DPAPI under `%ProgramData%\UpdateCoordinator\Agent`.
- Sends a heartbeat every 10 seconds (configurable) and logs changes of the registration state.
- Registers again automatically if the central server no longer accepts its credentials.
- Backs off exponentially (up to 5 minutes, configurable) while the central server is unreachable; timeouts and circuit breaker via `Microsoft.Extensions.Http.Resilience`. No error stops the service.
- Accepts only HTTPS addresses for the central server and validates every server response.

### Central server

- `POST /api/v1/agents/register`: issues client GUID and client secret, stores the client as `Pending`; rate limited per IP address.
- `POST /api/v1/agents/heartbeat`: authenticated with HTTP Basic (GUID + secret), records the heartbeat and returns the registration state.
- Whitelist of message types per registration state, enforced centrally for every agent endpoint.
- SQLite database via EF Core with automatic migrations on startup; client secrets stored only as SHA-256 hashes.
- Input validation for all agent messages with RFC 9457 problem details for errors.
- Health endpoint `/health`; OpenAPI description only in the development environment.

### Shared

- Project skeleton on .NET 10 with shared message contracts.
- Test projects with xUnit v3 on Microsoft.Testing.Platform, including end-to-end tests of the agent client against the in-memory central server.
- Shared build settings: warnings as errors, .NET analyzers, central package versions, NuGet audit against known vulnerabilities.
- Versions derived from git tags via MinVer.

### Decided

- Supported from Windows Server 2016 on the latest .NET.
- Trust in the central server via a trust anchor with trust on first use and a local registry override; no runtime data in the client installer (`docs/03-communication-protocol.md`, section 3.2).
- Client secret issued at registration in addition to the GUID (`docs/03-communication-protocol.md`, section 3.1).
- All project artifacts (code, logs, documentation) are written in English.
