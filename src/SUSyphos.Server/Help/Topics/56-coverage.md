# AD coverage {#top}

Compares the server accounts in Active Directory with the registered agents, so you find servers that are not managed by SUSyphos yet. The central server reads the enabled computer accounts with a Windows Server operating system every 6 hours from its own domain, or from the domain configured under `Coverage:Domain`. The line under the title says when it last read them; if it never did, the central server is not a domain member and no domain is configured.

## Figures {#figures}

- **Supported servers with an agent**: share of the enabled server accounts with Windows Server 2016 or newer that have a registered agent, with the numbers.
- **Without agent**: supported server accounts for which no agent reports.
- **Not supported**: accounts with older Windows Server versions; SUSyphos does not support them and does not count them.

## Supported servers without agent {#missing}

The accounts behind "Without agent", with account name, DNS name, operating system and build from Active Directory. Install the agent on them ([Agents](help/agents#install)), or disable stale accounts in Active Directory.

## Not supported {#unsupported}

Server accounts with a Windows Server version older than 2016 (or an unknown build). They need another way to be patched or should be replaced.
