# Notifications {#top}

The central server tells you about events that need attention, by e-mail and/or a webhook. Every minute it sends each new event once, collected in one message per channel; the [Events](#events) table decides which events go to which channel. A condition is reported once while it lasts and again only after it ended and came back; a failed delivery is retried with the next run. Administrators only.

Passwords and secrets are stored encrypted on this server (Windows data protection) and are not part of the backup; enter them again after a restore on another machine.

The settings are split into two tabs of one form: **Channels** (e-mail, webhook, messages and links; the tab says how many channels are on, amber when none) and **Events** (the event table, with the number of events sent at all). **Save** stores both tabs at once.

## Save and test {#actions}

- **Save** stores all settings on the page. Settings that are incomplete for an enabled channel are refused with a message, and nothing typed is lost.
- **Send a test notification** sends a test message through every enabled channel, using the saved settings (save first). The message at the top shows what each receiver answered, or its error.
- **Send test e-mail** (below the E-mail box) and **Send test webhook** (below the Webhook box) test one channel alone, with the saved settings, also while the channel is still switched off, so it can be tried before it goes live. The answer of the mail server or the HTTP status of the webhook (for example "HTTP 202 Accepted") is shown; every test is recorded in the audit log.

Changes are recorded in the [audit log](help/audit-log).

## E-mail {#email}

**Send e-mails** switches the channel on; the other fields are only editable while it is ticked. The tag shows whether it is on or off.

- **SMTP server** and **Port**: the mail server, for example smtp.example.com and 587.
- **Encryption**: "STARTTLS (port 587)", "TLS (port 465)" or "None (internal relay only)". Use "None" only for a relay in your own network.
- **User name**: for SMTP authentication; empty means no authentication.
- **Password**: the password of that user. Empty keeps the stored one; with a user name a password is required. **Remove the stored password** deletes it.
- **Sender address**: the From address, for example susyphos@example.com.
- **Recipients**: one or more addresses, separated by comma, semicolon or new lines.
- **Send test e-mail** (below the box) sends a test e-mail with the saved settings, also while e-mail is off, and shows the answer of the mail server.

E-mails are HTML in the look of the dashboard, with a plain text part for mail programs without HTML and for filter rules: a short header (date and time in the time zone of the central server, the central server, the severity Critical, Warning or Info), then each event with its name as heading, what happened, **Affected** and **Impact** where known, and an **Open in SUSyphos** button to the page concerned, and a footer with the address and version of the central server and a link to these settings.

## Webhook {#webhook}

**Call a webhook** switches the channel on.

- **Address (https://)**: the address that receives the notifications; only HTTPS is accepted.
- **Signature secret**: optional. With a secret, every call carries the header `X-SUSyphos-Signature` with `sha256=` and the HMAC-SHA256 of the body, so the receiver can check that the call came from this central server. Empty keeps the stored secret; **Remove the stored secret** deletes it.
- **Send test webhook** (below the box) calls the saved address with a test message, also while the webhook is off, and shows the HTTP status it answered; see the [example](#webhook-example).

The webhook receives a JSON document with subject, time, severity, central server and version, and the events (kind, category, severity, title, key, text, affected, impact and link). Any 2xx answer counts as delivered; anything else is retried on the next run.

### Example and use in Microsoft 365 {#webhook-example}

A call looks like this (POST, `Content-Type: application/json; charset=utf-8`; times in UTC):

```json
{
  "subject": "[SUSyphos] Critical: Coordinated group stopped",
  "createdAtUtc": "2026-10-27T08:15:00Z",
  "severity": "Critical",
  "server": "uc01.lab.local",
  "version": "0.7.9",
  "events": [
    {
      "kind": "LeasesHalted",
      "category": "Critical",
      "severity": "Critical",
      "title": "Coordinated group stopped",
      "key": "group.halted:1",
      "text": "Installations in the group SQL cluster stopped after a failed installation.",
      "affected": "SQL01, SQL02",
      "impact": "No server of the group installs until the group is released again.",
      "link": "https://uc01.lab.local:8443/administration/groups"
    }
  ]
}
```

With a signature secret the receiver can check the sender. In PowerShell, with `$body` the raw body as received (bytes) and `$header` the value of `X-SUSyphos-Signature`:

```powershell
$hmac = [System.Security.Cryptography.HMACSHA256]::new([Text.Encoding]::UTF8.GetBytes('your secret'))
$expected = 'sha256=' + [Convert]::ToHexString($hmac.ComputeHash($body)).ToLowerInvariant()
$valid = [System.Security.Cryptography.CryptographicOperations]::FixedTimeEquals([Text.Encoding]::ASCII.GetBytes($expected), [Text.Encoding]::ASCII.GetBytes($header))
```

**Messages in a Microsoft Teams channel** (the most common use): Teams receives webhooks through Workflows (Power Automate); the older "Incoming Webhook" connectors are being retired by Microsoft.

1. In Power Automate (or in Teams: **Workflows**), create a flow with the trigger **When a Teams webhook request is received** and set who may trigger it to "Anyone". Saving the flow shows its address (HTTP URL).
2. Add **Parse JSON** with the body of the trigger as content and generate the schema from the example above.
3. Add **Post message in a chat or channel** (post as Flow bot, choose team and channel) with, for example, the subject, then an "Apply to each" over `events` with title, text, affected and the link.
4. Paste the address of the flow into **Address (https://)**, save and use **Send test webhook**: the test message appears in the channel.

The address of the flow carries its own signature and works like a password: keep it secret. A flow can also check the `X-SUSyphos-Signature` header, but a Teams channel usually relies on the secret address.

Other uses in Microsoft 365, built the same way: a flow that creates a Planner task or a ticket (ServiceNow, Jira and similar) only for events with severity "Critical" (a condition on `severity`), or an Azure Logic App that writes the events to Log Analytics for Microsoft Sentinel.

## Messages and links {#links}

- **Subject prefix**: text before every e-mail subject, "[SUSyphos] " by default, so mail filters recognise the messages. Empty for none.
- **Dashboard address for links (optional)**: the address the messages link to. Empty uses this server's own name and HTTPS port (shown as the example in the field); set it when recipients reach the dashboard under another name, for example a DNS alias the certificate is issued for.
- **Offline after (hours)**: how long a server may send no heartbeat before "Server offline" is sent (1 to 168).
- **Restart pending for (days)**: how long a restart may be pending before "Restart pending for long" is sent (1 to 60).

## Events {#events}

Every event the central server can send, grouped as **Critical** (operation or security at risk), **Recommended** (needs an action, not urgently) and **Optional** (for information). Tick **E-mail** and **Webhook** per event; critical and recommended events are on by default, optional ones off. **Save** stores the choice.

- Critical: rollout alarm, update withdrawn by Microsoft, server blocked as a suspected clone, coordinated group stopped, certificate expiring, backup failed, server offline, trust reset on a server, Defender signatures out of date (a server that receives definition updates installed none for more than 48 hours, measured up to its last inventory).
- Recommended: server waiting for approval, gate waiting for approval, agent update failed, installation failed (on a server that still needs the update) (not for definition updates; the next one follows within hours), pre-flight check failed, restart pending for long, installation outside SUSyphos (or Windows set to install on its own) (definition updates excepted), support ending (the Windows version of a server reaches its end of support within 180 days, sent again after the end while no Extended Security Updates are found and once more when the first patch day after the end passed without them), certificate changed, new central server version, administration changed (accounts, roles, AD groups).
- Optional: new agent version, rollout reached a ring (not for definition updates), pause or blackout, new updates (once a day, the updates first reported the day before), daily summary, weekly summary (Mondays).

A condition (for example a server offline) is sent once while it lasts; things that happen (a ring release, an installation outside SUSyphos, a change of the administration) are sent once within a day of happening.
