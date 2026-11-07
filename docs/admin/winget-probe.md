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
4. Later: the same with a private source served by the central server and internet access blocked.

## Known so far

- App Installer ships the engine without the in-proc wrapper of the NuGet package; the probe initializes
  `WindowsPackageManager.dll` directly. Loading it from WindowsApps is refused for an ordinary user (error 5) and is
  expected to work as administrator or SYSTEM.
- The bundled engine needs no C++ runtime (statically linked); the one of App Installer needs `MSVCP140` and
  `CONCRT140`, which the probe takes from the VCLibs framework package unless the Visual C++ redistributable is
  installed.
