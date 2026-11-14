# Plan for 0.9.0-rc.5: WinGet uninstall and signed scripts

Status: proposal for Marc (2026-11-14). The database schema is an open decision; nothing is built until it is approved.

## Part 1: WinGet uninstall (stage 1 of the concept, WinGet only)

Already there: the agent uninstalls a WinGet package through the helper (`AgentAction.PackageId` with `Uninstall`,
`UninstallRestartInWindow`, `UninstallInWindow`), reports the result, resets the inventory and handles 3010 like
updates. Used today only by "uninstall when removed" of an assignment.

To build:

1. **Server › Software tab**: ⋯ on a row → **Uninstall…** (Admin) for programs matched to a WinGet package on a server
   with WinGet; otherwise greyed with "Not known to WinGet. Remove it on the server." or "Not supported on this server".
   SUSyphos itself is never offered. Dialog like "Uninstall update…": the three When choices, a note for runtimes
   (VC++, .NET, Java: other programs may need it), the package ID and the installed version.
2. **Package page**: **Uninstall from servers…** red on the right of the action band. Scope one server, a group, a
   ring or all servers with the package; When; **End the N assignments of this package** (on); with more than one
   server the confirmation box "I understand that N servers will lose this program". All targets at once (no ring by
   ring in this stage). Status row **Uninstalling** "7 of 12 done, 5 waiting for their window", link to the servers.
3. **Remove assignment**: the dialog shows the saved "On removal" choice preselected and changeable, with counts
   ("Uninstall from 4 servers; 1 more keeps it through another assignment").
4. **Remove from catalog**: allowed without assignments and without a running rollout, also when installed (changes the
   rc.4 rule); checkbox **Also uninstall it from N servers** (off).
5. **Success**: the action succeeded and the next inventory no longer lists the package (the agent sends one right
   after). Still listed: "Uninstalled, but still in the inventory" (amber). History **Installed by SUSyphos** gets
   rows with operation **Uninstallation**, category Software. Audit for every queueing.

Schema (proposal):

| Table | Change | Why |
|---|---|---|
| `AgentActions` | new column `BatchId` (Guid, nullable, indexed) | groups the actions of one "Uninstall from servers…" for the status row |

No new tables, no protocol change. Built 2026-11-14 without `Installations.Operation`: the history rows of uninstallations come from their remote actions, because `Installations` refers to catalog entries that an uninstalled version may not have.

Decisions for Marc:

- **Lease**: software installs without a lease since 13.11. Proposal: software uninstalls also without a lease (today
  the agent takes one for every uninstall).
- **Window**: proposal: "in the next window" means the software window (which falls back to the maintenance
  windows); a restart after it waits for a maintenance window, as for installations.

## Part 2: Signed scripts for WinGet packages

As in the approved concept (https://claude.ai/artifact/D4zqEgR9uWNiUsG4D8TynE) with Marc's decisions of 13.11.: own
ECDSA P-256 script key certified by the trust anchor, one Admin approves (no four-eyes, no signing password), full
output log of every run, PowerShell 5.1, four phases, local veto `AllowScripts = 0`, switch `Scripts.Enabled` off by
default.

Order of work: (a) key, signature formats in the contracts, agent verification with tests; (b) script page, approve
dialog, AMSI scan, parameters and values on assignments; (c) agent runner (job object, timeout, wrapper, parameters on
stdin, ExecutionPolicy and language mode detection, rollback protection); (d) output log, Script output page, result
columns; (e) help, docs, browser tests.

Schema (proposal):

| Table | Columns | Notes |
|---|---|---|
| `ScriptKeys` | `KeyId` (text, key), `PublicKey` (blob, SPKI), `Certificate` (blob, anchor signature over `SUSyphos.ScriptKey.v1\|…`), `ProtectedPrivateKey` (blob, DPAPI), `CreatedAtUtc`, `RetiredAtUtc` (null) | one active key; the format carries the key ID, so rotation can come later; in the encrypted backup like the anchor |
| `PackageScripts` | `Id` (int), `PackageId` (FK), `Phase` (int: BeforeInstall, AfterInstall, BeforeUninstall, AfterUninstall), `DraftContent` (text, null), `DraftParametersJson` (text, null), `DraftRunOn`, `DraftOnFailure`, `DraftTimeoutSeconds`, `DraftChangedAtUtc`, `DraftChangedBy`, `ApprovedRevision` (int, null) | unique (`PackageId`, `Phase`); drafts never reach an agent |
| `PackageScriptRevisions` | `ScriptId` + `Revision` (key), `Content` (blob, UTF-8 with BOM and CRLF, the signed bytes), `ContentSha256`, `ParametersJson`, `ParametersSha256`, `RunOn` (int: Install, Upgrade, Both), `OnFailure` (int: Stop, Continue), `TimeoutSeconds` (60 to 1800, default 300), `KeyId`, `Signature` (blob), `SignedAtUtc`, `ApprovedBy`, `ScanState`, `ScanDetail` | immutable once signed |
| `PackageAssignmentScriptValues` | `AssignmentId` + `ScriptId` + `Name` (key), `Value` (text, null for secrets), `ProtectedValue` (blob, DPAPI, secrets only), `ChangedAtUtc`, `ChangedBy` | values per assignment; without an assignment the defaults apply |
| `ScriptRuns` | `Id` (Guid), `ClientId`, `PackageId`, `ScriptId`, `Revision`, `Phase`, `Action` (Install, Upgrade, Uninstall), `StartedAtUtc`, `FinishedAtUtc`, `ExitCode`, `Result` (int: Succeeded, SucceededRestart, Failed, TimedOut, Blocked, NotVerified), `Output` (text, at most 1 MB, secrets masked), `Truncated`, `Lines`, `Errors`, `Warnings` | retention like the installation history |
| `Settings` | key `Scripts.Enabled` | off by default |

Protocol (new, `/api/v1`): release entries for software carry `scripts` (phase, script ID, revision, SHA-256 of
content and parameters, parameter values; secret values only in memory on the agent); `GET /api/v1/scripts/{id}/{rev}`
returns content, signature and key certificate; `POST /api/v1/agents/script-runs` takes the run with its output (at
most 1 MB, `413` above); the heartbeat reports the local veto and the detected ExecutionPolicy and language mode.
