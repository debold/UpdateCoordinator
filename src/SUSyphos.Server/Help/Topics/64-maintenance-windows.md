# Maintenance windows and reboots {#top}

The system defaults for when servers may install updates and restart, how Windows' own update setting is handled, and periods in which nothing is installed at all. Groups and single servers can override the defaults; a server uses its own setting first, then its groups, then the system default. Administrators only.

## Maintenance windows {#windows}

The default windows for every server that has no windows of its own and is in no group with windows. Agents install only inside a window, in the **local time of the server**: a window from 22:00 to 04:00 opens at 22:00 on each server in its own time zone. A window that ends before it starts runs past midnight.

**Save windows** stores the windows; agents get them with their next heartbeat. **Discard changes** reloads the saved windows. Without any window a server installs nothing.

## The window editor {#editor}

The same editor for maintenance windows is used here, for [groups](help/groups) and for [single servers](help/server-detail#windows). Each row is one window:

- **weekday** buttons (Mo to Su): the days on which the window starts. Select at least one.
- **from** and **to**: start and end in half-hour steps, 24-hour clock. "to 24:00" ends at midnight; an end before the start runs into the next day.
- **weeks**: "every week", or a monthly rule: "only in" or "all except" the ticked **weeks of the month** (1st, 2nd, 3rd, 4th, last). The week counts the occurrence of the weekday in the month: "only in 2nd" on Tuesday is the second Tuesday of the month (Patch Tuesday); "all except last" on Saturday is every Saturday except the last one of the month. The "last" Saturday can also be the fourth.
- **Remove** deletes the row.

**Add window** adds a row (up to 14). **Refresh preview** redraws the preview; it is only shown without the dashboard script, which otherwise redraws it on every change. "Not saved yet" reminds you that rows were added or removed but not stored.

Below the rows, the week grid shows the hours covered by the windows (from Monday to Sunday), a sentence describes them, and "Next openings" lists the next start times in server time.

Where windows can be inherited (groups and servers), the box **Inherit** ("Inherit from groups or the system default" or "Use the system default") uses the windows from the level above; the rows are then disabled. Untick it to give the group or server windows of its own.

## Reboots {#reboots}

The **System default** for restarts after updates, used by every server without a policy of its own or from a group:

- **Reboot inside the maintenance window**: the agent restarts a server that needs it inside its window, only while the window stays open for at least 30 more minutes, after a five-minute warning, at most once per six hours, never during a global pause or blackout.
- **Never reboot (report only)**: the agent only reports the pending restart.

A server's own policy wins; otherwise its groups decide, and one group with "Never" wins over the others. A server can always forbid restarts locally with the registry value `AllowReboot` = 0. **Save reboot policy** stores the default.

## Windows Automatic Updates {#automatic-updates}

Windows can install updates and restart on its own (Automatic Updates), outside rings and maintenance windows. The **System default** decides what the agent does about it:

- **Leave unchanged, only warn**: the agent does not touch the setting; servers on which Windows may install by itself get a warning.
- **Keep Windows from installing on its own**: on servers where no group policy controls Automatic Updates, the agent sets "Configure Automatic Updates" to option 3 (download and notify, but do not install) and restores the previous setting when this is switched off again. A group policy always wins.

Groups can override the default; "leave unchanged" in one group wins. **Save** stores this box (together with the Microsoft Update setting).

## Other Microsoft products {#microsoft-update}

- **Windows Update only**: updates for Windows and its components.
- **Microsoft Update**: the agent also registers the Microsoft Update service (like "Receive updates for other Microsoft products" in Windows) and searches and installs through it, so SQL Server, Office and other Microsoft products are covered, also on servers that otherwise use WSUS. When switched off, the agent removes only a registration it made itself.

Groups can override the default; "Windows Update only" in one group wins.

## Blackout periods {#blackouts}

Times in which no installation starts anywhere, for example a year-end freeze: like a planned global pause. Searches, inventory and reporting continue; running installations finish. Times are in the time zone of the central server.

The table lists the periods with **Start**, **End**, **Reason** and **State** (planned, active, ended); **Remove** deletes one.

To add one, enter **Start** (date and time), **End** (date and time) and a **Reason** (shown on the overview while the period is active), then **Add blackout period**. A period may last at most 90 days. Additions and removals are recorded in the [audit log](help/audit-log).
