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
  Implemented (2026-10-23): a "Fully patched" tile (no update missing at all, released or held back) next to patch compliance, and a "Statistics" box replacing the trend line: a drop-down picks patch compliance and fully patched, servers by status, new updates by classification, installations (by SUSyphos and outside it), open updates (released and held back), time from release to installation per ring, restarts or the most frequent error codes, over 30, 90 or 365 days (weekly beyond 90). The daily snapshot keeps the extra values; charts are SVG drawn on the server with tooltips, legend and a table view.
- Visibility of updates installed outside our own maintenance.
Implemented (2026-10-18): "Needs attention" (alarms, release gates, servers waiting for approval, servers with a failed installation or pre-flight check, servers without heartbeat for over an hour), key figures (agents reporting, pending client updates, warnings for WSUS source, disk space and reboot pending, patch compliance), deployment by ring as a stacked bar, the global pause switch (Operator) and the patch compliance trend from a daily snapshot. AD coverage and stopped coordinated groups were added on 2026-10-19. Not yet: installations outside our own maintenance and agent update failures.

## 3. Ring detail view

- **Per-update view:** several updates (monthly, out-of-band) can be at different stages in the same ring at the same time. For each active update, the planned installation period and its own progress are shown. Since servers in the same ring have different maintenance windows, an update can already be installed on some servers and still pending on others.
- Feedback and success rate, wait and release status per update (the release logic applies per update).
- **Overall ring status as a donut chart** across all updates in the ring: successful (green), pending (blue), queued (yellow), failed (red), and not needed (grey: every server of the ring that needs no installation of a released update, 2026-10-20).
Implemented (2026-10-18): page per ring with the ring sequence and gates, transition rule, donut of all server × update states, active updates with progress, feedback and success rate and wait day, and the server table of the selected update (maintenance windows, earliest installation, status, result with HResult text). States: successful and failed by the last installation result, pending = released without result, queued = reported as missing but not released for the ring.
### Update detail viewImplemented (2026-10-18): one page per update (reached from the update list, ring pages, server details and "Needs attention") with the rollout state, a donut over all servers, progress per ring (state, release time, feedback and success rate, server states), a timeline from the ring states and the audit log, and the servers with ring, status, earliest installation and result, filterable by status. Operators start, stop, resume, override and approve gates there.

## 4. Server detail view

- Health status, agent version, build number/OS level
- Ring and group membership (including whether rule-based or manual)
- **Update history** with status and **source** (our solution or external, e.g. installed manually)
- Next maintenance window and the installations planned in it
  (implemented 2026-10-20: box "Planned for the next maintenance window")
- Live status with progress
- **Agent logs for troubleshooting** (rarely needed). Mechanism: request via the action queue, the agent delivers the data. A live retrieval with a direct request to the agent is not planned because of the communication model (the agent always initiates).
- **Remote actions:** restart client service, immediate update search with report, restart server, ask the agent to check in/look for an agent update. Shows queue state and execution progress per action.
- **Troubleshooting information** on failed updates (event log excerpt, optionally CBS.log), HResult in plain text.
Implemented (2026-10-18): health (offline, failed, reboot pending, warning, OK), pre-flight checks, last search error, missing updates with release state, assignment and effective windows, and the update history with plain-text results. The server list filters by name or domain, ring and health. Remote actions, agent logs, time zone and the next window as a date were added on 2026-10-19. Live status (2026-10-20): a box with step, progress, updates and start time while the agent works (page reloads every 10 seconds), status "Updating" during download and installation, and why a pending reboot waits. Server facts show the uptime and the last update search with its result; a "Recent activity" list shows registration, approval, inventory, update searches, installations, remote action results, restarts and agent updates (no heartbeats, kept 90 days; 2026-10-20). Not yet: troubleshooting excerpts. Agent versions appear in short form (major.minor.patch) and link to the server list filtered by agent version (2026-10-20).

## 5. Administration

### Servers and assignment

- **Ring assignment** in three levels: final ring as fallback → rule-based → manual override.
- **Grouping** with the same logic, with shipped standard rules for role detection (domain controller, Exchange, SQL, cluster, Hyper-V).
- **Remove server:** soft delete (history is kept) or hard delete (all data is deleted).
- **Exclusion** of individual servers or groups from the update process, **global pause**.
- **Approval of new clients** (manual with IP, name and basic information, or rule-based, see `03-communication-protocol.md`).

### Maintenance windows

Three levels: system default (Mon–Fri 19:00–05:00, Sat/Sun all day) → group → individual server. In addition, blackout times as a time filter on top of the ring release.

Implemented pages (Admin role): Administration → Maintenance windows (system default), Server groups (create, delete, group windows), Assignment rules (create, delete). Servers and server detail (Reader role) show ring, groups and effective windows; administrators change the manual ring, manual groups and server windows on the server detail page.

### Release rules

Ring transitions (time-/success-based), rates, minimum/maximum wait times, manual gates, each also per update type/category.

Implemented: Updates page (rollout state per ring, queued updates, start/stop/override for Operators), ring transitions and gates under Pending approvals, Administration → Release rules (Admin) for transition rules and start rules. Rules per update category follow.

### Registration

Management of the approval rules for new agents (rule-based approval, e.g. domain member, verified by the central server against AD and DNS). Manual approvals run through the "Pending approvals / overrides" item. In addition: display of clone suspicions (same GUID from two sources) and of the central server certificate: uploading and approving a new TLS certificate (signed with the trust anchor, see `03-communication-protocol.md` 3.2), showing which agents already use the current binding, and showing local trust resets via the registry override.

### Users and roles

Local user accounts with Reader/Operator/Admin, and sign-in with Windows (Kerberos/NTLM, decided by Marc on 2026-10-21): AD groups are mapped to roles in the dashboard (stored by SID, the highest role wins), the account is linked to the user's SID and gets its role at every sign-in; the last local administrator always remains. Details: `docs/admin/README.md`, section 18.

### Audit and SIEM

View of the immutable audit log, configuration of the SIEM export (target endpoint and format, initially without filters).

### Backup

Configuration and triggering of the central server's backup function.

Implemented (2026-10-22): box "Move to a new server" announces the address of a central server restored from the backup; agents switch after checking the trust anchor (docs/03-communication-protocol.md, 3.4); the box lists agents that could not move.

### Agent settings

Implemented (2026-10-22): page Administration → Agent settings with the system defaults of the central agent settings (intervals, logging, resources, pre-flight, search, restart notice), grouped in boxes. Server groups and single servers override them in a collapsed "Agent settings" section of their forms that shows the applied value and its source; nothing is overridden by default.

### Agent rollout

Visibility of agent versions, state of the internal agent rollout, minimum version.

Implemented (2026-10-20): page Agents (role Operator; Admin for checking and releasing) with the agent versions the central server holds (source, release state per ring as tags, servers per version), release actions, "Check for new versions now", the installer download with address and anchor fingerprint in the file name, the agent versions reported by the servers and failed self-updates. The server detail shows the agent update state.

### Integrated help

Requested by Marc (2026-10-21): every switch, input and relevant display is explained in a help built into the dashboard, reachable with one click from where it is used; dialogs and controls not visible on the page (modal dialogs, filter drop-downs) are shown with screenshots of sample data.

Implemented (2026-10-21): help pages under `/help` (index) and `/help/<page>`, written in Markdown (`src/SUSyphos.Server/Help/Topics`, compiled into the assembly, raw HTML disabled, file name prefix sets the order) and rendered with Markdig. Headings carry explicit ids (`## Column filter {#filter}`). A small "?" (`HelpLink`) after the page title and each box and dialog title (one per page, section and dialog, not per control; Marc 2026-10-21) opens the matching section in a new tab, so a half-filled form stays. Shared components point to their own sections without a visible link (`data-help` on table tools, column filters and the maintenance window editor). The help is open without sign-in (it contains no data of the installation). Screenshots under `wwwroot/help-images` are taken by the browser tests from the demo lab (`build/Update-HelpScreenshots.ps1`). The browser test `HelpCoverageTests` fails the build when a page or one of its dialogs has a visible field, button or switch whose help (nearest box, dialog or page link, or `data-help`) is missing, points to a missing section, or whose section does not mention the control by its label.

## 6. Permissions (sketch)

| Area | Reader | Operator | Admin |
|---|---|---|---|
| Read dashboards, histories, logs | yes | yes | yes |
| Remote actions, approvals, overrides, pause | no | yes | yes |
| Administration, users, registration, SIEM, backup | no | no | yes |

> The assignment of individual functions to Operator/Admin is a sketch and not yet agreed.

## 7. Open points

- Exact permission assignment per function
- Escalation of notifications (reminders, further recipients) when nobody reacts; the channels themselves (e-mail, webhook) are implemented (decided by Marc on 2026-10-19, `docs/admin/README.md` section 20)
- Visualization details beyond the ideas listed

## 8. Design patterns (binding for every page)

Decided by Marc on 2026-10-25. New and changed pages use these patterns; existing pages are brought in line. Each pattern is one shared component, not markup repeated per page.

### Tiles

Key figures at the top of a page (overview, server details, AD coverage, agents, and wherever a page leads with numbers).

- **Header:** a short title in small type; optionally a link on the right, always named "Show ›".
- **Main part:** exactly one or two values, large and bold, centred horizontally and vertically. Two values sit side by side, split by a thin vertical line, each with a small caption below. A state is a pill (e.g. "Failed", "up to date"), not plain text. A distribution of states is a small donut with a legend (e.g. servers by status, AD coverage). Never more than two values; anything else goes to the footer.
- **Footer:** one line of small text below a thin separator, always at the bottom (unit, context, time reference); longer text is cut with "…" and shown in full as tooltip.
- **Edge:** a tile whose state needs attention gets a coloured left edge (red failure, amber warning); the edge never carries information the text does not say.
- All tiles of a row have the same height. Version numbers carry a "v" prefix (v0.7.7).

### Action bands

The band of actions below the page title.

- One row of the regular dashboard buttons (bordered, icon from the Lucide set plus text).
- Actions of one topic with more than one entry form a drop-down button with ▼ (e.g. "Updates ▼", "Server ▼", "Rollout ▼", "Assign ▼", "New ▼"). A topic with a single action stays a plain button. Inside a drop-down, related actions are separated by a thin line; there are no sub-headings.
- The main group or action of the page is blue and first. Plain page actions (Refresh) are single buttons.
- Dangerous actions are red and stand on the right, grouped the same way (e.g. "Remove ▼" with Remove and Delete, "Block ▼"). Opposite actions (approve / reject) stay separate buttons.
- An action that does not fit the current state is greyed out with the reason as tooltip, not hidden.
- Views are not actions: switches that show or hide list entries belong to the list's "Show:" switches, not to the band.
- Menus open without script (like the column filters); with script they close on a click elsewhere or Esc.

### Tabs

Pages with much content are split into tabs (Marc, 2026-10-25): server details (Overview, Updates, History, Configuration, System, Activity), update details (Overview, Servers, Timeline), Notifications (Channels, Events) and Release rules (Start rules, Rings).

- Title, action band and tiles stay above the tabs on every tab; the tabs hold the boxes below.
- The first tab is an overview of what matters now and links to the details in the other tabs.
- Tabs are plain links with the tab in the address (`?tab=…`): they work without script, survive a reload and can be linked to; "Show ›" on a tile opens the matching tab.
- A tab shows a count or a coloured badge when something waits there (e.g. missing updates, a failed check), so nobody has to open every tab.
- One shared tab bar component (the one of the former ring page) for every page.
