# Changelog

All notable changes are documented here. Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), versioning according to [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- Project skeleton with agent (Windows service), central server (ASP.NET Core as a Windows service) and a shared contracts project on .NET 10.
- Test projects with xUnit v3 on Microsoft.Testing.Platform, including integration tests of the central server through the HTTP pipeline.
- Health endpoint `/health` on the central server.
- OpenAPI description of the central server, exposed only in the development environment.
- Shared build settings: warnings as errors, .NET analyzers, central package versions, NuGet audit against known vulnerabilities.

### Decided

- Supported from Windows Server 2016 on the latest .NET.
- Trust in the central server via a trust anchor with trust on first use and a local registry override; no runtime data in the client installer (`docs/03-communication-protocol.md`, section 3.2).
- All project artifacts (code, logs, documentation) are written in English.
