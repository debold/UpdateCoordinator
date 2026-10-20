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

There is **no pre-shared secret, no token and no certificate on the client side** for registration. Onboarding of new agents is decided exclusively by the **approval mechanism** (see section 4.1). Communication runs over HTTPS.

### 3.1 Client identity (client GUID and client secret)

- On registration the **central server** issues a random **client GUID** (v4) and a random **client secret** (256 bit, Base64url) and returns both in the register response. Decided on 2026-10-18: the GUID alone is not a secret, so without the client secret anyone who learns a GUID could impersonate that server.
- The agent stores both locally (protected with DPAPI for the service account) and sends them with every further message as **HTTP Basic authentication** (`clientGuid:clientSecret`) over HTTPS.
- The central server stores only a SHA-256 hash of the secret and compares in constant time. Unknown GUID and wrong secret produce the same answer (`401`), so GUIDs cannot be probed.
- If the central server answers `401` (client deleted or database restored without it), the agent discards its identity and registers again. The new entry needs a new approval, so no trust is gained this way.
- The GUID identifies the client uniquely **across renames and domain rejoins**. Host name and domain are only attributes: changes are logged but do not trigger a new approval.
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
- **Local override (registry):** an administrator of the server can force the agent to discard the stored trust and pin again on the next contact (trust on first use). Mechanism: the string value `TrustResetToken` under `HKLM\SOFTWARE\Policies\<Product>\Agent` (or the installer key, see 3.3). The agent performs the reset exactly once whenever the value differs from the last processed one (evaluated at service start), so it also works with GPOs that rewrite values periodically. The reset is written to the Windows event log and reported to the central server with the next heartbeat (`TrustReset: true`), which logs it (later: audit log and dashboard). Only local administrators, who control the agent anyway, can write this key.
- **Limit:** if the anchor is lost (without backup) or compromised, central re-trust is impossible in principle, because an attacker could otherwise do the same. Only the local override or a reinstallation of the agent remains. Until then the agent sends nothing, reports "central server not trusted" locally and keeps retrying with backoff.
- **First contact:** trust on first use means an attacker who controls the network at the very first contact could get his own anchor pinned. Mitigations: install agents in a trusted network, and the anchor fingerprint in the download file name (see 3.3).

**Implementation (2026-10-18):**

| Aspect | Implementation |
|---|---|
| Anchor key | ECDSA P-256, created on first start, stored DPAPI-protected (service account) in `Trust:AnchorPath`. An existing but unreadable file stops the server instead of being replaced, because a new anchor would lock out every agent. |
| Signed data | ASCII `SUSyphos.TrustBinding.v1|<serial>|<hex SHA-256 of the certificate SPKI>`, signature SHA-256 (IEEE P1363). Defined once in `TrustBindingFormat` (contracts). |
| Binding endpoint | `GET /api/v1/trust/binding`, anonymous, returns anchor public key (SPKI), certificate key hash, serial and signature. |
| Certificate approval | An administrator approves a certificate by selecting it in the dashboard (or with `CERTTHUMBPRINT` at installation). Without a selection the central server creates its own self-signed certificate (180 days, rollover 30 days before expiry with a new key). Two changes count as approved without an administrator, because they cannot be introduced from outside: a renewal of the approved certificate (same subject and issuer, at least the same DNS names, later expiry, e.g. by autoenrollment) and the rollover of the own self-signed certificate. A foreign self-signed certificate is never taken over automatically. Every change signs a new binding (next serial) and is audited. Kestrel selects the certificate per handshake, so changes need no restart. Decided by Marc on 2026-10-19 (180 days, configurable down to 7 days). |
| Agent check | All messages use a TLS handler that accepts only the certificate key of the accepted binding, independent of CA chain and host name. Before the first message, after a reset and after a failed handshake, the agent fetches the binding over a separate connection that sends nothing else, verifies it and stores the result DPAPI-protected (`trust.dat`). |

### 3.3 Address of the central server

The installer binary contains no environment-specific data, so it stays signed and identical for every customer. The address of the central server is supplied at installation time and kept in the registry. Decided on 2026-10-18:

1. **Registry is the only runtime source.** The agent reads `CentralServerUrl` (string) from `HKLM\SOFTWARE\Policies\<Product>\Agent` (group policy) and `HKLM\SOFTWARE\<Product>\Agent` (installer). The policy value wins over the installer value and over every other configuration source, so the address can be changed centrally at any time. Only plain string values up to 2048 characters are accepted; the value must be an absolute `https://` URI.
2. **Installer option** `SERVERURL=https://…` (msiexec command line, transform, Intune, SCCM, Ansible) writes the installer value.
3. **Download from the central server (implemented 2026-10-20):** the central server offers the unchanged, signed MSI for download with the address encoded in the file name (e.g. `SUSyphos-Agent_updates.example.local_8443.msi`). A custom action reads the file name via the MSI property `OriginalDatabase` and writes the installer value. Because the file content is not modified, the Authenticode signature stays valid. If the file is renamed, the address is missing and option 1 or 2 applies (or the installer asks for it). The parser must tolerate the suffix browsers add to a repeated download (e.g. `… (1).msi`), and a file name must never make the installation fail: an unparsable name only means "no address from the file name".

**Trust anchor fingerprint in the file name (decided by Marc on 2026-10-20, implemented):** the file name also carries the first 16 bytes of the SHA-256 of the anchor public key (SubjectPublicKeyInfo) as 32 hex characters, e.g. `SUSyphos.Agent-0.4.0_uc01.example.local_8443_<fingerprint>.msi`. The installer writes it to `TrustAnchorFingerprint` in the installer key; when pinning, the agent accepts only an anchor with this fingerprint (`FingerprintMismatch` otherwise). This removes the trust-on-first-use weakness for downloaded installers without making the installer environment-specific. The fingerprint is only written together with the address from the same file name. Format and parser live in `AgentInstallerFileName` (contracts), shared with the installer custom action (.NET Framework 4.6.2, part of Windows Server 2016). The interactive installer asks for the address, pre-filled from command line, file name or installed value.

**Forged success reports:** are addressed by approval and GUID identification (only approved clients are accepted). A content plausibility check (e.g. build number against the expected update) was deliberately rejected, because legitimate cases such as a manual rollback produce the same deviation.

## 4. Registration of new agents

### 4.1 Approval mechanism

The approval mechanism is the **only mechanism** that decides on onboarding new agents. There is no secret, no token and no certificate on the client side. The client sends a **Register**, and approval happens

- **manually** by the admin (showing IP, name and basic information with approve/reject), or
- **rule-based**, e.g. the client is a domain member: the central server itself looks up the computer object in AD (by host name in the reported domain) and checks that the request comes from the IP address registered in DNS for it. Self-reported values such as a SID are not trusted; the agent therefore does not report a SID at all (decided 2026-10-18).

Implemented 2026-10-18: manual decisions on the dashboard page "Pending approvals" (role Operator); rule-based approval for the DNS domains in `Approval:AutoApproveDomains`, checked periodically by the central server (enabled computer account `HOST$` via LDAP with signing and sealing, source IP address among the DNS addresses of `host.domain`). Since 2026-10-20 administrators add and remove domains on the page Approvals ("Automatic approval"); domains from the configuration stay and are marked.

### 4.2 Flow

1. The agent sends **Register** (it knows it has never been connected). The central server issues the **client GUID and client secret** (see 3.1) and returns them. The client starts in state `Pending`.
2. Optionally it sends its **inventory** right afterwards.
3. The central server also accepts and stores inventory from **unapproved** clients, but returns **no control information** (no minimum version, no released updates) until the client is approved. Reason: only this way are the full inventory data (role, domain membership) available for the approval decision, in particular the rule-based one.
4. After approval, ring and group assignment follows (see `04-central-server.md`).

Register and inventory are deliberately **separate message types**, so the logic is not implemented twice.

## 5. Message types

Whitelist depending on registration state. The list is a **first draft**; more types are expected. "Unregistered / not approved" means no credentials yet or state `Pending`; "Registered" means state `Approved`. Clients in state `Rejected`, `Blocked` or `Deleted` may send no message at all (`403`). Register is only accepted without valid credentials.

| Message type | Unregistered / not approved | Registered |
|---|---|---|
| Register | yes | – |
| Inventory | yes | yes |
| Heartbeat / health check | yes | yes |
| Unregister | yes | yes |
| Update report (search result) | no | yes |
| Release request (fetch released updates) | no | yes |
| Install report (installation result) | no | yes |
| Action request (fetch queue) | no | yes |
| Action status update (progress) | no | yes |
| Agent update check | no | yes |

**Unregister also for unapproved clients:** if an installation is accidentally started on the wrong server and uninstalled again, the uninstallation triggers an unregister before the service stops. The central server marks the entry as deleted without the admin having to clean up.

### 5.1 Implemented endpoints (API version 1)

| Message | Endpoint | Authentication | Answers |
|---|---|---|---|
| Register | `POST /api/v1/agents/register` | none (rate limited per IP) | `200` client GUID + secret, `400` invalid input, `403` already registered, `429` rate limit |
| Heartbeat | `POST /api/v1/agents/heartbeat` | Basic (GUID + secret) | `200` registration state, `400` invalid input, `401` unknown credentials, `403` state not allowed |
| Inventory | `POST /api/v1/agents/inventory` | Basic (GUID + secret) | `204` stored, `400` invalid input, `401` unknown credentials, `403` state not allowed, `413` larger than 4 MB |
| Update report | `POST /api/v1/agents/updates` | Basic, approved only | `204` stored, `400` invalid input, `401`, `403` not approved, `413` larger than 4 MB |
| Releases | `GET /api/v1/agents/releases` | Basic, approved only | `200` release list with version, `401`, `403` not approved |
| Install report | `POST /api/v1/agents/installations` | Basic, approved only | `204` stored, `400` invalid input, `401`, `403` not approved |
| Actions | `GET /api/v1/agents/actions` | Basic, approved only | `200` up to 20 queued actions (marked as delivered), `401`, `403` not approved |
| Lease | `POST /api/v1/agents/lease` | Basic, approved only | `200` granted with end time, or not granted with reason |
| Lease release | `POST /api/v1/agents/lease/release` | Basic, approved only | `204` released, `404` no active lease |
| Action result | `POST /api/v1/agents/actions/{id}/result` | Basic, approved only | `204` stored, `400` invalid input, `401`, `403`, `404` unknown, not delivered or not this client's action |
| Trust binding | `GET /api/v1/trust/binding` | none (public, signed) | `200` binding (see 3.2), `503` no binding yet |

The inventory contains host name, domain, Windows time zone ID (kept only if the central server knows it; maintenance windows are in this time), operating system (product name, edition, display version, version with UBR), detected roles, the configured update source and up to 5,000 installed updates (update ID, revision, KB, title, classification). `installedUpdates: null` means "could not be read": the central server keeps the previous list. The heartbeat additionally carries the current OS version including UBR.

The protocol version is part of the route (`/api/v1/...`). An incompatible change introduces a new version, and the central server serves both during a transition period. All input is validated against the shared contracts (`SUSyphos.Contracts`); error answers use RFC 9457 problem details.

The update report carries the pre-flight checks (name, status `Ok`/`Warning`/`Failed`, detail), whether the search succeeded, the WUA error code if not, and up to 5,000 available updates (identity as in the inventory plus download size, reboot behavior and MSRC severity). Releases are delivered as a list of update ID, revision and earliest installation time with a version number; the heartbeat of an approved client announces the current version (`releasesVersion`), and the agent fetches the list only when it changed. The release list also carries the effective maintenance windows of the client (at most 14, each with weekdays, start and end in local server time); a change of the windows increases the version as well. The agent validates every window and rejects the whole list if one is invalid. Update reports and releases are accepted only from approved clients (decided 2026-10-18), so unapproved clients cannot fill the catalog through search results.

The install report describes one installation run: a run ID (a report buffered by the agent and sent twice is stored once), start and end, per update the WUA result code, error code and whether a reboot is needed, the OS version afterwards and the post-install health checks. The central server stores only results for updates released to that client, so a client cannot write arbitrary history. The heartbeat of an approved client also carries the global pause (`installationsPaused`). The agent reports in every heartbeat whether Windows shows a pending reboot (`rebootPending`) and its last boot time (`lastBootAt`; the central server ignores times in the future or before 2000). The release list carries the effective reboot policy (`Never` or `InsideMaintenanceWindow`); an unknown value makes the agent reject the list.

Further foreseeable messages (from the functional requirements, detailed specification still open): troubleshooting details for failed installations (event log excerpts, CBS.log, see `05-agent-design.md`), live status, history sync, full resync, log upload.

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
Implemented (2026-10-19): an Operator queues an action on the server detail page (audited; at most 10 open actions per server, approved servers only). The heartbeat answer carries the number of queued actions (`pendingActions`); when it is above zero the agent fetches them (`GET /api/v1/agents/actions`, which marks them as delivered), runs them in order and confirms each one (`Succeeded`, `Failed` or `Rejected` with a short detail of at most 512 characters; control characters are removed). An action that was never fetched expires after 24 hours, one without result one hour after delivery; a queued action can be cancelled until the agent fetches it. Implemented actions: search for updates now, full resync (inventory and search), upload of the agent logs (ZIP of the last 3 days, at most 10 MB, `POST /api/v1/agents/actions/{id}/logs` with `application/zip`; `413` above the limit, `415` other content types, `400` not a ZIP archive), restart of the server (Admin only with confirmation; ignores policy and window but never the local veto) and restart the agent service (the agent confirms first and then ends with exit code 1, so the service recovery settings restart it; the installer must configure restart on failure including non-crash failures). An agent that does not know an action rejects it.

## 9. Evaluation by the central server

HResult error codes are translated into plain text **centrally in the central server**, not in the agent, so there are no language-dependent differences between servers.
