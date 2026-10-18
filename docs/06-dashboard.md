# Dashboard and Administration Concept

> See `04-central-server.md` for data and rules, `03-communication-protocol.md` for the action queue.
> Access: web interface of the central server with the roles Reader / Operator / Admin.

## 1. Navigation (overview)

1. **Overview** (main dashboard)
2. **Rings** (ring detail)
3. **Servers** (server detail)
4. **Pending approvals / overrides** (separate, prominent item)
5. **Administration**

The overview of pending manual approvals and overrides is deliberately a separate navigation item and not hidden under administration, because these are cases actively waiting for a human decision (e.g. ring transitions with a mandatory release such as pilot → production).

## 2. Main overview

- **Urgent items at the top** (prioritized): states that require manual intervention and do not resolve themselves, e.g. stuck ring transitions with a poor success rate, expired group leases, repeatedly failing agent self-updates.
- **Deployment status and overall health per ring.**
- Number of **pending client updates** and **active warnings**, further metrics on distribution status and environment health.
- **Coverage rate** from the AD comparison (target: computer objects via LDAP, filtered to servers; actual: registered/reporting agents) with delta. Requires an AD-joined central server.
- **Patch compliance trend line** over time (similar to security dashboards).
- Visibility of updates installed outside our own maintenance.

## 3. Ring detail view

- **Per-update view:** several updates (monthly, out-of-band) can be at different stages in the same ring at the same time. For each active update, the planned installation period and its own progress are shown. Since servers in the same ring have different maintenance windows, an update can already be installed on some servers and still pending on others.
- Feedback and success rate, wait and release status per update (the release logic applies per update).
- **Overall ring status as a donut chart** across all updates in the ring: successful (green), pending (blue), queued (yellow), failed (red).

## 4. Server detail view

- Health status, agent version, build number/OS level
- Ring and group membership (including whether rule-based or manual)
- **Update history** with status and **source** (our solution or external, e.g. installed manually)
- Next maintenance window and the installations planned in it
- Live status with progress
- **Agent logs for troubleshooting** (rarely needed). Mechanism: request via the action queue, the agent delivers the data. A live retrieval with a direct request to the agent is not planned because of the communication model (the agent always initiates).
- **Remote actions:** restart client service, immediate update search with report, restart server, ask the agent to check in/look for an agent update. Shows queue state and execution progress per action.
- **Troubleshooting information** on failed updates (event log excerpt, optionally CBS.log), HResult in plain text.

## 5. Administration

### Servers and assignment

- **Ring assignment** in three levels: fallback ring Production → rule-based → manual override.
- **Grouping** with the same logic, with shipped standard rules for role detection (domain controller, Exchange, SQL, cluster, Hyper-V).
- **Remove server:** soft delete (history is kept) or hard delete (all data is deleted).
- **Exclusion** of individual servers or groups from the update process, **global pause**.
- **Approval of new clients** (manual with IP, name and basic information, or rule-based, see `03-communication-protocol.md`).

### Maintenance windows

Three levels: system default (Mon–Fri 19:00–05:00, Sat/Sun all day) → group → individual server. In addition, blackout times as a time filter on top of the ring release.

Implemented pages (Admin role): Administration → Maintenance windows (system default), Groups (create, delete, group windows), Assignment rules (create, delete). Servers and server detail (Reader role) show ring, groups and effective windows; administrators change the manual ring, manual groups and server windows on the server detail page.

### Release rules

Ring transitions (time-/success-based), rates, minimum/maximum wait times, manual gates, each also per update type/category.

Implemented: Updates page (rollout state per ring, queued updates, start/stop/override for Operators), ring transitions and gates under Pending approvals, Administration → Release rules (Admin) for transition rules and start rules. Rules per update category follow.

### Registration

Management of the approval rules for new agents (rule-based approval, e.g. domain member, verified by the central server against AD and DNS). Manual approvals run through the "Pending approvals / overrides" item. In addition: display of clone suspicions (same GUID from two sources) and of the central server certificate: uploading and approving a new TLS certificate (signed with the trust anchor, see `03-communication-protocol.md` 3.2), showing which agents already use the current binding, and showing local trust resets via the registry override.

### Users and roles

Local user accounts with Reader/Operator/Admin. AD/LDAP integration is a later extension.

### Audit and SIEM

View of the immutable audit log, configuration of the SIEM export (target endpoint and format, initially without filters).

### Backup

Configuration and triggering of the central server's backup function.

### Agent rollout

Visibility of agent versions, state of the internal agent rollout, minimum version.

## 6. Permissions (sketch)

| Area | Reader | Operator | Admin |
|---|---|---|---|
| Read dashboards, histories, logs | yes | yes | yes |
| Remote actions, approvals, overrides, pause | no | yes | yes |
| Administration, users, registration, SIEM, backup | no | no | yes |

> The assignment of individual functions to Operator/Admin is a sketch and not yet agreed.

## 7. Open points

- Exact permission assignment per function
- Notification channels (e.g. for pending approvals, alarms) and escalation
- Visualization details beyond the ideas listed
