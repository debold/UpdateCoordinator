# Release rules {#top}

How updates enter the rings (start rules) and when they move on to the next ring (transitions). Administrators only. See [How SUSyphos works](help/concepts#transitions) for the idea behind it.

- **New start rule**: opens the [start rule dialog](#start-rule-dialog).
- **New ring**: opens the [ring dialog](#ring-dialog) to add a ring.

## Rings and transitions {#transitions}

All rings in order with their number of servers and the transition rule to the next ring: how many days after its release in this ring an update may move on (minimum–maximum wait), with which feedback and success rate, whether a manual gate is in place, and the spread of installations. The last ring is the final ring: servers without manual or rule-based assignment land there, and it has no transition.

The next ring gets an update after the minimum wait when enough servers reported (feedback) and enough of them succeeded (success). At the maximum wait a good success rate is enough, even if feedback is missing. A poor success rate, or no report at all at the maximum wait, raises an alarm that an operator decides. The days count for each update separately, from its release in the ring it is in (in the first ring: after the delay of its start rule), not from the start of the rollout.

- **Edit**: opens the ring dialog.
- **Delete**: opens a confirmation; **Delete ring** removes the ring (not the final ring). Only a ring without servers and without assignment rules can be deleted; running rollouts skip it from then on.

## Ring dialog {#ring-dialog}

![The dialog to edit a ring and its transition](help-images/release-rules-ring-dialog.png)

- **Name**: name of the ring, at most 32 characters.
- **Spread installations over (hours)**: 0 to 168. Each server of the ring gets a fixed offset within this time after the release, so not all servers install at once; 0 means no spread. Applies to new releases.
- **Minimum wait (days after the release in this ring)** and **Maximum wait (days after the release in this ring)**: 0 to 90, counted per update from its release in this ring.
- **Feedback rate (%)**: share of the targeted servers of the ring that must have reported a result.
- **Success rate (%)**: share of successful installations among the results.
- **Manual gate**: when the rule is met, the update waits until an operator approves the transition under [Approvals](help/approvals#transitions).

A new ring is added before the final ring and starts with a transition of 2–7 days, feedback 80 % and success 95 %; edit it afterwards. **Create ring** or **Save** stores it; **Cancel** closes the dialog.

## Start rules {#start-rules}

When an approved server reports an update for the first time, the matching start rule with the lowest priority number decides how its rollout starts. Without a matching rule the update stays queued until an operator starts it. The table shows **Priority**, the conditions (**Classification**, **Severity**, **Title pattern**, **Restart**; "any" means no condition), **Start** (automatically, with delay, or manually, and "outside maintenance windows" where allowed) and **Starts in** (the rings the update is released to at once).

Rules are applied when a server reports an update, when a rule is saved and every minute to all waiting updates. A rule decides only once per update; changing it later does not change running rollouts.

- **Edit** opens the start rule dialog; **Delete** opens a confirmation, and **Delete start rule** removes the rule (updates it would start are handled by the next matching rule, or stay queued).

## Updates withdrawn by Microsoft {#withdrawn}

Microsoft sometimes withdraws a faulty update. Windows Update then stops offering it; agents look up the released updates their search did not find and report those Windows Update no longer knows at all. An update counts as **probably withdrawn** when servers report this, no approved server still gets it offered, and neither a newer revision nor a newer update that replaces it explains it. Agents never install an update Windows Update no longer offers.

- **When an update looks withdrawn**: "Stop its rollout and notify" (default) stops the rollout at once and sends a notification; "Only notify" just sends the notification. A stopped rollout can be resumed on the update page.
- **Save** stores the choice.

Updates that are only no longer applicable to a server or hidden on it locally can look the same; that is why one server that still gets the update offered is enough to rule it out, and the update page names the case so you can check Microsoft's release notes. Block the update there to keep it from coming back.

## Start rule dialog {#start-rule-dialog}

![The dialog to create a start rule, with matching updates](help-images/release-rules-start-dialog.png)

- **Classification**: only updates of this classification, or "Any classification".
- **Severity**: only updates with this Microsoft severity, or "Any severity".
- **Restart**: only updates with this restart behaviour as Windows Update reports it: "No restart" (e.g. definition updates), "Restart required", "May restart", or "Any restart behaviour". Updates whose behaviour is not known yet match only "Any".
- **Priority (lower wins)**: 0 to 10000; new rules get the next free number, and two rules cannot share one.
- **Title pattern (\* and ? allowed)**: the whole title must match; use `*` for the varying parts, for example `*Cumulative Update*`.
- **Install outside the maintenance windows**: servers install the updates of this rule as soon as they are released, also outside their windows. Allowed only with the restart condition "No restart", and delivered only while Windows reports the update as never restarting; anything that restarts waits for a window. A global pause still stops them. A new installation brings one such rule for definition updates (no restart, all rings at once, outside the windows); change or delete it like any other rule.
- **Start**: "Automatically" starts the rollout at once; "Manually" keeps the update queued until an operator starts it.
- **Delay before the first installation (hours)**: 0 to 720; servers of the first ring may install only after the delay. The transition wait starts after it.
- **Starts in**: the first ring always gets the update; tick further rings to release it there at once too, for example definition updates in every ring. Rings not ticked follow their transition rules.

**Matching updates** lists known updates the conditions match, and whether a rule with a lower number decides them, they already started, or they start when the rule is saved. **Preview matches** refreshes the list without saving. **Create start rule** or **Save** stores the rule; **Cancel** closes the dialog.

Opened from an update ("Create release rule from this update…"), the dialog is pre-filled with its classification, severity and title.
