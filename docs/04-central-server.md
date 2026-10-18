# Central Server: Functions, Data Storage, Security, Operation

> See `01-architecture.md` for the overall context and `03-communication-protocol.md` for the interface.
> A worked-out table schema is still pending. This document describes the domain entities and rules it will be derived from.

## 1. Data storage

- **SQLite**, no separate SQL Server installation. Portable and usable at customers without existing database infrastructure.
- Deliberately relational (rather than LiteDB), because the dashboard needs statistics via SQL aggregation (failure rates, timelines, compliance trend).
- No real high availability (multi-master). Protection through the backup function.

### Backup

- Controlled by the central server service itself: the service detects a moment without database access and creates a consistent file-based image (e.g. ZIP) of the database, configuration/settings and other relevant data.
- The backup contains the private **trust anchor** of the central server (file `Trust:AnchorPath`, default `%ProgramData%\UpdateCoordinator\Server\trust-anchor.key`, see `03-communication-protocol.md` 3.2). It is DPAPI-protected for the service account, so a restore must happen under the same account or the key must be re-encrypted. Without it, all agents lose trust after a restore. It must therefore not be stored in plain text in the backup (encryption method still to be defined).
- No automatic external transfer needed. The servers are backed up externally anyway and include the local image.
- Litestream was deliberately rejected (disaster recovery only with manual failover, no real HA).

### Deployment

- Classic Windows installer that installs and starts the service with a default configuration.
- On updates the existing configuration is preserved. Changes to configuration or database schema (migration) are detected and applied automatically by the installer or the next service start.
- On breaking changes, a central server version always ships with the matching agent version.

## 2. Domain entities (draft)

| Entity | Main contents |
|---|---|
| **Server/client** | **Client GUID** (issued centrally, unchanged across renames and domain rejoins), registration state (pending/approved/deleted/blocked on clone suspicion), name, IP, domain membership, OS version, build/UBR (updated on every sync), agent version, detected roles, ring, groups, update source (WU/WSUS), heartbeat time |
| **Update (catalog)** | Unique update ID with revision, KB, title, category/type (e.g. security, feature), aggregated from agent reports |
| **Ring** | Name, order, transition rules; the final ring (Production) is also the fallback ring |
| **Ring update state** | Per update **and** ring: feedback rate, success rate, minimum/maximum wait time, transition state, release gate |
| **Group** | Members (rule-based/manual), coordinated group yes/no (leases), maintenance window |
| **Assignment rule** | Attribute/AD conditions for ring and group assignment, priority, manual overrides |
| **Maintenance window** | System default, group override, server override |
| **Installation/history** | Per server and update: update ID + revision, ResultCode, HResult, reboot required/performed, start/end, **source** (our solution or external/manual) |
| **Remote action (queue)** | Per client: action, order, status, progress in % |
| **Lease** | Per coordinated group: holder, expiry, result |
| **User** | Local accounts, role |
| **Audit log** | Immutable, separate from regular reporting |
| **Approval rule (registration)** | Rules for rule-based approval of new agents (e.g. domain member, verified by the central server against AD and DNS), state of pending manual approvals |
| **Agent release** | Version, rollout ring membership, minimum version |

## 3. Ring logic

### Transition types

- **Time-based:** the ring starts at a fixed time.
- **Success-based:** the next ring starts only when the previous one has a sufficient success rate.

### Rates

- **Feedback rate** (how many servers reported at all) and **success rate** (successes among the received reports) are evaluated separately, so offline servers do not distort the success rate.
- **Minimum wait time** before the transition. **Maximum wait time:** when it is reached and the success rate of the reports so far is above the threshold (even if the feedback rate was missed), the rollout proceeds. If the success rate is poor, it does **not** roll on automatically; instead an **alarm** is raised for a human.

### Reference: per update

The entire release logic (rates, wait times, transition) applies **per concrete update within a ring**, not to the ring as a whole. Several updates pass through the same ring independently and in parallel.

### Additional rules

- Optional **manual release gate** per ring transition (e.g. mandatory for pilot → production). Notification channel and escalation for an outstanding release are still to be worked out.
- Release rules (wait times, rates) can additionally be defined **per update type/category** (e.g. security vs. feature).
- **Global pause:** while active, agents start no new installations. Health check, search and reporting continue normally. Implemented: switched by an Operator on the overview page; `Installation:GlobalPause` in the configuration forces it on.
- **Manual override per update:** bypasses ring logic, rates and wait times and puts the update into the next possible installation window (e.g. critical security updates).
- **Exclusion** of individual servers or entire groups from the update process, in addition to the global pause.
- **Blackout/installation time windows** (black/white list) act as a time filter on top of the ring release.
- **Staggering/offset** of installation times within a ring (e.g. so not all domain controllers install at the same time).
- Optional **random delay** for ungrouped servers.

### Implemented (2026-10-18)

- **Start rules** decide how an update enters the first ring (Pilot) when an approved server first reports it as missing: the matching rule with the lowest priority number applies; conditions are classification, MSRC severity and title pattern (all set conditions must match). "Automatic" starts the rollout with an optional delay (0–720 hours) before servers of the first ring may install it; the transition wait time starts after the delay. "Manual" keeps the update **queued** until an Operator starts it; so does the absence of a matching rule. Default (decided 2026-10-18): one rule without conditions, automatic, no delay, so every update starts automatically.
- **Transitions:** Pilot → Test and Test → Production each have a minimum and maximum wait time, a minimum feedback rate and a minimum success rate, and a manual gate. Defaults: Pilot → Test 2–7 days, Test → Production 3–10 days, each 80 % feedback and 95 % success; the manual gate is on for Test → Production only. Wait times run from the release in the source ring. Only success-based transitions are implemented; fixed start times are not.
- **Rates** are computed from the servers currently in the source ring: targeted = servers the update was released to or that reported a result; the last result per server counts, so a successful retry replaces a failure. If no server of the ring needs the update, the transition passes after the minimum wait time.
- **Decision:** rule met → next ring released, or "awaiting approval" with a manual gate; feedback met but success poor, or nothing reported at the maximum wait time, or poor success at the maximum wait time → **alarm**. Gates and alarms are decided by an Operator (approve, release anyway, or stop the rollout); an alarm never clears by itself.
- **Override** releases the update to every ring at once; **stop** withdraws all releases of the update that are not installed yet; a stopped rollout can be resumed.
- **Releases:** an approved server gets an update when it is released for its ring and the server reported it as missing; the release stays until the inventory reports the update as installed. A ring change moves the releases. The earliest installation time is the release time of the ring.
- The central server evaluates rollouts every `Rollout:EvaluationInterval` (default 1 minute).
- Security note: only approved agents report updates, so only they can start a rollout by a start rule. A rollout of an update ID that a single agent invented reaches only servers that report the same update as missing themselves, and the agent installs only updates its own Windows Update search returns.
- Not yet implemented: rules per update category, exclusions, blackout windows, staggering, random delay, leases.

### Ring and group assignment (three levels)

1. **Fallback:** servers without an assignment are in the final ring Production (deployed last; decided 2026-10-18, no separate Default ring).
2. **Rule-based** by attributes (values reported by the client, AD attributes, AD group memberships).
3. **Manual assignment** of a server or group, overriding the rules.

Grouping uses the same logic. In addition, predefined standard rules for automatic **role detection** are shipped (see `05-agent-design.md`).

Implemented (2026-10-18): the rings `Pilot`, `Test` and `Production` are fixed. An assignment rule has a priority, a target (ring or group) and up to three conditions that must all match: detected role, DNS domain and host name pattern (`*` and `?`). Ring: manual assignment, else the matching ring rule with the lowest priority number, else `Production`. Groups: every matching group rule adds the group; manual memberships are kept independently. Assignment is recalculated on registration, after every inventory, after every administrative change and on start. Conditions on AD attributes and AD group memberships are not implemented yet.

**Derived flow on first registration** (from the registration flow in `03-communication-protocol.md`, not yet explicitly confirmed): since the inventory (including detected roles) is available before approval, group and ring rules can apply immediately on approval without first landing in the fallback ring.

### Group coordination (leases)

- For coordinated groups (e.g. domain controllers) where not all members may install/reboot at the same time, coordination runs **centrally in the central server** (not server to server). No single hanging server may block the group (lease with expiry).
- **Quality gate:** if several leases in a row fail or expire without feedback, the central server stops issuing leases for this group.
- Leases apply only to explicitly coordinated groups. All other servers install on their own.

## 4. Maintenance windows

Three levels following the same pattern: **system default** (Mon–Fri 19:00–05:00, Sat/Sun all day, changeable in the dashboard) → **group level** → **individual server**.

Implemented (2026-10-18): the most specific level with windows wins. Server windows replace everything else; otherwise the windows of all groups of the server apply together (union); otherwise the system default. The central server stores the resulting effective windows per client and delivers them with the release list; a change increases the release version so the agent fetches it. Windows are entered as text, one per line, e.g. `Mon-Fri 19:00-05:00` or `Sat,Sun 00:00-24:00`, at most 14 per level.
Reboot policy (implemented 2026-10-18) follows the same three levels: server setting, otherwise the groups of the server (if any group says "Never", it wins), otherwise the system default ("Never" unless changed). The effective policy is delivered with the release list; a change increases the release version.

## 5. Agent updates (agent rollout)

- Its own **fixed, predefined** ring structure, not the admin-configurable Windows update logic (agent risk is lower, admins usually patch servers only monthly). Example idea: random 10 % / 30 % / 60 %. Same quality standards, but independent and on a faster cadence.
- Two sources: breaking-change coupling to the central server installation **and** a regular search by the central server for separately published agent patches (e.g. GitHub Releases).
- The central server has visibility of the installed agent version per server.

## 6. Security of administration

### Access to the web interface

- Initially simple user name/password login with local accounts.
- Later extensible with Windows authentication, LDAP/AD or other mechanisms.

### Role model (hierarchical, independent of the login method)

| Role | Permissions |
|---|---|
| Reader | Read |
| Operator | Reader + operational actions |
| Admin | Operator + Reader + administration |

### Audit log

- All security-relevant admin actions are stored **immutably** and **separately from regular reporting**.
- Optional export to an external SIEM in a common, compatible format. Initially only **target endpoint and format** are configurable, no filtering by action type (can be added later).

### Removing servers

Two options: **soft delete** (marked as retired/deactivated, history is kept) or **hard delete** (all data is removed from the database).

## 7. AD comparison

If the central server is AD-joined, it reads the computer objects (filtered to servers) via LDAP as the **target** and compares them with the registered/reporting agents as the **actual** state. This yields a coverage rate with delta.

## 8. Open points

- Concrete table schema (to be derived from section 2)
- Notification/escalation for outstanding manual releases
