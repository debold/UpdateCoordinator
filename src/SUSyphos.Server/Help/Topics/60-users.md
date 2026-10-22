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

- **AD group**: the group as DOMAIN\Group (for example LAB\SUSyphos-Admins) or its SID. While you type (two characters or more) a list shows the matching groups from Active Directory; choose one to fill the field. Without a connection to the directory the field still takes a typed name.
- **Role**: the role members of this group get.
- **Add** (next to the role) adds the mapping; **Remove** deletes one. When you pick a privileged group from the suggestions (Domain Admins, Enterprise Admins, Administrators and the like), a warning appears right under the field, and such a mapping is marked "usually gives no role (UAC)" in the table: Windows filters these groups to "deny only" in a logon without elevation.
- **Switch on** / **Switch off** turns Windows sign-in on or off. Local accounts keep working either way.

If the configuration file sets `Authentication:Windows:Enabled` to false (emergency switch), nobody can sign in with Windows, whatever is set here. The central server must be a domain member and be opened by its DNS name for Kerberos to work.

### Troubleshooting Windows sign-in {#windows-troubleshooting}

When "Sign in with Windows" ends with a message instead of the dashboard, the Windows part usually worked already; the role is missing. Look in the [audit log](help/audit-log) for the entry **Login.WindowsNoRole**: its actor is the account the browser signed in with, and its details name the authentication used (Negotiate, Kerberos or NTLM), how many groups were in the token and whether a mapped group was filtered. Common causes, most frequent first:

1. **A privileged group is mapped**, such as Domain Admins, Enterprise Admins, Schema Admins or Administrators. In a session without elevation (User Account Control) Windows keeps these groups only as "deny only" in the token, so they give no role. `whoami /groups` shows them as "Group used for deny only". The sign-in page then says so. Map a dedicated group instead, for example DOMAIN\SUSyphos-Admins; mapping a privileged group shows a warning.
2. **The browser uses another account**: it signs in with the account logged on to Windows on that computer, not with an administrator account you use elsewhere. The audit entry shows which one.
3. **The group was not mapped**: the AD group field only takes effect with **Add**; **Switch on** alone does not save it. The group must be in the table above.
4. **New group membership**: Windows reads groups at logon. After adding the account to a group, sign out of Windows and in again (or run `klist purge`).

Kerberos needs no extra setup on the central server's own name: the service runs as LocalSystem and the computer account's HOST/ SPN covers it. Only for a DNS alias register an SPN (`setspn -S HTTP/<alias> <computer name>`). For sign-in without any prompt, the browser must treat the address as intranet: add it (or `*.yourdomain`) to the Local intranet zone, or set the Edge policy `AuthServerAllowlist`. An untrusted certificate ("Not secure") does not stop the sign-in, but should be replaced by a certificate from your CA ([TLS certificate](help/certificate)).
