# How SUSyphos works {#top}

A short tour of the ideas behind the dashboard: where updates come from, how they move through the rings and when servers install them.

## Agents and the central server {#agents}

Every managed server runs the SUSyphos agent. It searches Windows Update (or Microsoft Update) itself, reports the missing updates, its inventory and its state to the central server, and installs what the central server releases for it. The agent always starts the connection; the central server never connects to a server. A heartbeat every few seconds carries the state and picks up remote actions.

New agents must be approved before they get anything ([Approvals](help/approvals)).

## Updates and rollouts {#rollouts}

An update appears in the dashboard when an approved server reports it as missing. A **start rule** ([Release rules](help/release-rules#start-rules)) decides once what happens with it: start the rollout automatically (optionally after a delay), or keep it **queued** until an operator starts it.

A rollout runs each update separately through the **rings** in their order, for example Pilot, Test and Production. In each ring the update is **released**: the servers of that ring that need it install it in their next maintenance window.

## Ring transitions {#transitions}

When an update moves on to the next ring is decided by the **transition rule** of the ring it is in ([Release rules](help/release-rules#transitions)):

- **Minimum wait**: the update stays at least this long in the ring, counted from its release there (each update on its own, not from the start of the rollout).
- **Feedback**: the share of the servers of the ring that reported a result.
- **Success**: the share of successful installations among those results (offline servers do not lower it).
- **Maximum wait**: when it is reached and the success rate is good, the update moves on even if feedback is missing.
- **Manual gate**: the update waits until an operator approves the next ring.

If the success rate is poor, or nothing was reported at the maximum wait, an **alarm** stops the rollout before the next ring until an operator decides (release anyway, or stop the rollout). Operators can also **release to all rings** at once (for urgent updates) or **stop** a rollout.

## Maintenance windows and reboots {#windows}

A released update installs only inside a maintenance window of the server, in the server's local time. Windows come from the server itself, its groups or the system default ([Maintenance windows](help/maintenance-windows)). Restarts follow the reboot policy: inside the window, or never (report only). The global pause and blackout periods stop all new installations.

## Groups and coordination {#groups}

Groups give servers common settings and, when **coordinated**, make sure that only a part of the group installs and restarts at the same time, for example domain controllers or cluster nodes ([Server groups](help/groups)). Software (WinGet) is not coordinated: it installs without waiting for a slot, and a failed package does not stop the group; only a restart it needs waits for a slot.

## Assignment {#assignment}

Servers get their ring and groups from [assignment rules](help/assignment-rules) (by role, domain or name), by hand on their [details page](help/server-detail#assignment), or end up in the last ring (Production) when nothing applies.

## The frame of every page {#layout}

Every page of the dashboard has the same frame:

- **Top bar** (dark): the SUSyphos mark opens the overview; **Search** in the middle finds servers, updates, KB numbers, software packages, pages and help topics (see [Search](#search)). On the right:
  - **My requests** (the list icon): your own remote actions, see [My requests](#my-requests); a red number counts the open ones.
  - **Notifications** (the bell): what waits for a person, with a red number: decisions in Approvals (operators and administrators), a central server in debug mode, a newer central server; and the way to **Needs attention** on the overview.
  - **Settings** (the gear, administrators): opens the SUSyphos page.
  - **Help** (the question mark): opens this help in a new tab.
  - **Account** (your initials): your name, role and the version of the central server, and **Sign out**.
- **Rail** (left): the pages you need most, Overview, Servers, Updates, Software and Rollout (administrators). **Hide navigation** folds the navigation away and **Show navigation** brings it back; the choice stays in this browser. Without a choice the navigation folds away by itself in windows narrower than 1400 pixels, so lists keep their room.
- **Navigation**: the pages grouped as Devices, Updates & software, Rollout, Reports and Administration; a click on a group opens or closes it, and closed groups stay closed in this browser (the group of the current page always opens). **Find a page** narrows the navigation to matching pages while you type; Enter searches everything. Numbers next to a page count what waits there, for example the decisions in Approvals.
- **Content**: the page on a white surface.

On a narrow screen (a phone or a small window) the rail goes and the menu button at the top left opens the navigation. Everything works without the dashboard script as well; then the navigation is hidden or shown by reloading the page.

## Search {#search}

**Search** in the top bar (and **Search for** on its result page) finds, with a part of the name or number:

- **Pages** of the dashboard by their name in the navigation,
- **Servers** by host name or domain,
- **Updates** by title or KB number (with or without "KB"),
- **Software** packages by name or WinGet ID,
- **Help** topics by their text.

Each kind shows at most 20 results; a single matching server, update or package opens directly. The search reads only and shows only what your role may see on its pages.

## My requests {#my-requests}

**My requests** in the top bar lists the remote actions you requested yourself, never those of others: open ones (requested, picked up by the agent, running) and those finished within the last hour, each with its server, its step and, for log requests, **Download logs**. The number shows how many are open, with a pulsing dot while any is. With script the list refreshes every 10 seconds, and when one of your requests finishes a message appears at the bottom right: for 10 seconds when it succeeded, until you close it when it failed. Without script the list shows the state when the page was loaded.
