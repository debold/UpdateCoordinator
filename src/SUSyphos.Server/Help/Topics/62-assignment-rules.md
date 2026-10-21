# Assignment rules {#top}

Rules put servers into rings and groups automatically, by their detected roles, their domain and their host name. Administrators only.

- **Ring**: a manual ring on the server's [details page](help/server-detail#ring) wins; otherwise the matching ring rule with the lowest priority number decides; otherwise the server goes to the final ring (Production).
- **Groups**: every matching group rule adds its group, in addition to the groups set by hand.

Assignments are recalculated when a server registers, after every inventory, after every change of rules, rings or groups, and when the central server starts. A server that moves to another ring gets the releases of its new ring from then on.

## Rule list {#list}

- **New rule**: opens the [rule dialog](#dialog).

The table shows each rule with **Priority**, what it **Assigns** (a ring or a group), its **Roles**, **Domain** and **Host name pattern**. All conditions of a rule must match; empty conditions match every server; a rule with several roles matches servers with any of them.

- **Edit**: opens the rule dialog.
- **Delete**: opens a confirmation; after **Delete rule**, servers assigned by the rule are recalculated at once and may move to another ring or leave the group.

## New rule and edit {#dialog}

![The dialog to create or edit an assignment rule](help-images/assignment-rule-dialog.png)

- **Assigns**: the ring or group the rule puts matching servers into.
- **Priority (lower wins)**: 0 to 10000. When several ring rules match, the one with the lowest number decides. For group rules the priority only orders the list.
- **Roles (any of them)**: roles the agent detected on the server: the built-in ones (DomainController, Exchange, SqlServer, ClusterNode, HyperVHost) and every role a server has reported. Further roles can be defined on the agents (`Inventory:RoleRules`). "Any role" leaves the condition out.
- **Domain**: the DNS domain of the server, for example lab.local.
- **Host name pattern**: the host name with `*` for any number of characters and `?` for exactly one, for example SQL* or WEB0?. Upper and lower case do not matter.

**Create rule** or **Save** stores the rule and recalculates the assignments; **Cancel** closes the dialog.
