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

### Tables

Every list (Marc, 2026-10-25), built on the shared list components.

- **Box:** the table sits in a box with title, "?" and a short hint on the right.
- **Toolbar** above the header: search field, optional "Group by", active filters as chips with × and "Clear all"; on the right the "Show:" switches for hidden entries (an active switch is outlined in blue). The toolbar stays visible when everything is hidden, so the switches can be turned off again.
- **Header:** sortable columns show ▲/▼ on the sorted column; filterable columns have a filter symbol, blue while the filter is active. A column filter opens below its header with a search in the values, the count per value, Clear and Apply.
- **Columns:** fixed widths per list (no jumping while paging or filtering); numbers right-aligned; long text cut with "…" and shown in full as tooltip; tags (ring) and pills (state) follow the status pattern.
- **Selection:** a checkbox per row and "Select all" in the header (all, none or some). While rows are selected a bar appears above the header with the count, the actions for the selection (action band pattern) and "Clear selection"; selected rows are tinted.
- **Row actions:** last column, fixed width: at most one icon for the most common action plus "⋯" for the rest; the menu shows an icon per entry, dangerous entries red at the bottom behind a separator. A row that expands shows ▾ there instead.
- **Expanded row:** the details open right below the row, indented and lightly shaded, the row stays tinted. A table inside works like every table, compact: sorting, filters (as chips), and above 10 rows a search and pages of 10, with a note how many the filter hides. Outer and inner state both live in the address.
- **Grouping:** a group row with the value and the count above each group; columns stay aligned.
- **Footer:** count ("1–10 of 48 servers"), rows per page (10, 25, 50, 100) and pages.
- **Empty states:** "nothing there yet" explains what belongs here and offers the action to add it; "nothing matches" says how many entries search and filters hide and offers to clear them.

### Dialogs

Every modal dialog (Marc, 2026-10-25), built on the shared dialog component.

- **Two widths only:** narrow for short forms and confirmations, wide for long forms and dialogs with a preview.
- **Header:** title, "?" (help section of the dialog) and × to close. **Footer:** right-aligned, Cancel then the main action, named after what it does ("Create user", "Restart", never "OK"); an optional note on the left of the footer (e.g. when a change takes effect). The body scrolls, header and footer stay.
- **Fields:** stacked, label above, help below; two short fields may share a row. Long forms are split into sections with a small heading and a thin line.
- **Errors:** a refused save keeps the dialog open with everything typed; a short summary at the top ("2 fields need a change"), the specific message right at each field, the field outlined in red.
- **Dangerous actions:** the title is a question, the body says in plain words what happens and lists what is affected, the main button is red with the exact verb ("Delete with history"). An irreversible action needs a ticked "I understand …" first.
- **Choosing between options:** selectable options, each with a one-line explanation, and a single main button; the usual option is preselected. No dialog shows one button per option.
- **Preview:** where the setting affects servers (assignment rules, groups, agent settings), the wide dialog shows on the right what would change; it updates while typing with script and on "Preview" without.
- Dialogs open from the address (`?dialog=…`), so they work without script and survive a reload; they close with ×, Cancel or Esc, and focus starts in the first field.

### Forms and fields

Every form, in dialogs and on pages (Marc, 2026-10-25).

- **Field anatomy:** label above (small, bold), "optional" next to the label of a field that may stay empty, help text below. Units sit inside the field on the right ("30 seconds"); several values are chips in one field.
- **Selection fields** look like text fields (same height, full width of their column) with a chevron icon at the right edge, whatever the text length. Drop-down *buttons* (action bands, row menus) are buttons: as wide as their text, the caret beside the label.
- **States:** focus with a blue ring; error with a red outline and the message below; disabled greyed with the reason as help; read-only (no right to change) as plain text, not as a locked field.
- **Inherit:** every setting that can come from a level above (ring, reboots, Automatic Updates, Microsoft Update, maintenance windows, agent settings) has an "Inherit" switch at its label. While on, the field shows the inherited value dashed and greyed with its source below; switched off, the field is editable.
- **Switches and checkboxes:** a switch turns a feature on or off (channel, schedule, coordination); its fields hang below it, indented with a thin line, greyed while it is off. Plain options inside a form are checkboxes.
- **Adding to a list:** below the list a lightly tinted block holds all fields of the new entry and "+ Add" in one row, the button aligned with the fields; help and warnings sit right below the block.
- **Settings pages:** one form across all boxes with Save (and Discard changes) in the action band at the top; with script "Unsaved changes" appears as soon as something changes, and leaving the page asks first.

### Status indicators

How states are shown everywhere (Marc, 2026-10-25).

- **Five status colours with fixed meanings**, never used for anything else (rings and charts use the categorical palette): green = good (done, healthy, up to date, succeeded, released); blue = in progress (running, pending, updating, waiting for the window); amber = attention (warning, reboot pending, waiting for approval, update available); red = problem (failed, offline, alarm, blocked, withdrawn); grey = neutral (not needed, queued, superseded, expected, off). Colour is never alone: a word or an icon always goes with it.
- **One form per job:**
  - *Pill* (outlined, with a dot): the state of a thing; large in tiles.
  - *Tag* (filled grey, square corners): a category or origin, not a state (classification, "manual", "rule", "SUSyphos", "Installation").
  - *Ring tag*: outlined in the ring's colour with the ring number ("1 Pilot"); one fixed colour per ring.
  - *Dot*: a small indicator next to a value, explained in its tooltip (e.g. heartbeat freshness).
  - *Icon mark*: a state in front of a value (agent version: ✓ current, ↑ update available, – failed).
  - *Edge*: a tile or box that needs attention.
- **Alignment:** an indicator and its text form one line box, centred vertically, with fixed sizes (dot 8 px, icon mark 16 px, pill and tag 20 px high) and a fixed 6 px gap; table rows have one fixed height, so rows with different indicators line up.
- **Severity** of notifications, banners and messages: Critical = red, Warning = amber, Info = blue, as the same pill everywhere.
- **One vocabulary:** a state has one word everywhere ("Failed", not "Error"; "Reboot pending", not "restart pending"; "Offline"; "Up to date"). Words and colours come from one shared table in the code.

### Messages

How the dashboard tells what happened and what lasts (Marc, 2026-10-25).

- **Result of an action:** one line with an icon right below the page header; green when done, red when refused (with what to change). The text says what happened ("The restart of LABSQL01 is queued; …"), never just "Success". Success fades after 10 seconds, an error stays until closed.
- **Toast:** when the page keeps its scroll position after an action, the same message also appears at the top right, with a thin line while a success fades. Without script the line at the top is enough.
- **Lasting banners:** a state of the page's object that lasts (restart planned, installations paused, blackout, update blocked or withdrawn) is a banner below the header in its severity colour, with one sentence on when and why and, on the right, the action that ends it ("Cancel restart", "Resume"). It stays as long as the state; at most two at a time, the more severe first.
- **Notes in boxes and forms:** explanations grey with an "i"; a warning about the current input amber with a "!", right where it applies; no coloured background inside boxes.

### Page header and boxes

- **Page header:** breadcrumb on every page below the top level, icon, title and "?", one subtitle line with the key facts only (explanations belong in the help).
- **Order of a page,** always: messages and banners, action band, tiles, tabs, boxes.
- **Boxes:** title with "?", a short hint on the right (one line, about 60 characters at most), optionally a state pill or switch in the header. A box with its own form has a footer: Save (and further box actions) on the right, an optional note on the left. Tables fill the box edge to edge.
- **Layouts:** one column, two equal columns, or main and side column (2 : 1).

### Fact lists

- Term on the left in a fixed column, value on the right; a value may carry a pill or tag and a muted detail; missing values are "–".
- Long lists get small group headings; links only on values that open more.

### Charts

- Donut sizes: 40 px in table cells, 64 px in tiles, 120 px in boxes (with the total in the middle). Bars are thin.
- Status colours for states, the categorical palette for categories (rings, classifications), assigned in a fixed order.
- A legend from two series on, a tooltip on every mark, and "Show as table" below larger charts.

### Texts and formats

One way to write each kind of value, produced by shared helpers in the code.

- **Date and time:** "2026-10-25 14:23", server time, 24 hours; relative times ("in 9 hours") only as an addition.
- **Durations:** "5 minutes", "1–2 days".
- **Numbers and percentages:** digit grouping and decimal separator come from the regional settings of the central server's operating system (never hard-coded); a percentage is the number, a space and "%" ("75 %").
- **Versions and updates:** "v0.7.7", "KB5066791".
- **Buttons:** verb first, sentence case, "…" only when a dialog follows ("Restart server…"); never "OK".
- **Titles and labels:** sentence case, no full stop. **Notes and messages:** full sentences with a full stop that say what happened.
