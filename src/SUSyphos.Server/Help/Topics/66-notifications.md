# Notifications {#top}

The central server tells you about events that need attention, by e-mail and/or a webhook. Every minute it sends each new event once, collected in one message: new servers waiting for approval, suspected clones, rollout alarms, updates waiting at a gate, stopped coordinated groups, agent versions waiting for release, failed agent updates, failed scheduled backups and certificate events (expiring soon, changed). A condition is reported once while it lasts and again only after it ended and came back; a failed delivery is retried with the next run. Administrators only.

Passwords and secrets are stored encrypted on this server (Windows data protection) and are not part of the backup; enter them again after a restore on another machine.

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

## Webhook {#webhook}

**Call a webhook** switches the channel on.

- **Address (https://)**: the address that receives the notifications; only HTTPS is accepted.
- **Signature secret**: optional. With a secret, every call carries the header `X-SUSyphos-Signature` with `sha256=` and the HMAC-SHA256 of the body, so the receiver can check that the call came from this central server. Empty keeps the stored secret; **Remove the stored secret** deletes it.

The webhook receives a JSON document with subject, time and the events (kind, key, text and link).

## Links {#links}

**Dashboard address for links (optional)**: the address the messages link to, for example https://susyphos.example.local:8443/. Without it, messages contain no links.
