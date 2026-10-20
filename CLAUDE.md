# Windows Update Coordinator (working title)

## Goal

Central coordination of Windows updates for Windows servers with ring deployment, central configuration and reporting. A deliberate alternative to WSUS (classified as deprecated by Microsoft since September 2024), later usable in arbitrary customer environments.

## Context

- Two components: **agent** (lightweight Windows service in C# on every server, uses the WUA API) and **central server** (native Windows service with a web API for agents and a web dashboard, SQLite for storage).
- Communication: REST/JSON over HTTPS with ASP.NET Core. The agent initiates every connection; the central server never calls an agent.
- The central server decides per ring which updates are released when. The agent decides locally when to install within the allowed windows.
- New agents are onboarded only through an approval mechanism (manual or rule-based), without a pre-shared secret or certificate on the client side. Clients are identified by a centrally issued GUID plus a client secret issued at registration; the agent verifies the central server through a pinned trust anchor (trust on first use, no runtime data in the installer).
- The design was created in a spec session. The documents in `docs/` are the authoritative source.

## Language

- Everything in the repository is English: code, comments, log messages, documentation, commit messages.
- Conversation with Marc in chat is in German.

## Documents (read when needed, not all at once)

- `docs/01-architecture.md`: overview, decisions, implementation order (read first)
- `docs/03-communication-protocol.md`: messages, registration, identity, trust
- `docs/04-central-server.md`: entities, ring logic, administration security, backup
- `docs/05-agent-design.md`: inventory, pipeline, installation, reporting
- `docs/06-dashboard.md`: user interface and administration
- `docs/admin/README.md`: admin documentation (setup, communication paths)
- `docs/admin/test-guide.md`: walk-through for a domain lab test

Guidelines (always apply):

@docs/02-guidelines.md

## Way of working

- Order according to `docs/01-architecture.md`: first the vertical slice with register and heartbeat, then inventory, then update functions, finally ring logic and dashboard.
- Present a short plan before each feature. Ask instead of guessing when something is unclear.
- Tests (positive and negative) and documentation (changelog, README, admin documentation) are part of finishing every feature.
- Every change gets a security review, especially input validation and SQL.
- If the code contradicts the documents, report it and update the document deliberately instead of silently working around it.
- Build: `dotnet build UpdateCoordinator.slnx`; tests: `dotnet test --solution UpdateCoordinator.slnx` (Microsoft.Testing.Platform, configured in `global.json`).

## Decisions made

- Supported from Windows Server 2016; agent and central server use the latest .NET (currently .NET 10).
- Trust in the central server via a trust anchor with trust on first use and a local registry override; no runtime data in the client installer (details in `docs/03-communication-protocol.md`, section 3.2).
- Clients authenticate with GUID + client secret issued at registration (HTTP Basic over HTTPS, `docs/03-communication-protocol.md` 3.1).
- Separate semantic versions for agent and central server from git tags `agent/vX.Y.Z` / `server/vX.Y.Z` (MinVer); protocol version in the route (`/api/v1`).
- Work directly on `main` (no feature branches or pull requests) until a requirement justifies it.
- Central server address: registry value `CentralServerUrl` (policy key before installer key), written by installer option `SERVERURL` or from the installer file name when downloaded from the central server (`docs/03-communication-protocol.md` 3.3).
- Public repository https://github.com/debold/UpdateCoordinator under the MIT license.
- Installers: MSI packages built with WiX Toolset 7, self-contained publish; CI on GitHub Actions installs them on the runner (Marc, 2026-10-19). Marc accepted the WiX Open Source Maintenance Fee EULA (no revenue from this project); revisit if that changes.
- Agent operating limits (Marc, 2026-10-19): about 200 MB private memory (restart above it), below-normal process priority, backoff up to 5 minutes; configurable under `Resources:*` and `CentralServer:MaxBackoff`.
- Windows Automatic Updates (Marc, 2026-10-20): the agent may change the local setting so Windows does not install on its own, but only when configured centrally (opt-in); otherwise it only detects and warns (`docs/05-agent-design.md`, section 3).
- Dashboard: Blazor with static server rendering by default (interactive only where it adds real value, e.g. live status), ASP.NET Core Identity with local accounts, first administrator via one-time browser setup with a local token, permissions as sketched in `docs/06-dashboard.md`.

## Open decisions (do not decide unilaterally)

- Project name (placeholder in code: `UpdateCoordinator`)
- Concrete database schema and operating limits of the central server (agent limits decided, see Decisions made)
- Permission assignment per function for Operator and Admin
- Escalation of notifications (channels decided: e-mail via SMTP and a generic webhook, Marc 2026-10-19)
- Encryption of the trust anchor in the backup
- Whether the download file name also carries the trust anchor fingerprint (`docs/03-communication-protocol.md` 3.3)
