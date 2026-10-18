# Central Server: Functions, Data Storage, Security, Operation

> See `01-architecture.md` for the overall context and `03-communication-protocol.md` for the interface.
> A worked-out table schema is still pending. This document describes the domain entities and rules it will be derived from.

## 1. Data storage

- **SQLite**, no separate SQL Server installation. Portable and usable at customers without existing database infrastructure.
- Deliberately relational (rather than LiteDB), because the dashboard needs statistics via SQL aggregation (failure rates, timelines, compliance trend).
- No real high availability (multi-master). Protection through the backup function.

### Backup

- Controlled by the central server service itself: the service detects a moment without database access and creates a consistent file-based image (e.g. ZIP) of the database, configuration/settings and other relevant data.
- The backup contains the private **trust anchor** of the central server (see `03-communication-protocol.md` 3.2). Without it, all agents lose trust after a restore. It must therefore not be stored in plain text in the backup (encryption method still to be defined).
- No automatic external transfer needed. The servers are backed up externally anyway and include the local image.
- Litestream was deliberately rejected (disaster recovery only with manual failover, no real HA).

### Deployment

- Classic Windows installer that installs and starts the service with a default configuration.
- On updates the existing configuration is preserved. Changes to configuration or database schema (migration) are detected and applied automatically by the installer or the next service start.
- On breaking changes, a central server version always ships with the matching agent version.

## 2. Domain entities (draft)

| Entity | Main contents |
|---|---|
| **Server/client** | **Client GUID** (issued centrally, unchanged across renames and domain rejoins), registration state (pending/approved/deleted/blocked on clone suspicion), name, IP, domain membership, SID, OS version, build/UBR (updated on every sync), agent version, detected roles, ring, groups, update source (WU/WSUS), heartbeat time |
| **Update (catalog)** | Unique update ID with revision, KB, title, category/type (e.g. security, feature), aggregated from agent reports |
| **Ring** | Name, order, transition rules, default ring (deployed last) |
| **Ring update state** | Per update **and** ring: feedback rate, success rate, minimum/maximum wait time, transition state, release gate |
| **Group** | Members (rule-based/manual), coordinated group yes/no (leases), maintenance window |
| **Assignment rule** | Attribute/AD conditions for ring and group assignment, priority, manual overrides |
| **Maintenance window** | System default, group override, server override |
| **Installation/history** | Per server and update: update ID + revision, ResultCode, HResult, reboot required/performed, start/end, **source** (our solution or external/manual) |
| **Remote action (queue)** | Per client: action, order, status, progress in % |
| **Lease** | Per coordinated group: holder, expiry, result |
| **User** | Local accounts, role |
| **Audit log** | Immutable, separate from regular reporting |
| **Approval rule (registration)** | Rules for rule-based approval of new agents (e.g. domain member + SID match with AD), state of pending manual approvals |
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
- **Global pause:** while active, agents start no new installations. Health check, search and reporting continue normally.
- **Manual override per update:** bypasses ring logic, rates and wait times and puts the update into the next possible installation window (e.g. critical security updates).
- **Exclusion** of individual servers or entire groups from the update process, in addition to the global pause.
- **Blackout/installation time windows** (black/white list) act as a time filter on top of the ring release.
- **Staggering/offset** of installation times within a ring (e.g. so not all domain controllers install at the same time).
- Optional **random delay** for ungrouped servers.

### Ring and group assignment (three levels)

1. **Default ring** as fallback (deployed last).
2. **Rule-based** by attributes (values reported by the client, AD attributes, AD group memberships).
3. **Manual assignment** of a server or group, overriding the rules.

Grouping uses the same logic. In addition, predefined standard rules for automatic **role detection** are shipped (see `05-agent-design.md`).

**Derived flow on first registration** (from the registration flow in `03-communication-protocol.md`, not yet explicitly confirmed): since the inventory (including detected roles) is available before approval, group and ring rules can apply immediately on approval without first landing in the default ring.

### Group coordination (leases)

- For coordinated groups (e.g. domain controllers) where not all members may install/reboot at the same time, coordination runs **centrally in the central server** (not server to server). No single hanging server may block the group (lease with expiry).
- **Quality gate:** if several leases in a row fail or expire without feedback, the central server stops issuing leases for this group.
- Leases apply only to explicitly coordinated groups. All other servers install on their own.

## 4. Maintenance windows

Three levels following the same pattern: **system default** (proposal: Mon–Fri 19:00–05:00, Sat/Sun all day) → **group level** → **individual server**.

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
