# Guidelines and Working Rules for the Implementation

> Valid project-wide, independent of the individual feature. Applies to every implementation, in particular by agentic AI.

## 1. Language

- Everything in the project is **English**: code, comments, log messages, documentation (README, changelog, admin documentation, `docs/`), commit messages.

## 2. Code quality and currency

- High code quality is mandatory.
- Use **current versions** of .NET and all components from the start of the project, no outdated versions.
- Regularly check dependencies for updates (close security gaps early, use new features).

## 3. Reuse instead of building

- Rely on existing, established components and libraries as much as possible.
- Do not hand-code protocol and format handling (REST/JSON/HTTPS via ASP.NET Core, validation and OpenAPI via framework features).

## 4. Security from the start

- Mandatory **code reviews with a security focus** for every change.
- **Validate all input** (protection against code injection, SQL injection etc.), including input from agents.
- Parameterized queries, no string concatenation in SQL.
- Incoming messages are checked against the whitelist of allowed message types per registration state (see `03-communication-protocol.md`).

## 5. No duplicates (DRY)

- Do not implement functionality that is used in several places more than once.
- Refactor promptly and extract centrally so it is maintained only once.
- Example: register and inventory remain separate messages, each with its own reusable logic.

## 6. Tests

- **Everything** is tested automatically. This is the most important quality goal.
- Tests are implemented **together with the feature**, not afterwards.
- Every feature has **positive and negative** test cases.

## 7. Documentation

- Changelog.
- README.
- Admin documentation as a guide and information on setup and communication paths: who communicates with whom, when, and over which protocols.

## 8. CI/CD

- Later a pipeline on GitHub: automated tests, automated build of the installer, eventually automated live tests.

## 9. Operational requirements (stability)

The services should be 99.9 % available, fault tolerant, able to run permanently, free of memory leaks and secure. **Server operation must never be put at risk.** Specifically:

- Robust exception handling at all levels: a single error must never terminate the whole service.
- Retries with backoff for communication with the central server.
- Circuit breaker: if the central server is permanently unreachable, the agent backs off and tries again later instead of running in an error loop.
- Defined limits for memory and CPU usage.
- Clean dispose pattern for all resources, in particular the COM objects of the WUA API (historically prone to memory leaks).
- The agent never triggers a reboot or any action that endangers operation unless it is explicitly configured and allowed in the maintenance window.
- A monitoring mechanism (watchdog/service recovery) restarts the service automatically if it hangs.

> Note: the operational requirements in section 9 were proposed in the spec session and confirmed. Agent limits are defined (200 MB private memory with restart, below-normal priority, backoff up to 5 minutes; `docs/admin/README.md`, configuration); limits of the central server are deliberately not defined yet and are evaluated over time in operation (Marc, 2026-10-20).
