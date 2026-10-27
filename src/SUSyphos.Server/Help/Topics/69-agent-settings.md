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
