# Audit log {#top}

Every change in the administration and every decision, with time, actor, action, target and details: sign-ins (successful, failed, locked out), sign-outs, approvals, rollout decisions, remote actions, changes of rules, groups, windows, accounts, certificate and notifications, backups and agent downloads. Administrators only.

## Integrity check {#integrity}

The log is append-only: the database refuses changes and deletions, and every entry carries a SHA-256 hash over its content and the previous entry (a hash chain). The page checks the whole chain on every visit:

- **Integrity check passed: the hash chain is intact.**
- **Integrity check failed at entry …**: the database file was modified outside SUSyphos at that entry. Treat it as a security incident; compare with a backup.

## Entries {#entries}

Newest first, 50 per page, times to the second in the time zone of the central server. Search in target and details; filter by **Actor**, **Action** and **Target** with the column filters, as described in [Working with lists](help/tables). **View** offers **All entries** (default), **Sign-ins** (sign-ins, sign-outs, refused sign-ins and the first setup), **Rollouts and approvals** (rollouts, ring approvals, held and blocked updates, approved servers, released agent versions) and **Settings changes** (rings, rules, groups, maintenance windows, notifications, accounts and other settings); each sets the **Action** filter to the actions of that area. Long values are shortened.
