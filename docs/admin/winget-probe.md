# SUSyphos WinGet Probe (lab tool)

`susyphos-wingetprobe.exe` answers the questions of the WinGet lab test before WinGet becomes part of the agent
(planned for 0.9). It is **not** part of the agent, not installed by any package and not released; CI builds it on
every push to `main` as the artifact **susyphos-wingetprobe** (self-contained, x64, no .NET needed on the server).

The WinGet access itself lives in the library `src/SUSyphos.WinGet`, which the agent will use later; the probe is only
a command-line shell around it.

## What it finds out

| Question | Command |
| --- | --- |
| Does the WinGet COM API work in-proc as SYSTEM, without a signed-in user? | `info`, `search`, `install` |
| Is App Installer there, which version, for whom is it registered? | `info` |
| Can App Installer be installed unattended (online, offline)? | `bootstrap --online`, `bootstrap --offline <folder>` |
| Does the engine shipped with the probe work where App Installer cannot be installed (Server 2016)? | any command with `--engine bundled` |
| How much of the installed software does WinGet see, and how much could it update? | `inventory` |
| Duration, exit codes, restart request and logs of an install or upgrade | `install`, `upgrade` |
| Does a private REST source work (later: served by the central server)? | `--source <https url>` |

## Running it

Unpack the artifact into a folder on the server, e.g. `C:\Lab\WinGetProbe`. The agent runs as SYSTEM, so the tests
run as SYSTEM too, with PsExec from Sysinternals:

```bat
psexec -s -i cmd.exe
cd /d C:\Lab\WinGetProbe
susyphos-wingetprobe info
```

`info` and `inventory` only read. `bootstrap`, `install` and `upgrade` change the server: run them only on lab
machines.

### Commands

```text
susyphos-wingetprobe info                         [--engine installed|bundled]
susyphos-wingetprobe inventory                    [--engine installed|bundled]
susyphos-wingetprobe bootstrap --online
susyphos-wingetprobe bootstrap --offline <folder>
susyphos-wingetprobe search <text>                [--source <https url>] [--engine ...]
susyphos-wingetprobe install <id> [--version <v>] [--source <https url>] [--accept-agreements] [--engine ...]
susyphos-wingetprobe upgrade <id> [--version <v>] [--source <https url>] [--accept-agreements] [--engine ...]
susyphos-wingetprobe uninstall <id> [--source <https url>] [--engine ...]
susyphos-wingetprobe cache <id> [--version <v>] [--cache <folder>]
susyphos-wingetprobe serve --certificate <thumbprint> [--port <port>] [--cache <folder>]
```

Every command also takes `--out <folder>` (default: `probe-results` next to the exe).

- **Engine.** `installed` (default) loads the engine of the installed App Installer
  (`WindowsPackageManager.dll` from its WindowsApps folder, with the C++ runtime from the VCLibs framework package).
  `bundled` loads the engine shipped in `engine\` next to the exe (NuGet package
  Microsoft.WindowsPackageManager.InProcCom, same version as the WinGet COM projection). Only one engine per run.
- **Install and upgrade** are for the whole machine and silent. Package agreements are not accepted unless
  `--accept-agreements` is given; read the agreements of the package first (`winget show <id>` on a test machine).
  The probe never restarts the server; a restart request of the installer is only reported.
- **Private source.** `--source` adds the REST source under the name `susyphos` for the account (as `winget source add`
  would) and uses it for that command. It stays configured; `winget source remove susyphos` removes it. Only https.
- **Bootstrap online** installs the PowerShell module Microsoft.WinGet.Client from the PowerShell Gallery for all users
  and runs `Repair-WinGetPackageManager -AllUsers -Latest`. Needs internet access.
- **Bootstrap offline** provisions App Installer from a folder with the files of a
  [winget-cli release](https://github.com/microsoft/winget-cli/releases): the `.msixbundle`, the `_License1.xml` and
  the unpacked `DesktopAppInstaller_Dependencies.zip` (the x64 packages are taken). For the in-proc engine the files on
  disk are enough; nobody needs to sign in.
- **Cache** puts one package version into the installer cache (default `winget-cache` next to the exe): the manifests
  from the community repository (raw.githubusercontent.com/microsoft/winget-pkgs) and the installers for x64 and
  neutral (x86 only when there is no x64 one), each checked against the SHA-256 of the manifest and stored under that hash. Without `--version` the newest
  version of the community source is taken (needs the engine). A download that receives nothing for 60 s is broken off
  and tried again (three attempts); progress is shown every 10 s. Needs internet access; run it on any machine and copy
  the folder.
- **Serve** offers the cache as a private WinGet REST source (protocol 1.1.0) at `https://<name>:<port>/api/` and the
  installers at `https://<name>:<port>/files/<hash>.<ext>`; `<name>` is the DNS name of the certificate. The servers
  that use the source must trust the certificate (WinGet checks TLS like Windows does), e.g. one from the lab's AD CS;
  the self-signed certificate of the central server is not trusted by Windows on its own. Every cached file is checked
  against its hash at start; nothing outside the cache is served. Runs until Ctrl+C; every request is logged. There is no sign-in: whoever reaches the port can read the cached
  packages, as with the community source. Allow the port in the firewall of the serving machine.

## Results

Each run writes to the results folder:

- `<command>-<computer>-<yyyyMMdd-HHmmss>.json`: machine (version, build, Server Core, account, SYSTEM), the App
  Installer packages found, which engine was loaded from where or why not, and the data of the command (sources,
  inventory comparison, install result with status, installer exit code, restart request and duration).
- `probe-<computer>-<yyyyMMdd>.log`: the same as on the screen, one line per step.
- `winget-logs\`: the logs WinGet writes during install and upgrade.

Exit code 0 means the command succeeded, 1 that it failed (the JSON says why), 2 that the command line was wrong.
`info` succeeds even when WinGet is not usable; the reason is in the report.

Send the whole results folder back after each round.

## Test rounds

Matrix: Windows Server 2016, 2019 (central server), 2022, 2025 with desktop, and 2025 Server Core.

1. `info` and `inventory` on all five servers, with both engines.
2. `bootstrap --online`, then `info`; on a second server `bootstrap --offline`. On 2016 App Installer is expected to
   refuse (it needs build 17763); the report records the error.
3. Install and upgrade from the community source: `7zip.7zip`, `Notepad++.Notepad++` (once with Notepad++ open),
   `Microsoft.PowerShell`, `Microsoft.VCRedist.2015+.x64`. For an upgrade, install an older version first
   (`install <id> --version <older>`). Then the same with `--engine bundled`.
4. Private source on the central server (2019): fill the cache (`cache 7zip.7zip --version 24.08`, `cache 7zip.7zip`,
   `cache Notepad++.Notepad++`, `cache Microsoft.PowerShell`, `cache Microsoft.VCRedist.2015+.x64`), run
   `serve --certificate <thumbprint>`, then on another server `search 7zip --source https://<name>:7443/api/` and the
   installs and upgrades of round 3 with `--source`, once with internet access blocked for that server.

## Known so far

- App Installer ships the engine without the in-proc wrapper of the NuGet package; the probe initializes
  `WindowsPackageManager.dll` directly. Loading it from WindowsApps is refused for an ordinary user (error 5) and is
  expected to work as administrator or SYSTEM.
- The bundled engine needs no C++ runtime (statically linked); the one of App Installer needs `MSVCP140` and
  `CONCRT140`, which the probe takes from the VCLibs framework package unless the Visual C++ redistributable is
  installed.

## Round 1 (2026-11-08)

`info` and `inventory` as SYSTEM on Server 2016, 2019, 2022 and 2025 with desktop:

| Server | App Installer | Installed engine | Bundled engine | Inventory |
| --- | --- | --- | --- | --- |
| 2016 (14393) | not installed | – | not loadable (error 126) | – |
| 2019 (17763) | not installed | – | loads, sources listed | 20 of 20 matched, 11 known to WinGet |
| 2022 (20348) | not installed | – | loads, sources listed | 5 of 5 matched, 3 known to WinGet |
| 2025 (26100) | 1.29.380, registered only for two administrators | loads as SYSTEM (C++ runtime from VCLibs) | loads | 7 of 7 matched, 4 known to WinGet |

- The bundled engine works as SYSTEM in-proc without App Installer on 2019 and later, so the agent does not depend on
  App Installer being installed or registered for anyone.
- On 2016 the engine cannot load because Windows lacks the ICU libraries `icuuc.dll` and `icuin.dll` (confirmed in
  round 2 by `engine.missingLibraries`); Windows has them only since version 1703. WinGet is therefore not usable on
  Server 2016.
- Every uninstall entry was matched to WinGet's installed list. Programs WinGet does not know from a source (VMware
  Tools, FortiClient, the SUSyphos packages) are listed, but cannot be updated through WinGet.
- A source can be behind the installed version (Edge 157 installed, 156 in the source); the report now takes WinGet's
  own "update available" instead of comparing version strings.

## Round 2 (2026-11-08)

- Install of `7zip.7zip` 24.08 on 2022 with the bundled engine as SYSTEM: status Ok, MSI exit code 0, no restart, 6 s
  (7.7 s with loading the engine). The installer ran silently for the machine (`ALLUSERS=1`, `REBOOT=ReallySuppress`).
- Upgrade to 26.06 right after: `DownloadError` 0x801901F8, i.e. HTTP 504 (Gateway Timeout) from the download server
  or a proxy on the way; the installer never started. Since then the probe copies WinGet's own diagnostic logs of the
  run (`WinGetCOM-*.log` from `%TEMP%\WinGet\defaultState`, for SYSTEM `C:\Windows\SystemTemp`) into `winget-logs\`.
- The retry with the diagnostic logs showed the cause: GitHub answered every release download with 504 at that time
  (also from outside the lab, also for the version installed before, while github.com and its API worked). WinGet
  tried Delivery Optimization (502) and WinINet (504) twice each. Many packages (7-Zip, Notepad++, PowerShell) are
  downloaded from GitHub releases, which speaks for the installer cache on the central server (round 4).
- Upgrade of `Microsoft.VCRedist.2015+.x64` 14.24 to 14.51 on 2022 with the bundled engine as SYSTEM: status Ok,
  exit code 0, no restart, 5.7 s. Download through Delivery Optimization from download.visualstudio.microsoft.com,
  hash verified; the Burn bundle installed its two MSIs and removed the old bundle. WinGet logs a failed font lookup
  and a missing resource file on every run without App Installer; both are harmless.

## Round 3 (2026-11-08)

On 2022 with the bundled engine as SYSTEM, after GitHub delivered again:

- Upgrade `7zip.7zip` 24.08 to 26.06: Ok, exit code 0, 3.2 s. The MSI major upgrade removed 24.08 first; MSI scheduled
  the deletion of a rollback file for the next restart (message 1903) and reported a restart already pending
  (`MsiSystemRebootPending = 1`), but WinGet reports no restart request, as the installer returned 0 and not 3010.
- Install `Notepad++.Notepad++` 8.9.9: Ok, exit code 0, 1.6 s (MSI from GitHub through Delivery Optimization).
- The downgrade test asked for 8.6.9, which the source does not offer (it lists 8.8.9 to 8.9.9); since then the error
  names the versions the source offers.
- Downgrade to 8.9.8 over 8.9.9: the Notepad++ MSI refuses it itself ("A newer version ... is already installed",
  1603, WinGet status InstallError 0x8A150049); the running program was not touched. A downgrade needs an uninstall
  first; the probe has an `uninstall` command since then.
