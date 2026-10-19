# Test Guide: Domain Lab

> A walk through every function in a small Active Directory lab, with what to check. Use test machines only: the agent installs updates and, if allowed, restarts servers.

## 1. Lab

| Machine | Role | Notes |
|---|---|---|
| DC01 | Domain controller (Windows Server 2016 or newer) | `lab.local` |
| UC01 | Central server, domain member | Windows Server 2019 or newer recommended |
| SRV01, SRV02 | Test servers, domain members | one 2016, one 2022 or 2025 if possible |
| SRV03 | Copy of SRV01 (optional) | for the clone test |
| OLD01 | Windows Server 2012 R2 computer account (optional, may be offline) | for the AD coverage test |

Snapshots before the first installation make it easy to repeat updates.

## 2. Central server

1. Optional: enroll a TLS certificate for `uc01.lab.local` from the lab CA into `LocalMachine\My`. Without one the central server creates its own self-signed certificate (browsers warn; agents do not care).
2. Install: `msiexec /i UpdateCoordinator.Server.msi CERTTHUMBPRINT=<thumbprint> /qn /l*v server.log`, or without `CERTTHUMBPRINT` for the own certificate. The own certificate needs version 0.2.0 or newer; 0.1.0 still needs `CERTTHUMBPRINT`.
3. Check: service `UpdateCoordinator.Server` runs; `https://uc01.lab.local:8443/health` answers; firewall rule "UpdateCoordinator Central Server (HTTPS)" exists.
4. Open `https://uc01.lab.local:8443/setup`, enter the token from `%ProgramData%\UpdateCoordinator\Server\setup-token.txt` (readable by administrators only) and create the first administrator. The token file disappears.
5. Administration → Users: create an Operator and a Reader account for the role tests.

## 3. Agents

1. Install on SRV01 manually: `msiexec /i UpdateCoordinator.Agent.msi SERVERURL=https://uc01.lab.local:8443/ /qn /l*v agent.log`
2. Install on SRV02 by group policy: software installation with the MSI, plus the registry value `HKLM\SOFTWARE\Policies\UpdateCoordinator\Agent\CentralServerUrl` (string) set by GPO preferences.
3. Check: service `UpdateCoordinator.Agent` runs; `%ProgramData%\UpdateCoordinator\Agent\Logs\agent-*.log` shows "Registered at the central server"; the servers appear under Pending approvals with host name, domain and roles (DC roles appear only on domain controllers).

## 4. Approval

- **Manual:** approve SRV01 under Pending approvals; the audit log shows the decision.
- **Rule-based:** set `Approval:AutoApproveDomains` to `lab.local` in `appsettings.json` of the central server (or as environment variable `Approval__AutoApproveDomains__0`) and restart the service. SRV02 is approved within a minute (enabled computer account in AD, source IP matches DNS). Disable the computer account of a third server and check that it stays pending.

## 5. Rings, groups and windows

- Assignment rules: put SRV01 into Pilot by host name pattern, SRV02 into Test.
- Group "Lab" with a maintenance window that is open now (e.g. the current weekday all day), both servers manually in it.
- Server detail shows ring, groups, effective windows, time zone and the next window.

## 6. Updates and rollout

1. Server detail → Remote actions → "Search for updates now" on both servers; the action turns Succeeded, and Missing updates fills.
2. Updates page: updates start in Pilot automatically (default start rule). SRV01 installs inside the window; the history shows the result, the ring page the rates.
3. Release rules: set Pilot → Test to 0 days minimum wait to speed up; after the next evaluation (1 minute) Test is released, SRV02 installs.
4. With the default manual gate Test → Production: the update waits under Pending approvals; approve it there or on the update page.
5. Force a failure (e.g. stop the Windows Update service on SRV02 during installation or fill the disk) and check that the alarm appears and nothing rolls on.

## 7. Reboots

1. Default policy is "Never": a server with a pending reboot only shows "Reboot pending".
2. Set the group "Lab" to "Reboot inside the maintenance window": the agent restarts the server with a 5-minute warning while the window is open at least 30 more minutes; history shows "Required, restarted".
3. Local veto: set `HKLM\SOFTWARE\Policies\UpdateCoordinator\Agent\AllowReboot` = `0` on SRV02; no restart happens, also not by the admin action "Restart server" (the action is rejected).

## 8. Coordinated group (leases)

Mark "Lab" as coordinated (25 %, at most 5: with two members only one at a time). Release an update to both servers in the same window: only one installs and restarts at a time; the other waits for the lease ("Waiting for a lease" in its log).

## 9. Remote actions and logs

Full resync, restart agent service (the service comes back within a minute), request agent logs (download from the action list), restart server (Admin, with confirmation).

## 10. AD coverage

The overview shows "AD coverage" within minutes of the central server start (it reads AD every 6 hours). Servers without agent appear on the AD coverage page; OLD01 appears under "not supported".

## 11. Clone detection (optional)

Copy SRV01 to SRV03 (without sysprep) and rename SRV03. While both run, SRV01 is blocked within a few heartbeats and appears under Pending approvals. Delete it on its detail page; both register anew.

## 12. Upgrade and uninstall

Install a newer MSI over both components without options: settings, identity and database are kept. Uninstall: services, program files, installer settings and the firewall rule disappear; data under `%ProgramData%\UpdateCoordinator` stays.

## 13. Certificate and backup (optional)

- Certificate renewal: with a CA certificate from an autoenrollment template, renew it in `certlm.msc` (Personal → Certificates → All Tasks → Renew Certificate with New Key). Within 15 minutes the audit log shows `Certificate.Renewed` and agents keep reporting.
- Backup and restore: download a backup (Administration → Backup), install the central server on a fresh VM with the same name, stop the service, run `UpdateCoordinator.Server.exe restore <zip>` as administrator, start the service. The dashboard accounts and servers are back, and agents report to the restored server without any reset (after the DNS name points to it).

## 14. What to report back

For each step: works / does not work, with the agent and server logs (`%ProgramData%\UpdateCoordinator\<component>\Logs`) and, for installer problems, the MSI log (`/l*v`).
