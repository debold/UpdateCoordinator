# Notifications {#top}

The central server tells you about events that need attention, by e-mail and/or a webhook. Every minute it sends each new event once, collected in one message per channel; the [Events](#events) table decides which events go to which channel. A condition is reported once while it lasts and again only after it ended and came back; a failed delivery is retried with the next run. Administrators only.

Passwords and secrets are stored encrypted on this server (Windows data protection) and are not part of the backup; enter them again after a restore on another machine.

The settings are split into two tabs of one form: **Channels** (e-mail, webhook, messages and links; the tab says how many channels are on, amber when none) and **Events** (the event table, with the number of events sent at all). **Save** stores both tabs at once.

## Save and test {#actions}

- **Save** stores all settings on the page. Settings that are incomplete for an enabled channel are refused with a message, and nothing typed is lost.
- **Send a test notification** sends a test message through every enabled channel, using the saved settings (save first). Errors from the mail server or webhook are shown.

Changes are recorded in the [audit log](help/audit-log).

## E-mail {#email}

**Send e-mails** switches the channel on; the other fields are only editable while it is ticked. The tag shows whether it is on or off.

- **SMTP server** and **Port**: the mail server, for example smtp.example.com and 587.
- **Encryption**: "STARTTLS (port 587)", "TLS (port 465)" or "None (internal relay only)". Use "None" only for a relay in your own network.
- **User name**: for SMTP authentication; empty means no authentication.
- **Password**: the password of that user. Empty keeps the stored one; with a user name a password is required. **Remove the stored password** deletes it.
- **Sender address**: the From address, for example susyphos@example.com.
- **Recipients**: one or more addresses, separated by comma, semicolon or new lines.

E-mails are HTML in the look of the dashboard, with a plain text part for mail programs without HTML and for filter rules: a short header (date and time in the time zone of the central server, the central server, the severity Critical, Warning or Info), then each event with its name as heading, what happened, **Affected** and **Impact** where known, and an **Open in SUSyphos** button to the page concerned, and a footer with the address and version of the central server and a link to these settings.

## Webhook {#webhook}

**Call a webhook** switches the channel on.

- **Address (https://)**: the address that receives the notifications; only HTTPS is accepted.
- **Signature secret**: optional. With a secret, every call carries the header `X-SUSyphos-Signature` with `sha256=` and the HMAC-SHA256 of the body, so the receiver can check that the call came from this central server. Empty keeps the stored secret; **Remove the stored secret** deletes it.

The webhook receives a JSON document with subject, time, severity, central server and version, and the events (kind, category, severity, title, key, text, affected, impact and link).

## Messages and links {#links}

- **Subject prefix**: text before every e-mail subject, "[SUSyphos] " by default, so mail filters recognise the messages. Empty for none.
- **Dashboard address for links (optional)**: the address the messages link to. Empty uses this server's own name and HTTPS port (shown as the example in the field); set it when recipients reach the dashboard under another name, for example a DNS alias the certificate is issued for.
- **Offline after (hours)**: how long a server may send no heartbeat before "Server offline" is sent (1 to 168).
- **Restart pending for (days)**: how long a restart may be pending before "Restart pending for long" is sent (1 to 60).

## Events {#events}

Every event the central server can send, grouped as **Critical** (operation or security at risk), **Recommended** (needs an action, not urgently) and **Optional** (for information). Tick **E-mail** and **Webhook** per event; critical and recommended events are on by default, optional ones off. **Save** stores the choice.

- Critical: rollout alarm, update withdrawn by Microsoft, server blocked as a suspected clone, coordinated group stopped, certificate expiring, backup failed, server offline, trust reset on a server, Defender signatures out of date (a server that receives definition updates installed none for more than 48 hours, measured up to its last inventory).
- Recommended: server waiting for approval, gate waiting for approval, agent update failed, installation failed (on a server that still needs the update) (not for definition updates; the next one follows within hours), pre-flight check failed, restart pending for long, installation outside SUSyphos (or Windows set to install on its own) (definition updates excepted), certificate changed, new central server version, administration changed (accounts, roles, AD groups).
- Optional: new agent version, rollout reached a ring (not for definition updates), pause or blackout, new updates (once a day, the updates first reported the day before), daily summary, weekly summary (Mondays).

A condition (for example a server offline) is sent once while it lasts; things that happen (a ring release, an installation outside SUSyphos, a change of the administration) are sent once within a day of happening.
