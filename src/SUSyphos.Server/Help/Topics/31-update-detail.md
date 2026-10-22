# Update details {#top}

One update with its rollout through the rings and its state on every server. The line under the title shows classification, severity, revision, restart behaviour and when it was first reported; the tag next to the title shows the rollout state as in the [update list](help/updates#columns).

When a newer update replaces this one (Windows Update supersedence), a note names it. Each ring then gets the newer update as soon as it is released there: servers that get the newer one no longer get this one, the others keep it. Once the newer update has reached every ring this one reached, this rollout stops by itself ("Rollout stopped: superseded by …" in the timeline).

## Actions for this update {#actions}

Operators and administrators:

- **Approve for** a ring: a manual gate before this ring is passed and the update is released to it.
- **Release anyway for** a ring: an alarm before this ring is overruled and the update is released to it. Look at the failures in the previous ring first.
- **Start rollout** / **Resume rollout**: starts a queued update or resumes a stopped rollout.
- **Release to all rings**: releases the update to every ring at once, skipping wait times, rates and gates.
- **Stop rollout**: withdraws all releases that are not installed yet; can be resumed later.
- **Block**: stops the rollout like "Stop rollout" and locks the update: neither a start rule nor an operator starts it again until someone chooses **Unblock** (which leaves the rollout stopped; resume it when the update should be installed again). For an update known to be faulty.

Administrators also have:

- **Block and uninstall…**: opens the [uninstall dialog](#uninstall).

A red note says when the update is **blocked** (by whom and since when) or **probably withdrawn by Microsoft**: servers that had it released report that Windows Update no longer knows it, did not install it, and no newer update or revision replaces it. Depending on the setting under [Release rules](help/release-rules#withdrawn) the rollout was stopped or only a notification sent. The note goes away when a server gets the update offered again; a stopped rollout stays stopped.

## Block and uninstall {#uninstall}

Blocks the update and lets every server that reports it as installed uninstall it with its next heartbeat, also outside the maintenance window. The agent uninstalls through Windows Update; where Windows Update cannot remove an update (for example combined servicing stack and cumulative packages) it uses DISM with the KB number. Some packages, such as servicing stack updates, cannot be removed at all; the remote action on that server then reports why.

- **Uninstall and restart right after**: if Windows needs a restart, the server restarts at once after a five minute warning.
- **Uninstall, restart in the next window**: a needed restart waits for the next maintenance window that stays open long enough.
- **Cancel** closes the dialog without changing anything.

The local veto (registry value AllowReboot) prevents the restart. Each server shows the action and its result under its remote actions.

Administrators also have **Create release rule from this update…**, which opens a new start rule pre-filled with the classification, severity and title of this update ([Release rules](help/release-rules#start-rules)).

## Start rule {#start-rule}

Which start rule started the rollout and when, or, before a rollout, which rule would decide now (with its mode: automatic with delay, or manual start). "No start rule matches" means the update stays queued until an operator starts it. Start rules decide only once; changing a rule later does not change running rollouts.

## Progress by ring {#progress}

One row per ring:

- **State**: *Waiting* (the previous ring has not met its transition rule yet), *Released*, *Waiting for manual release* (gate), *Alarm* with the reason, or nothing when the rollout has not reached the ring.
- **Released**: when the update was released to the ring. For rings the rollout has not reached yet it shows the **expected** time: from the release of the last reached ring plus the minimum waits of the transitions (all servers report at once) to the same plus the maximum waits (a good success rate without full feedback). **manual approval** marks a ring that needs an operator on the way. It is an estimate: low feedback, alarms and gates change it, and rings behind an alarm or an open gate show none.
- **Feedback**: share of the targeted servers of the ring that reported a result (reported / targeted).
- **Success**: share of successful installations among the results. The last result per server counts, so a successful retry replaces a failure.
- **Status (servers)**: the servers of the ring by state. Click the ring to see them one by one.

## All servers {#all-servers}

All servers that reported the update, by state: successful, pending (released, waiting for the window or installing), queued (not released to their ring yet), failed and not needed.

## Servers {#servers}

Every server concerned, with **Ring**, **Status**, **Earliest installation** and **Result**. The status:

- **Failed**: the last installation failed (the result shows the error code and an explanation).
- **Pending: window or installing**: released and due; installs in the current or next maintenance window.
- **Planned**: released, but the earliest installation is still ahead (a start delay or the spread of the ring).
- **Waiting for release**: reported as missing, not released to the server's ring yet.
- **Installed**: installed, by SUSyphos or outside it (reported by the inventory).
- **Not needed**: the server does not need it.

## Timeline {#timeline}

Everything that happened to the rollout: start, releases per ring, gates, alarms, approvals, stops and overrides, with time and who did it (a person or the rules).
