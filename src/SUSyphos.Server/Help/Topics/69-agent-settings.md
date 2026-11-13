# Agent settings {#top}

Settings of the agents themselves, set once for all agents here; a group or a single server can set other values (their "Agent settings" section). Empty fields keep the agent's own value (its configuration file, shown as "agent default"). Agents apply changes with their next heartbeat and keep them, so they also apply after a restart before the central server is reached. Changes of logging or process priority restart the agent service once. Values in the registry of a server (installer or policy key) win over these settings. What an agent actually applies, and where each value comes from, shows on its server page (System, "Agent settings", Show). Administrators only.

![The agent settings page: one row per setting, dashed fields keep the agent default](help-images/agent-settings.png)

Which value applies: a value set on the server wins; then its groups (if several groups set a setting, the lowest number wins, for other settings the group first by name); then the value set here; else the agent's own value.

- **Save agent settings** stores all fields at once; **Discard changes** shows the stored values again. A field outside its range is refused with a note and the entered values stay in the fields.

## Intervals {#intervals}

- **Heartbeat (seconds)**: how often the agent contacts the central server, 5 to 600. Short intervals make the dashboard react faster and cost a little load on the central server.
- **Update search (hours)**: how often the agent searches Windows Update, 1 to 24.
- **Inventory (hours)**: how often the agent collects its inventory, 1 to 24 (it is sent only when it changed or after a day).
- **Longest wait while the central server is unreachable (minutes)**: the agent waits longer after each failed contact, up to this, 1 to 60.

## Logging {#logging}

- **Log level**: Warning (only problems), Information (default) or Debug (much more detail, for troubleshooting).
- **Log files kept**: how many daily log files the agent keeps, 1 to 365.

## Resources {#resources}

- **Memory limit (agent restarts above it) (MB)**: when the agent uses more private memory after a full cleanup, it restarts its service, 64 to 4096.
- **Below-normal process priority**: on (default) keeps the agent from competing with the server's own work.

## Pre-flight check {#pre-flight}

- **Free space required on the system drive (MB)**: below it the agent neither searches nor installs, 512 to 1000000.

## Update search {#update-search}

- **Include drivers**: off leaves drivers out of the search, so only software updates are reported and installed.

## Restart notice {#restart-notice}

- **Warning before a restart (minutes)**: how long signed-in users are warned before the agent restarts the server, 1 to 30.
- **Message to signed-in users**: the text of that warning, at most 200 characters, no quotes.
- **Trust the central server's own certificate for WinGet** (section Software): off by default. WinGet checks the certificate of the WinGet source of the central server like Windows does, not through the trust anchor. With a certificate of a certification authority the servers trust (for example from AD CS) nothing is needed. With the central server's own self-signed certificate, switched on, the agent adds exactly that certificate (the one it verified through its trust anchor) to the trusted root certificates of the server; it never adds a certificate of a CA. When the certificate changes, the setting is switched off or the agent is uninstalled, the agent removes what it added. While the central server uses a self-signed certificate and this setting is off, the overview and the software pages show a banner **Certificate** (see [The certificate of the central server](help/software#certificate)).

## Windows Update {#windows-update}

The box **Windows Update** (moved here from the maintenance windows page) sets how the agent sets up Windows Update on every server; groups can override it on their [page](help/group-detail#settings). **Save** stores both settings; agents get them with their next heartbeat.

### Windows Automatic Updates {#automatic-updates}

Windows can install updates and restart on its own (Automatic Updates), outside rings and maintenance windows. The setting **Windows Automatic Updates** decides what the agent does about it:

- **Leave unchanged, only warn**: the agent does not touch the setting; servers on which Windows may install by itself get a warning. That includes Windows Server without any setting: its Update Orchestrator installs updates after its scheduled scan.
- **Keep Windows from installing on its own**: on servers where no group policy controls Automatic Updates, the agent turns them off (NoAutoUpdate = 1, as Azure Update Manager does for manual patching). Windows then neither downloads nor installs nor offers updates by itself; SUSyphos keeps searching and installing through the Windows Update API. The agent removes its value when this is switched off again or the agent is uninstalled. A group policy always wins.

Windows Update for Business deferrals set by group policy ("Select when Quality Updates are received") hold updates back for the deferral days, also from the agent's search; the pre-flight check warns about them.

Groups can override the default; "leave unchanged" in one group wins. 

### Other Microsoft products {#microsoft-update}

- **Windows Update only**: updates for Windows and its components.
- **Microsoft Update**: the agent also registers the Microsoft Update service (like "Receive updates for other Microsoft products" in Windows) and searches and installs through it, so SQL Server, Office and other Microsoft products are covered, also on servers that otherwise use WSUS. When switched off, the agent removes only a registration it made itself.

Groups can override the default; "Windows Update only" in one group wins.
