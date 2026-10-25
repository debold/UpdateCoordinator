# Rings {#top}

A ring is a set of servers that get updates at the same stage of a rollout, for example Pilot, Test and Production ([How SUSyphos works](help/concepts#transitions)). An update reaches the first ring, waits there for the time and the feedback and success rates the transition rule asks for, and moves on to the next ring.

Rings have no page of their own. Everywhere a ring appears it is a ring tag: its number and name, outlined in one fixed colour per ring.

## Where to find rings {#where}

- **Servers**: the strip above the [server list](help/servers#rings) shows all rings in order with their number of servers and, between them, the wait of the transition and whether a manual gate is in place. A ring tag there filters the list to that ring; several can be on at once. **Assign** in the band moves the selected servers to a ring by hand.
- **Updates**: the **Rings** column of the [update list](help/updates) shows for each update how far its rollout came in each ring; the [update page](help/update-detail#progress) has the progress per ring with feedback and success rates.
- **Overview**: [Deployment by ring](help/overview#rings) shows the state of each ring.
- **Release rules**: the [Rings tab](help/release-rules#transitions) holds the transition rules (wait, feedback, success, manual gate).
- **Assignment rules**: the [rules](help/assignment-rules) put servers into rings; a server set to a ring by hand on its [details page](help/server-detail#ring) or with **Assign** stays there.
