# SUSyphos

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
- Build: `dotnet build SUSyphos.slnx`; tests: `dotnet test --solution SUSyphos.slnx` (Microsoft.Testing.Platform, configured in `global.json`).

## Decisions made

- Supported from Windows Server 2016; agent and central server use the latest .NET (currently .NET 10).
- Trust in the central server via a trust anchor with trust on first use and a local registry override; no runtime data in the client installer (details in `docs/03-communication-protocol.md`, section 3.2).
- Clients authenticate with GUID + client secret issued at registration (HTTP Basic over HTTPS, `docs/03-communication-protocol.md` 3.1).
- Separate semantic versions for agent and central server from git tags `agent/vX.Y.Z` / `server/vX.Y.Z` (MinVer); protocol version in the route (`/api/v1`).
- Work directly on `main` (no feature branches or pull requests) until a requirement justifies it.
- Central server address: registry value `CentralServerUrl` (policy key before installer key), written by installer option `SERVERURL` or from the installer file name when downloaded from the central server (`docs/03-communication-protocol.md` 3.3).
- Public repository https://github.com/debold/UpdateCoordinator under the MIT license (may be renamed to SUSyphos by Marc; the agent package check reads `AgentPackages:Repository`).
- Installers: MSI packages built with WiX Toolset 7, self-contained publish; CI on GitHub Actions installs them on the runner (Marc, 2026-10-19). Marc accepted the WiX Open Source Maintenance Fee EULA (no revenue from this project); revisit if that changes.
- Agent operating limits (Marc, 2026-10-19): about 200 MB private memory (restart above it), below-normal process priority, backoff up to 5 minutes; configurable under `Resources:*` and `CentralServer:MaxBackoff`.
- Windows Automatic Updates (Marc, 2026-10-20): the agent may change the local setting so Windows does not install on its own, but only when configured centrally (opt-in); otherwise it only detects and warns (`docs/05-agent-design.md`, section 3). Since 2026-10-29 (lab evidence on Server 2019): the opt-in writes `NoAutoUpdate = 1` (option 3 still downloaded and offered updates), "not configured" warns on Windows Server too, Windows Update for Business deferrals are warned about, and uninstalling the agent takes back its own changes (`--cleanup`).
- Agent download and updates (Marc, 2026-10-20): the downloaded agent installer carries address and trust anchor fingerprint in its file name; the interactive agent setup asks for the address. The central server checks the GitHub releases for new agent versions (daily and on demand), and an administrator releases them ring by ring or for all rings at once; no automatic release while the packages are not code-signed (no certificate yet).
- Project name SUSyphos (Marc, 2026-10-20); renamed from the working title UpdateCoordinator in code, packages, services, registry and data folders. No migration of installations from before the rename (only test installations existed); they are installed anew.
- Operating limits of the central server (Marc, 2026-10-20): none for now; evaluated over time in operation.
- Trust anchor in the backup (Marc, 2026-10-20): the encryption with the backup password is sufficient.
- Script in the dashboard (Marc, 2026-10-20): one own script file served by the central server (`script-src 'self'`, no inline code, no external sources) as progressive enhancement; every page must keep working without it.
- AD sign-in (Marc, 2026-10-21): Windows-integrated sign-in (Negotiate) next to local accounts; AD group to role mapping in the dashboard (Identity page, formerly Users), stored by SID; config switch `Authentication:Windows:Enabled` as emergency off.
- Move to a new central server (Marc, 2026-10-22): the old server announces the new address; an agent switches only if the new server proves the same trust anchor (restored from the backup) and knows its credentials, otherwise it stays and reports why; a move with a new anchor stays a manual new onboarding (`docs/03-communication-protocol.md` 3.4).
- Dashboard design patterns (Marc, 2026-10-25): tiles (title, one or two centred values, pill or donut for states, one-line footer below a separator, coloured edge for attention) and action bands (grouped drop-down buttons where a topic has several actions, dangerous actions red on the right) are binding for every page, as shared components (`docs/06-dashboard.md`, section 8).
- Support lifecycle table (Marc, 2026-10-26): the support dates of the Windows versions are built in (`src/SUSyphos.Server/Lifecycle/lifecycle.json`), updated with each release, not fetched online. Every release checks it automatically: the table carries the review date and the expected date of the next Windows Server release, and a test fails when that date has passed without a newer version in the table. Focus is Windows Server; Windows 10/11 only get names and end of support, no security baseline.
- Security baseline defaults (Marc, 2026-10-26): "too far behind" after the sum of the ring waits + 7 days (`Security:BehindDays`), fleet warning at 20 % behind, MSRC data of the last 3 months.
- ESU (Marc, 2026-10-26): Azure Arc alone does not count as ESU; besides an ESU key or Azure, ESU is inferred when the server installed a security update released after the end of support.
- Uninstall, hold and one-time windows (Marc, 2026-10-27, concept https://claude.ai/artifact/SLp51NmbiEGMdWSu1guVe2): uninstall from the update page for a server, ring, group or all affected servers, now or in the next maintenance window; in coordinated groups every uninstall and the restart after it wait for the lease, also when pushed now; "all affected" preselects blocking the update; hold of an update per ring, group or server with optional end date; one-time maintenance windows (date, from–to) on every level, valid for all released updates (no window per update; such cases are planned manually); preview of the next 30 days on every level, fixed at 30 days; hold for Operator and Admin, uninstall and one-time windows for Admin only.
- Times (Marc, 2026-10-20): the dashboard shows and takes all times in the time zone of the central server, 24-hour format; agents, protocol and database stay in UTC.
- Dashboard: Blazor with static server rendering by default (interactive only where it adds real value, e.g. live status), ASP.NET Core Identity with local accounts, first administrator via one-time browser setup with a local token (Marc, 2026-10-21: the interactive central server installer creates the token, shows it to copy and opens the setup with it after `#` in the address; handed over in a registry key only SYSTEM and administrators can read), permissions as sketched in `docs/06-dashboard.md`.

## Open decisions (do not decide unilaterally)

- Concrete database schema of the central server
- Permission assignment per function for Operator and Admin
- Escalation of notifications (channels decided: e-mail via SMTP and a generic webhook, Marc 2026-10-19)
