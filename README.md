# UpdateCoordinator (working title)

Central coordination of Windows updates for Windows servers with ring deployment, central configuration and reporting, as an alternative to WSUS. The project name is still open; `UpdateCoordinator` is a placeholder.

> Status: agents register, verify the central server through its trust anchor, send heartbeats and their inventory (OS, roles, installed updates). Approval, update search and installation follow.

## Components

| Project | Responsibility |
|---|---|
| `src/UpdateCoordinator.Agent` | Windows service on every managed server (WUA API) |
| `src/UpdateCoordinator.Server` | Central server: Windows service with a web API for agents and the dashboard |
| `src/UpdateCoordinator.Contracts` | Shared message contracts and routes |
| `tests/*.Tests` | Automated tests per project (xUnit v3) |
| `tests/UpdateCoordinator.IntegrationTests` | End-to-end tests: real agent client against the in-memory central server |
| `tests/UpdateCoordinator.TestSupport` | Shared test infrastructure |

Architecture and decisions are documented in [`docs/`](docs/01-architecture.md), the working rules in [`docs/02-guidelines.md`](docs/02-guidelines.md).

## Prerequisites

- Windows (agent and central server are Windows services)
- .NET SDK 10 (version pinned in `global.json`)

## Build and test

```
dotnet build UpdateCoordinator.slnx
dotnet test --solution UpdateCoordinator.slnx
```

The build treats warnings as errors, enforces code style and the security rules of the .NET analyzers, and fails on packages with known vulnerabilities.

## Running locally

1. Create the ASP.NET Core development certificate once: `dotnet dev-certs https` (it does not need to be trusted: the agent trusts the central server through its trust anchor)
2. Start the central server: `dotnet run --project src/UpdateCoordinator.Server` (listens on https://localhost:7190; database `server.dev.db` and trust anchor `trust-anchor.dev.key` in the project folder)
3. Start the agent in a second terminal: `dotnet run --project src/UpdateCoordinator.Agent` (development settings point to the local central server)

The agent pins the trust anchor of the central server, registers, stores its identity and trust state under `%ProgramData%\UpdateCoordinator\Agent` and sends a heartbeat every 10 seconds. Both components write daily rolling logs to `%ProgramData%\UpdateCoordinator\<component>\Logs`. EF Core migrations are created with the local tool (`dotnet tool restore` once): `dotnet ef migrations add <Name> --project src/UpdateCoordinator.Server --output-dir Data/Migrations`.

## Versioning

Agent and central server are versioned separately from git tags (`agent/v1.2.3`, `server/v1.2.3`) via MinVer; untagged builds get a pre-release version with the commit hash.

## Project structure and conventions

- `Directory.Build.props`: shared settings for all projects (target framework, analyzers, NuGet audit)
- `Directory.Packages.props`: all package versions in one place
- `tests/Directory.Build.props`: shared settings for the test projects
- Internal types are automatically visible to the matching `*.Tests` project.
- Everything in the repository is written in English.

## Documentation

- [CHANGELOG.md](CHANGELOG.md)
- [Admin documentation](docs/admin/README.md)

## License

[MIT](LICENSE)
