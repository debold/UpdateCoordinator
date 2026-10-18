# Agent Design (Client)

> See `01-architecture.md` for the overall context, `03-communication-protocol.md` for the messages and `02-guidelines.md` for operational and quality requirements.

## 1. Basics

- Modern **Windows service in C#**, no PowerShell scripting, no script-based access paths.
- Update access via the **WUA API** (COM). Supported from Windows Server 2016 on the latest .NET (see `01-architecture.md`).
- **Self-directed:** the agent does not wait for real-time instructions but decides locally within the releases allowed by the central server.
- All outgoing information is buffered locally if the central server is unreachable or does not acknowledge.

## 2. Inventory

### Scope

- System information and inventory of **already installed updates** on first start.
- **Build number/UBR** is read and transmitted on **every** sync cycle, because it changes with every update.
- No hardware information (RAM, CPU etc.) for now; focus on OS version/build and update status.
- The **search for newly available updates** is a separate, later step.
- The **update history** (`QueryHistory`) is not part of the initial inventory but of cyclic reporting and is synchronized continuously.

### Full resync

In addition to the regular cycle there is a full resync of the inventory data against drift, both **scheduled** and triggerable **centrally on demand**.

### Role detection

The inventory contains detected server roles so automatic group rules can apply:

- **Domain controller** via the LDAP configuration
- **Exchange**, **SQL Server**, **cluster membership**, **Hyper-V host** via the client inventory

In addition the agent reports domain membership and SID (basis of rule-based approval, see `03-communication-protocol.md`).

## 3. Cycle (fixed pipeline)

1. **Pre-flight health check**
2. **Update search**
3. **Reporting** to the central server
4. **Installation** (only for updates released by the central server)

### Pre-flight health check

Before every update run: internet connectivity or reachability of the update source, success of the update search, sufficient free disk space and further prerequisite checks. A detected **WSUS configuration** is reported as a **warning** (WSUS is not desired).

### Update source

Configurable per server whether to search directly against Windows Update or against WSUS (technically via `ServerSelection` of `IUpdateSearcher`).

## 4. Installation

- The central server sends releases as a package of **update ID + earliest installation time**.
- The agent forms **batches** locally and respects blackout and maintenance windows.
- **Bundling:** several updates due in the same window are installed in one shared download/install/reboot cycle.
- **Pull-forward tolerance (configurable):** updates whose earliest time lies shortly after the start of the current batch are pulled into the batch (if the window allows) to avoid additional reboots. A batch that is already due is not delayed artificially.
- **Global pause:** the agent starts no new installations but continues health check, search and reporting.
- **Manual override per update:** the update is placed into the next possible installation window.
- **Optional random delay** for ungrouped servers.
- **Lease** only for servers in a coordinated group: the agent waits for the lease before installing; all others install on their own.
- Reboots only as configured and within the allowed window (see `02-guidelines.md`).

## 5. Reporting

### Per update after an installation run

Update ID with revision, ResultCode, HResult error code, reboot required/performed, start and end time.

### Per server

New build number, overall status of the run, **post-install health check** (reachability/health after the reboot) as the counterpart to the pre-flight check.

### Troubleshooting failures

- Automatically the relevant entries from the event log `Microsoft-Windows-WindowsUpdateClient/Operational` for the affected time window.
- Optionally, on demand, upload of `CBS.log` for persistent cases.

The HResult is translated into plain text **centrally**, not in the agent.

### Heartbeat

Periodic, independent of update cycles (guideline 5–15 s); also serves as polling of the action queue.

### Live status

Detailed status, with percentage progress where possible:

- Installation window started, pending installations, next scheduled run
- Download running (%), installation running (%), reboot started/completed (no percentage, only start/end), inventory running
- Concrete **list of update IDs** of the current installation window

Download and installation progress can be read as a percentage via the WUA API.

### Preview

The central server can see the **planned installations of the next maintenance window** per server, not only status and history.

### Detection of external updates

Updates installed outside our own maintenance are detected and reported with their source.

## 6. Remote actions

The agent fetches actions from the queue, executes them in the given order and reports status with progress: restart service, immediate update search with report, restart server, check-in with agent update search, full resync, later log retrieval.

## 7. Agent self-update

- The agent asks **only the central server** for updates.
- If it is below the minimum version, it requests and installs an update on its own.
- Repeated failure is made visible in the central server and escalated.

## 8. Installation and uninstallation

- The address of the central server comes from the registry (group policy before installer value); the installer writes it from the `SERVERURL` option or from its own file name when downloaded from the central server (see `03-communication-protocol.md` 3.3).
- On uninstallation the agent sends an **unregister** to the central server before the service stops (relevant also for unapproved clients).

## 9. Stability

The operational requirements from `02-guidelines.md` apply: 99.9 % availability, fault tolerance, no memory leaks (in particular clean disposal of the COM objects), retries with backoff, circuit breaker, resource limits, watchdog, no actions endangering operation without configuration.
