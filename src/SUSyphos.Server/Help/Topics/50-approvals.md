# Approvals {#top}

Decisions that wait for an operator: new servers, ring transitions, stopped coordinated groups and clone suspicions, plus the domains whose servers are approved automatically. The page reloads every 30 seconds, so new servers appear by themselves. Operators and administrators.

## Actions for selected servers {#actions}

Tick new servers in the list below (**Select**), then:

- **Approve selected servers**: the servers become managed. They get their ring and groups from the assignment rules right away (the inventory is already there) and start receiving releases.
- **Reject selected servers**: the servers get nothing and send nothing until they are deleted.

Approve only servers you expect: a new agent registers without any secret, so anyone who can reach the central server could register a machine.

## New servers {#pending}

Agents that registered and wait for a decision, with host name and domain, **Registered from** (the IP address the registration came from), operating system and build, detected roles, registration time and last heartbeat. **Approve** or **Reject** decides for one server; **Select** several and use the buttons above the list to decide for all of them at once. Compare the name, domain and address with what you expect before approving.

## Ring transitions {#transitions}

Updates that wait at a manual gate (**Awaiting approval**) or stopped on an **Alarm** before a ring, with the reason and since when.

- **Approve** (gate) or **Release anyway** (alarm): the update is released to the ring.
- **Stop rollout**: withdraws the update from all servers that have not installed it yet. It can be resumed on the [update details](help/update-detail#actions).

## Stopped coordinated groups {#groups}

Coordinated groups whose installations failed or whose installation slots (leases) expired several times in a row. The central server then gives no member of the group a slot, so nobody in the group installs or restarts. Check the servers of the group (failures, offline servers), then **Resume** it; the count of failed leases in a row starts again at zero.

## Clone suspicions {#blocked}

Servers whose credentials were used by two different hosts in turn, typically a virtual machine cloned with the agent installed. Both host names are shown. The server is blocked until someone decides:

- **Unblock** if only one host really uses the credentials (for example after a rename).
- Otherwise delete the server on its [details page](help/server-detail#remove); the agents then register again, each with its own identity.

## Automatic approval {#domains}

New servers of the listed domains are approved without a person, once the central server finds their computer account enabled in Active Directory and the address they registered from is one DNS lists for them. Without a domain, every new server waits for a manual decision.

Administrators can **Add domain** (enter the DNS domain, for example corp.example.com, and press the button) and **Remove** one. Domains marked "(configuration)" come from the configuration file and can only be changed there. The central server needs to reach the domain controllers (LDAP with signing) and DNS. Changes are recorded in the [audit log](help/audit-log).
