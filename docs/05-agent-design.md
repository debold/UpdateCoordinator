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

The inventory contains detected server roles so automatic group rules can apply. Roles are detected by **rules**: each rule names a role and a list of conditions, and the role is detected if any condition matches. Condition kinds: registry key exists, registry value exists (optionally with an expected value, case-insensitive) and Windows service installed; all paths are relative to HKLM and read-only. Built-in rules (decided 2026-10-18):

| Role | Detection |
|---|---|
| `DomainController` | `ProductType` = `LanmanNt` under `SYSTEM\CurrentControlSet\Control\ProductOptions` (local and reliable; replaces the earlier idea of reading the LDAP configuration) |
| `Exchange` | key `SOFTWARE\Microsoft\ExchangeServer\v15\Setup` or service `MSExchangeIS` |
| `SqlServer` | key `SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL` |
| `ClusterNode` | service `ClusSvc` or key `Cluster` |
| `HyperVHost` | service `vmms` |

**Extensible without code changes:** additional rules come from configuration (`Inventory:RoleRules`). A configured rule with a new name adds a role; a rule with the name of a built-in role adds conditions to it. Invalid rules stop the agent at startup with a clear error.

In addition the agent reports its domain membership. It deliberately reports no SID: a self-reported value cannot be trusted, so the central server verifies domain membership itself against AD and DNS (see `03-communication-protocol.md`, 4.1).

### Collection and sending

- Installed updates are read through the WUA API (`IUpdateSearcher`, search `IsInstalled=1 and IsHidden=0`, `Online = false`, so only the local cache is used and no internet access is needed). The API is accessed late-bound through the COM support of .NET; every COM object is released explicitly. If the API is not available, the inventory is sent without the update list.
- The inventory is collected after registration, on service start and then every `Inventory:CollectInterval` (default 6 hours). It is only sent if it changed or `Inventory:MaxSendInterval` (default 1 day) has passed. A failed send is retried with the next cycle.
- Values are shaped to the contract before sending (control characters removed, lengths truncated, invalid entries dropped), so a single odd value never blocks the whole inventory; the central server validates again.

## 3. Cycle (fixed pipeline)

1. **Pre-flight health check**
2. **Update search**
3. **Reporting** to the central server
4. **Installation** (only for updates released by the central server)

### Implementation (step A, 2026-10-18)

- The update run (pre-flight check, search, report) starts every `Updates:SearchInterval` (default 4 hours) and only on approved clients. A run counts only once its report was delivered; otherwise the next cycle tries again.
- Pre-flight checks: `DiskSpace` (free space on the system drive below `Updates:MinFreeDiskSpaceMegabytes`, default 4096, is `Failed`; unknown is `Warning`), `UpdateService` (Windows Update service missing or disabled is `Failed`), `UpdateSource` (WSUS by policy is `Warning`), `AutomaticUpdates` (Windows installs or restarts on its own is `Warning`, see below). A `Failed` check skips the search.
- Search: WUA `IsInstalled=0 and IsHidden=0 and Type='Software'`, online, against the configured source (`Updates:Source`: `System` = as configured on the server, `WindowsUpdate` = always Windows Update directly). The same COM access and the same value shaping are used as for the inventory. A failed search is reported with its WUA error code.
- Releases: the agent keeps the release list of the central server in memory and fetches it when the version announced in the heartbeat changes; without approval the list is empty.

### Pre-flight health check

Before every update run: internet connectivity or reachability of the update source, success of the update search, sufficient free disk space and further prerequisite checks. A detected **WSUS configuration** is reported as a **warning** (WSUS is not desired).

### Update source

Configurable per server whether to search directly against Windows Update or against WSUS (technically via `ServerSelection` of `IUpdateSearcher`).

### Windows Automatic Updates

Windows can install updates by itself, independent of the agent. Then updates arrive outside rings and maintenance windows and the coordination is bypassed. The agent **reads** the setting and reports it with the inventory (`updateSource.automaticUpdates`) and as pre-flight check `AutomaticUpdates`; it changes it only on central opt-in (decided by Marc on 2026-10-20, see below).

- Mode: `NoAutoUpdate = 1` in `HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU` is `Disabled`; otherwise `AUOptions` from the policy key, and with option 5 (local administrator chooses) or without policy the local value in `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update`. Unknown values count as not configured. Without configuration Windows Server 2016 and later downloads and notifies (option 3), while Windows 10 and 11 install on their own; the agent reads `ProductType` under `HKLMSYSTEMCurrentControlSetControlProductOptions` (`WinNT` is a client) and reports "not configured" on a client as installing on its own (Marc, 2026-10-23).
- Bypasses the coordination (warning, server status "Warning"): `AUOptions = 4` (download and install on a schedule), `AlwaysAutoRebootAtScheduledTime = 1` or a compliance deadline (`SetComplianceDeadline = 1` in the WindowsUpdate policy key).
- **Opt-in change (implemented 2026-10-20):** when the release list says `manageAutomaticUpdates` (system default or group setting on the central server; one group with "leave unchanged" wins), the agent writes `AUOptions = 3` to the policy key, the way sconfig does, unless a value it did not write is there (group policy or `NoAutoUpdate`; option 5 counts as "local administrator chooses" and is replaced). A marker `ManagedAutomaticUpdates` in the agent installer key records the agent's own value, so it only ever removes its own change when the setting is switched off or the server is no longer approved. The inventory reports `managedByAgent`.
- Recommended: "Configure Automatic Updates" option 3 (download, notify before install; Defender signatures keep updating) or 7 (notify before install and restart), or disabled. On Windows Server 2016 and 2019 a group policy refresh can briefly reset the setting to the default (Microsoft known issue); option 3 or 7 keeps that harmless.

## 4. Installation

### Implementation (step B, 2026-10-18)

- **Maintenance windows:** the effective windows delivered by the central server with the release list (system default, group or server level, see `04-central-server.md` section 4) in local server time. Only if the central server delivered no windows does the agent use its local fallback: the windows configured in `Installation:Windows`, otherwise the system default (Mon–Fri 19:00–05:00, Sat/Sun all day).
- **Batch:** inside a window, a batch starts as soon as at least one release is due (earliest installation time reached) and the update is still missing according to the last search. Releases due within `Installation:PullForwardTolerance` (default 30 minutes) join the batch. Nothing is installed early if nothing is due.
- **No retry loop:** an attempted update is not tried again before `Installation:RetryDelay` (default 6 hours) has passed; attempts survive service restarts.
- **Health first:** a failed pre-flight check prevents the installation; the global pause announced in the heartbeat stops new installations.
- **Installation:** the batch is looked up by update ID and revision, EULAs are accepted, downloaded and installed in one WUA cycle without user prompts. Released updates the source no longer offers are reported as not started.
- **Reboots** (decided 2026-10-18): the agent restarts the server only if the effective reboot policy from the central server is "inside the maintenance window", no local veto applies (`Installation:AllowReboot`, registry value `AllowReboot`; any value other than 1/true forbids), installations are not paused, a maintenance window is open for at least 30 more minutes (adjoining windows count together) and no restart was requested in the last 6 hours. A reboot counts as pending when Windows sets `Component Based Servicing\RebootPending` or `WindowsUpdate\Auto Update\RebootRequired` (also for updates installed by others); `PendingFileRenameOperations` is ignored because many products set it routinely. The restart goes through `shutdown.exe` with a 5-minute warning to signed-in users and is recorded as planned (reason p:2:17). Without a policy the agent never reboots and only reports the pending reboot.
- **Reporting:** each run produces an install report with a run ID. Reports are buffered DPAPI-protected on disk (`installations.dat`, at most 50) and sent with the next successful cycle, so no result is lost while the central server is unreachable. After a run, inventory and update search run again.

### Design

- The central server sends releases as a package of **update ID + earliest installation time**.
- The agent forms **batches** locally and respects blackout and maintenance windows.
- **Not offered any more (2026-10-22):** after a successful search the agent looks up the released updates the search did not find (at most 100) by ID and revision in all four states (installed or not, hidden or not) at the same source; those not found at all go to the central server as `notOffered` (withdrawn by Microsoft, or no longer applicable). A failed lookup reports nothing.
- **Uninstall (2026-10-22):** the remote actions `Uninstall` and `UninstallRestartInWindow` name the update (ID, revision, KB). The agent uninstalls it through WUA (`IUpdateInstaller.Uninstall`) if Windows reports it as uninstallable, otherwise with DISM: the installed package whose name contains the KB, or a `Package_for_RollupFix`/`Package_for_KB` package whose information links the KB, removed with `/Remove-Package /Quiet /NoRestart` (exit code 3010 means a restart completes it). Afterwards inventory and search run again; a needed restart follows at once (5-minute warning) or in the next window, never against the local veto.
- **Supersedence (2026-10-21):** every available update carries the IDs Windows Update reports as superseded by it (`supersededUpdateIds`, at most 500, own ID and invalid values dropped); the central server uses them to replace older updates ring by ring.
- **Outside the windows (Marc, 2026-10-21):** a release marked `outsideWindows` (start rule with the restart condition "no restart" and "install outside the maintenance windows", e.g. definition updates) may be installed at any time once due; the global pause, the health check and leases in coordinated groups still apply, and reboots still wait for a window. The central server marks a release only while Windows reports the update as never restarting.
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

**Implemented (2026-10-20):** search, download, installation and inventory announce themselves in an activity tracker; the heartbeat carries the current activity, and because the main loop waits for these steps, a separate pulse sends it in between (`docs/03-communication-protocol.md`, section 5.1). Download and installation run as asynchronous WUA jobs (`BeginDownload`/`BeginInstall` with callback objects); the agent polls the job every 2 seconds for the overall percentage. If WUA refuses the asynchronous call, the agent logs a warning and uses the synchronous call without progress. The heartbeat also reports why a pending reboot waits (the decision of the reboot planner, section 4), so the central server can show it. Not yet: reboot started/completed as a phase, the next scheduled run.

**Next tasks (2026-10-20):** the heartbeat also carries when the agent searches for updates next (`nextSearchAt`) and collects the inventory next (`nextInventoryAt`); the central server shows them with the installations of the next window and a planned restart as one list of tasks. It ignores times more than a day late or more than 30 days ahead.

### Preview

The central server can see the **planned installations of the next maintenance window** per server, not only status and history.

Implemented (2026-10-20) on the central server, from data it already has: the server detail page shows the next window (start and end in UTC, from the effective windows and the time zone the agent reports; adjoining windows count as one) and the released updates that are due before it closes, including failed ones the agent tries again, with notes on the global pause and on coordinated groups (lease needed). The agent itself still decides; the preview does not include its retry delay or the pull-forward tolerance.

### Detection of external updates

Updates installed outside our own maintenance are detected and reported with their source.

**Implemented (2026-10-20):** the inventory carries the Windows Update history of the last 90 days (at most 500 entries, newest first; read locally with `IUpdateSearcher.QueryHistory`, no network): update ID, revision, title, date, installation or uninstallation, result, error code and the client application ID Windows recorded. The agent's own sessions use `SUSyphos.Agent`, so every other ID (e.g. `AutomaticUpdates`, the update orchestrator behind Settings, Defender, `wusa`) marks an installation outside SUSyphos. The central server shows the history on the server detail page and counts these installations. Typical cause: Windows Automatic Updates still installs on its own (section 3; enable "Manage Automatic Updates" centrally).

**Update types (2026-10-20):** the update search uses `IsInstalled=0 and IsHidden=0` without a `Type` criterion, so drivers are found, reported and can be released like software updates (before, only `Type='Software'` was searched). Updates for other Microsoft products come through the Microsoft Update service: when it is switched on centrally (system default, group override; "Windows Update only" in one group wins), the agent registers it with the WUA service manager (`AddService2`, registered with Automatic Updates, marker `RegisteredMicrosoftUpdate` in the agent key) and searches, inventories and installs with `ServerSelection = ssOthers` and its service ID, also on servers that otherwise use WSUS. Switched off, it removes only a registration it made itself (2026-10-20).

## 6. Remote actions

**Install now (2026-10-21):** the remote actions `InstallNow` and `InstallNowRestartInWindow` store the request (`installations.dat`) and start a search; with the next installation cycle the agent installs every release of its list that the last search found missing, regardless of window, earliest time and retry delay. A global pause keeps the request waiting, the health check and leases apply as usual. Afterwards a pending reboot is done at once with the 5-minute warning (`InstallNow`) or held like `RestartServerInWindow`; the local veto prevents only the restart. The request is cleared after one cycle, also when nothing was to install.

**Restart in the maintenance window (2026-10-20):** the agent stores the request (`installations.dat`), treats it like a pending reboot allowed by policy and restarts with the usual rules (open window for at least 30 more minutes, warning, local veto, global pause, lease in coordinated groups). It reports the planned time (start of the next window long enough) and why it waits; the request ends when the server has started after it, or with `CancelRestart`.

The agent fetches actions from the queue, executes them in the given order and reports status with progress: restart service, immediate update search with report, restart server, check-in with agent update search, full resync, later log retrieval.

## 7. Agent self-update

- The agent asks **only the central server** for updates.
- If it is below the minimum version, it requests and installs an update on its own.
- Repeated failure is made visible in the central server and escalated.

**Implemented (2026-10-20):** the heartbeat announces a released newer version (version, SHA-256, size; see `04-central-server.md`, section 5). The agent downloads it from the central server with the same trust check as every message, streamed into `%ProgramData%\SUSyphos\Agent\Update`, a folder recreated for every update with access for SYSTEM and administrators only, and keeps the file open with read sharing only until msiexec is done, so the verified file cannot be swapped. It checks size and SHA-256 and runs `msiexec /i … /qn /norestart /l*v`. The MSI stops the service, replaces the files and starts the new version; no reboot. The attempt is kept in `agent-update.json`; after the restart the running version decides success. Failures (checksum, msiexec exit code, timeout, old version after the restart) are reported with the heartbeat (`agentUpdateError`) and retried after `AgentUpdate:RetryDelay` (6 h). Below the minimum version of the central server the agent is updated with the shipped package even without a release (`04-central-server.md`, section 5).

## 7a. Central settings and moving

Implemented (2026-10-22): the release list carries central agent settings; the agent applies them through a configuration provider between its own files and the registry, so registry and policy values always win, and keeps them protected for the next start. Changes of logging or priority restart the service; everything else applies with the next cycle. A heartbeat can announce a new central server address; the agent moves only after the server there proves the pinned trust anchor and accepts its credentials (docs/03-communication-protocol.md, 3.4).

## 8. Installation and uninstallation

- The address of the central server comes from the registry (group policy before installer value); the installer writes it from the `SERVERURL` option or from its own file name when downloaded from the central server (see `03-communication-protocol.md` 3.3).
- On uninstallation the agent sends an **unregister** to the central server before the service stops (relevant also for unapproved clients).

## 9. Stability

The operational requirements from `02-guidelines.md` apply: 99.9 % availability, fault tolerance, no memory leaks (in particular clean disposal of the COM objects), retries with backoff, circuit breaker, resource limits, watchdog, no actions endangering operation without configuration.
