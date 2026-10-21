# Identity {#top}

Who may use the dashboard: local accounts, accounts that sign in with Windows (Active Directory) and the AD groups that give them their role. Administrators only. (AD groups here decide who signs in; groups of servers are [Server groups](help/groups).)

## Roles {#roles}

The roles build on each other:

| Role | May |
|---|---|
| **Reader** | See everything: overview, servers, updates, rings. |
| **Operator** | Also decide: approve servers, approve gates, start, stop and override rollouts, pause installations, send remote actions to servers. |
| **Admin** | Also change settings, rules and accounts, restart and remove servers, release agent versions. |

## Accounts {#accounts}

The list shows every account with its **Role** and **State** (Active, Locked by an administrator, or Locked out after five failed sign-ins for 15 minutes). Windows accounts carry the tag "Windows"; their role comes from their AD groups and is set at every sign-in.

- **New user**: opens the [dialog](#new-user) for a local account.
- **Edit**: change role or password of a local account.
- **Lock**: the account can no longer sign in, and its open sessions end. **Unlock** allows it again and also ends a lockout after failed sign-ins.
- **Delete**: opens a confirmation; **Delete user** ends the account and its sign-in at once. Audit entries keep its name. Your own account and the last active administrator cannot be deleted; the last local administrator is always kept, so Windows sign-in can never lock everyone out.

All changes are recorded in the [audit log](help/audit-log).

## New user and edit {#new-user}

![The dialog for a new user](help-images/users-new-dialog.png)

- **Account name**: the name used to sign in (letters, digits and common punctuation; a backslash is reserved for Windows accounts). It cannot be changed later.
- **Role**: Reader, Operator or Admin, see [Roles](#roles).
- **Initial password**, or when editing **New password (empty: keep the current one)**: at least 12 characters; passphrases are welcome, there are no rules about character classes.
- **Create user** or **Save** stores it; **Cancel** closes the dialog.

A role change or a new password ends the sessions of the account within a minute.

## Active Directory sign-in {#windows}

Lets users sign in with their Windows account ("Sign in with Windows" on the sign-in page); on a domain computer the browser does it without asking for a password (Kerberos or NTLM). The tag next to the title shows whether it is on or off.

Their role comes from the AD groups mapped here; if a user is in several mapped groups, the highest role wins. Users in no mapped group cannot sign in. Mappings are stored by the group's SID, so renaming the group in AD does not break them.

- **AD group**: the group as DOMAIN\Group (for example LAB\SUSyphos-Admins) or its SID.
- **Role**: the role members of this group get.
- **Map group** adds the mapping; **Remove** deletes one.
- **Switch on** / **Switch off** turns Windows sign-in on or off. Local accounts keep working either way.

If the configuration file sets `Authentication:Windows:Enabled` to false (emergency switch), nobody can sign in with Windows, whatever is set here. The central server must be a domain member and be opened by its DNS name for Kerberos to work.
