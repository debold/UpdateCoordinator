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

Groups give servers common settings and, when **coordinated**, make sure that only a part of the group installs and restarts at the same time, for example domain controllers or cluster nodes ([Server groups](help/groups)).

## Assignment {#assignment}

Servers get their ring and groups from [assignment rules](help/assignment-rules) (by role, domain or name), by hand on their [details page](help/server-detail#assignment), or end up in the last ring (Production) when nothing applies.
