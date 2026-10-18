# Communication Protocol Agent ↔ Central Server

> See `01-architecture.md` for the overall context and `04-central-server.md` for data storage.

## 1. Transport and format

- **REST with JSON over HTTPS**, implemented with ASP.NET Core (controllers/endpoints, model binding, validation, OpenAPI via framework features).
- No hand-coded protocol or format details.

## 2. Communication model

- **The agent always initiates.** The central server never opens a connection to an agent.
- The agent sends a frequent **heartbeat** (guideline 5–15 seconds). It is a simple ping/API call and checks whether actions are queued for the agent.
- The central server keeps a **queue** of arbitrary actions per client. The agent fetches it, executes the actions in the given order and continuously reports the status per action (fetched, running with progress in %, completed).
- This status channel can later also carry live log retrieval (the agent delivers log data on request from the queue).
- Central server unreachable: the agent buffers outgoing information (reports, status) locally and sends it once the connection is restored (no loss of information when an acknowledgement is missing).

## 3. Authentication and trust

There is **no secret, no token and no certificate on the client side** for registration. Onboarding of new agents is decided exclusively by the **approval mechanism** (see section 4.1). Communication runs over HTTPS.

### 3.1 Client identity (client GUID)

- On registration the **central server** issues a random **client GUID** (v4) and returns it in the register response. The agent stores it locally (protected with DPAPI) and sends it with every message.
- The GUID identifies the client uniquely **across renames and domain rejoins**. Host name, domain and SID are only attributes: changes are logged but do not trigger a new approval.
- The approval (see 4.1) refers to the GUID.
- **Clone detection (draft):** if the same GUID reports from two sources at the same time (e.g. a cloned VM), the central server flags a clone suspicion and blocks the GUID until an admin resolves it.
- **Reinstallation (draft):** the GUID is lost with the operating system and the server registers again. If the name matches, the central server suggests taking over the old entry including its history.
- The GUID is an identifier, not a secret. It therefore does not belong in dashboard URLs or the SIEM export (draft: use an internal ID there).

### 3.2 Identity of the central server (trust anchor)

The agent verifies that it is talking to the genuine central server. A certificate change must not lock out agents and must be approvable centrally. The client installer deliberately contains **no runtime data** (no certificate, no fingerprint, not even as an installation parameter), so it can be used unchanged in any infrastructure. Decided on 2026-10-18:

- **Trust anchor instead of a TLS pin:** at installation the central server generates its own long-lived signing key pair (trust anchor). The private key is part of the backup. Only the public anchor is pinned, not the TLS certificate.
- **Trust on first use:** the agent stores the public anchor on first contact (protected with DPAPI, like the client GUID).
- **Certificate binding:** the central server signs the SPKI hash of its currently valid TLS certificate with the anchor and serves this binding on a dedicated endpoint. The binding is self-authenticating and may therefore be fetched over a TLS connection that is not yet verified. The agent accepts a TLS certificate only if a valid binding to its SPKI, signed with the pinned anchor, exists. All other messages are sent only afterwards. Every binding carries an increasing serial number; the agent rejects bindings with a serial number lower than the highest one it has seen, so a retired (possibly compromised) certificate cannot be reused.
- **Certificate change:** the admin uploads the new certificate in the dashboard and approves it, and the central server signs the new binding. Agents follow automatically on their next contact, regardless of whether key, CA or validity period change. No advance announcement is needed, and offline agents are not locked out.
- **Local override (registry):** an administrator of the server can force the agent to discard the stored trust and pin again on the next contact (trust on first use). Mechanism: a value under `HKLM\SOFTWARE\Policies\<Product>\Agent` (draft: `TrustResetToken`, string). The agent performs the reset exactly once whenever the value differs from the last processed one, so it also works with GPOs that rewrite values periodically. The reset is written to the Windows event log and reported to the central server, which shows it in the audit log and dashboard. Only local administrators, who control the agent anyway, can write this key.
- **Limit:** if the anchor is lost (without backup) or compromised, central re-trust is impossible in principle, because an attacker could otherwise do the same. Only the local override or a reinstallation of the agent remains. Until then the agent sends nothing, reports "central server not trusted" locally and keeps retrying with backoff.

**Forged success reports:** are addressed by approval and GUID identification (only approved clients are accepted). A content plausibility check (e.g. build number against the expected update) was deliberately rejected, because legitimate cases such as a manual rollback produce the same deviation.

## 4. Registration of new agents

### 4.1 Approval mechanism

The approval mechanism is the **only mechanism** that decides on onboarding new agents. There is no secret, no token and no certificate on the client side. The client sends a **Register**, and approval happens

- **manually** by the admin (showing IP, name and basic information with approve/reject), or
- **rule-based**, e.g. the client is a domain member and the reported SID matches the SID in AD.

### 4.2 Flow

1. The agent sends **Register** (it knows it has never been connected). The central server issues the **client GUID** (see 3.1) and returns it.
2. Optionally it sends its **inventory** right afterwards.
3. The central server also accepts and stores inventory from **unapproved** clients, but returns **no control information** (no minimum version, no released updates) until the client is approved. Reason: only this way are the full inventory data (role, domain membership, SID) available for the approval decision, in particular the rule-based one.
4. After approval, ring and group assignment follows (see `04-central-server.md`).

Register and inventory are deliberately **separate message types**, so the logic is not implemented twice.

## 5. Message types

Whitelist depending on registration state. The list is a **first draft**; more types are expected.

| Message type | Unregistered / not approved | Registered |
|---|---|---|
| Register | yes | – |
| Inventory | yes | yes |
| Heartbeat / health check | yes | yes |
| Unregister | yes | yes |
| Action request (fetch queue) | no | yes |
| Action status update (progress) | no | yes |
| Agent update check | no | yes |

**Unregister also for unapproved clients:** if an installation is accidentally started on the wrong server and uninstalled again, the uninstallation triggers an unregister before the service stops. The central server marks the entry as deleted without the admin having to clean up.

Further foreseeable messages (from the functional requirements, detailed specification still open): update report (search result), install report (per update, see `05-agent-design.md`), live status, history sync, full resync, log upload.

## 6. Unique update identification

Updates are identified uniquely in communication (not only by KB number) to avoid ambiguity. Reports carry the update ID **with revision**.

## 7. Minimum version check

- On every connection the central server tells the agent the required **minimum version**.
- If the agent is below it, it requests and installs an update **on its own, immediately**. A separate block/flag mechanism is not needed.
- If the self-update fails repeatedly (failure loop on the old version), this must be made **visible and escalating** in the central server so the admin can intervene (analogous to the quality gate for group leases).

## 8. Remote actions (queue)

The following actions are planned:

- Restart the client service
- Immediate update search with report
- Restart the server
- Ask the agent to check in and look for an agent update
- Full resync of the inventory
- Later: log retrieval

Restart actions are subject to the operational rules in `02-guidelines.md` (never endanger operation without explicit configuration).

## 9. Evaluation by the central server

HResult error codes are translated into plain text **centrally in the central server**, not in the agent, so there are no language-dependent differences between servers.
