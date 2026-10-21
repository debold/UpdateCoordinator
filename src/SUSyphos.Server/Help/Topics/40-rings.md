# Rings {#top}

A ring is a set of servers that get updates at the same stage of a rollout, for example Pilot, Test and Production ([How SUSyphos works](help/concepts#transitions)). The band at the top shows all rings in order with their number of servers and, between them, the wait time of the transition ("2–7 days", counted per update from its release in the ring before) and whether a manual gate is in place. Click a ring to open it.

The line under the title summarises the transition rule of this ring: after how many days, with which feedback and success rate the update moves on to the next ring.

## Actions {#actions}

- **Open servers of this ring**: the [server list](help/servers) filtered to this ring.
- **Add servers…** (administrators): opens the [dialog](#add-servers) to move servers into this ring by hand.
- **Edit ring rules**: the transition rules under [Release rules](help/release-rules#transitions).
- **Edit assignment rules**: the rules that put servers into rings, see [Assignment rules](help/assignment-rules).

## Updates in this ring {#updates}

Updates released to this ring, and updates its servers report as missing that are not released here yet. Click a title to see its state per server in the tab below.

- **State**: *Rolling out* (servers still have to install it), *Released* (done in this ring), *Waiting for manual release*, *Alarm* with the reason, *Waiting for the ring before* (the rollout has not reached this ring yet) or *Queued: no rollout yet* (servers here report it as missing, but no rollout runs). The list holds every update the ring status counts.
- **Installs from**: when the ring's servers may install it at the earliest.
- **Day**: how long the update has been in the ring, compared with the minimum and maximum wait.
- **Feedback** and **Success**: the rates the transition rule looks at (reported / targeted servers).
- **Servers**: the servers of the ring by state.

## Ring status {#status}

Every server of the ring for every update released here: successful, pending (released, waiting for the window or installing), queued (reported as missing, not released for this ring yet), failed, not needed (installed otherwise or not applicable).

## Servers in this ring {#servers}

The tab lists the members of the ring with operating system, build, **Assigned by** (manual, an assignment rule, or the fallback to the last ring), status and pending updates. Search, sorting and filters work as described in [Working with lists](help/tables).

The second tab, **Status of … per server**, appears after selecting an update: each server of the ring with its maintenance windows (server time), earliest installation, state and result. **Update details ›** opens the update.

## Add servers {#add-servers}

![The dialog to add servers to a ring](help-images/ring-add-servers.png)

Tick the servers to move (**Move**) and confirm with **Move to** the ring. They are assigned to this ring by hand: assignment rules no longer move them. Their groups and maintenance windows stay. **Cancel** closes the dialog. To give a server back to the rules, set its ring to "Automatic" on its [details page](help/server-detail#ring).
