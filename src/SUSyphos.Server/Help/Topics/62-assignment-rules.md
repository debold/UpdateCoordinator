# Assignment rules {#top}

Rules put servers into rings and groups automatically, by their detected roles, their domain and their host name. Administrators only.

- **Ring**: a manual ring on the server's [details page](help/server-detail#ring) wins; otherwise the matching ring rule with the lowest priority number decides; otherwise the server goes to the final ring (Production).
- **Groups**: every matching group rule adds its group, in addition to the groups set by hand.

Assignments are recalculated when a server registers, after every inventory, after every change of rules, rings or groups, and when the central server starts. A server that moves to another ring gets the releases of its new ring from then on.

## Rule list {#list}

- **New rule…**: opens the [rule dialog](#dialog).

The table shows each rule with **Priority**, what it **Assigns** (a *Ring* or *Group* tag with the ring tag or the group name; the link opens the server list filtered by that ring or group), its **Conditions** (roles as tags, then the domain and the host name pattern), and **Servers**: how many approved servers the conditions of the rule match, linked to the server list with exactly these servers. A matched server can still be in another ring: a manual ring on the server or a ring rule with a lower number decides first. All conditions of a rule must match; empty conditions match every server; a rule with several roles matches servers with any of them.

- **Edit** (the pencil at the end of the row): opens the rule dialog.
- **More** (⋯): **Show matching servers** opens the server list with the servers the conditions match; **Delete rule…** opens a confirmation, and after **Delete rule** servers assigned by the rule are recalculated at once and may move to another ring or leave the group.

## New rule and edit {#dialog}

![The dialog to create or edit an assignment rule](help-images/assignment-rule-dialog.png)

- **Assigns**: the ring or group the rule puts matching servers into.
- **Priority (lower wins)**: 0 to 10000. When several ring rules match, the one with the lowest number decides. For group rules the priority only orders the list.
- **Roles (any of them)**: roles the agent detected on the server: the built-in ones (DomainController, Exchange, SqlServer, ClusterNode, HyperVHost) and every role a server has reported. Further roles can be defined on the agents (`Inventory:RoleRules`). "Any role" leaves the condition out.
- **Domain**: the DNS domain of the server, for example lab.local.
- **Host name pattern**: the host name with `*` for any number of characters and `?` for exactly one, for example SQL* or WEB0?. Upper and lower case do not matter.

Below the fields, **What changes** (with "… of … approved servers ›") opens the list of the approved servers the rule as typed would affect: for a ring rule the servers that move (current ring → new ring), and those it matches but that keep their ring because it was set by hand or a rule with a lower number decides; for a group rule the servers that join the group, or leave it when an edited rule no longer matches them. **Preview** updates the list after a change without saving and opens it. Changes come first; at most 15 are listed. A rule for a coordinated group warns above the list when servers would then be in more than one coordinated group: such a server needs a lease of every group, and a failure counts in each.

**Create rule** or **Save** stores the rule and recalculates the assignments; **Cancel** closes the dialog.
