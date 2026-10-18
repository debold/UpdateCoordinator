# UpdateCoordinator (working title)

Central coordination of Windows updates for Windows servers with ring deployment, central configuration and reporting, as an alternative to WSUS. The project name is still open; `UpdateCoordinator` is a placeholder.

> Status: project skeleton. Agent and central server start but have no functionality yet. Next is the vertical slice with register and heartbeat.

## Components

| Project | Responsibility |
|---|---|
| `src/UpdateCoordinator.Agent` | Windows service on every managed server (WUA API) |
| `src/UpdateCoordinator.Server` | Central server: Windows service with a web API for agents and the dashboard |
| `src/UpdateCoordinator.Contracts` | Shared message contracts and routes |
| `tests/*` | Automated tests per project (xUnit v3) |

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

## Project structure and conventions

- `Directory.Build.props`: shared settings for all projects (target framework, analyzers, NuGet audit)
- `Directory.Packages.props`: all package versions in one place
- `tests/Directory.Build.props`: shared settings for the test projects
- Internal types are automatically visible to the matching `*.Tests` project.
- Everything in the repository is written in English.

## Documentation

- [CHANGELOG.md](CHANGELOG.md)
- [Admin documentation](docs/admin/README.md)
