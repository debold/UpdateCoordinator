# Build from source

Developer notes for SUSyphos: project layout, building and testing, running locally, installers and versioning. Administrators find setup and operation in the [admin documentation](admin/README.md).

## Components

| Project | Responsibility |
|---|---|
| `src/SUSyphos.Agent` | Windows service on every managed server (WUA API) |
| `src/SUSyphos.Server` | Central server: Windows service with a web API for agents and the dashboard |
| `src/SUSyphos.Contracts` | Shared message contracts and routes |
| `src/SUSyphos.WinGet` | Access to WinGet (in-proc COM API, uninstall inventory, App Installer bootstrap), for the agent later |
| `tools/SUSyphos.WinGetProbe` | Lab tool for the WinGet tests, not released ([guide](admin/winget-probe.md)) |
| `tests/*.Tests` | Automated tests per project (xUnit v3) |
| `tests/SUSyphos.IntegrationTests` | End-to-end tests: real agent client against the in-memory central server |
| `tests/SUSyphos.TestSupport` | Shared test infrastructure |

Architecture and decisions are documented in [`docs/`](01-architecture.md), the working rules in [`docs/02-guidelines.md`](02-guidelines.md).

## Prerequisites

- Windows (agent and central server are Windows services)
- .NET SDK 10 (version pinned in `global.json`)

## Build and test

```
dotnet build SUSyphos.slnx
dotnet test --solution SUSyphos.slnx
```

The build treats warnings as errors, enforces code style and the security rules of the .NET analyzers, and fails on packages with known vulnerabilities.

`tests/SUSyphos.UiTests` checks the dashboard in a real browser: Playwright drives the installed Microsoft Edge (headless; no browser download) against a central server with a demo lab. Every page is checked in four window sizes (1920, 1440, 1280 and 1024 pixels wide) for sideways scrolling, cut-off text, elements sticking out of their box, overlapping controls, unlabelled buttons, browser and content-security-policy errors and broken links; tables must fit without scrolling from 1280 pixels on. Further tests open every dialog, every column filter and use the script features. A failing page leaves a full-page screenshot in the test output folder under `ui-failures`. `HelpCoverageTests` checks that every visible field, button and switch of every page and dialog leads to a help section that names it, and `HelpScreenshotTests` takes the screenshots of the help (`build/Update-HelpScreenshots.ps1` writes them anew). Without Edge the UI tests are skipped.

## Running locally

1. Create the ASP.NET Core development certificate once: `dotnet dev-certs https` (it does not need to be trusted: the agent trusts the central server through its trust anchor)
2. Start the central server: `dotnet run --project src/SUSyphos.Server` (listens on https://localhost:7190; database `server.dev.db` and trust anchor `trust-anchor.dev.key` in the project folder)
3. Start the agent in a second terminal: `dotnet run --project src/SUSyphos.Agent` (development settings point to the local central server)

The agent pins the trust anchor of the central server, registers, stores its identity and trust state under `%ProgramData%\SUSyphos\Agent` and sends a heartbeat every 10 seconds. Both components write daily rolling logs to `%ProgramData%\SUSyphos\<component>\Logs`. EF Core migrations are created with the local tool (`dotnet tool restore` once): `dotnet ef migrations add <Name> --project src/SUSyphos.Server --output-dir Data/Migrations`.

## Installers and CI

`./build/Build-Installers.ps1` builds the MSI packages of agent and central server (WiX Toolset, self-contained) into `artifacts/installers`. The GitHub Actions workflow `.github/workflows/ci.yml` builds and tests every push and installs, upgrades and uninstalls both MSIs on the runner. Installation options: see the admin documentation, section 17.

## Versioning

Agent and central server are versioned separately from git tags (`agent/v1.2.3`, `server/v1.2.3`) via MinVer; untagged builds get a pre-release version with the commit hash.

## Project structure and conventions

- `Directory.Build.props`: shared settings for all projects (target framework, analyzers, NuGet audit)
- `Directory.Packages.props`: all package versions in one place
- `tests/Directory.Build.props`: shared settings for the test projects
- Internal types are automatically visible to the matching `*.Tests` project.
- Everything in the repository is written in English.

## Licenses of third-party assets

The dashboard icons are from [Lucide](https://lucide.dev) (ISC license, [assets/icons/lucide/LICENSE](../assets/icons/lucide/LICENSE)); `build/New-IconData.ps1` turns the SVG files into the table the dashboard renders inline.
