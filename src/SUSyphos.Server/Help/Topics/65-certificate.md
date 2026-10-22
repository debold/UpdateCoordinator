# TLS certificate {#top}

The certificate the central server presents to agents and browsers over HTTPS. Administrators only.

Agents do not pin the certificate but the **trust anchor** of the central server, a key that stays the same for its whole life. Whenever the certificate changes, the trust anchor signs a binding for the new certificate, and every agent accepts the new certificate on its next contact. A certificate change therefore never cuts the agents off. Renewals of the certificate in the LocalMachine store (for example by autoenrollment) are picked up automatically; without a certificate of its own the central server creates and renews a self-signed one.

## In use {#in-use}

The certificate currently presented: **Subject**, **Issuer**, **Thumbprint**, **Valid until** and the **Binding serial** (increases with every signed binding; agents accept only increasing serials). The tag shows the days left; it turns amber below 30 days.

## Certificates {#available}

All certificates in the LocalMachine\My store of the central server, with their DNS names. Usable ones come first: they have a private key, are currently valid and are allowed for server authentication. The others are shown dimmed with **Not usable** and the reason: no private key, expired or not valid yet, not allowed for server authentication (enhanced key usage), a private key the central server service cannot read (permissions), or a key type it does not support. Browsers accept a certificate only for the names it contains, so choose one with the name you use to open the dashboard. **In use** marks the current one; **Use…** opens the [switch dialog](#switch).

To use a certificate from your CA, import it with its private key into LocalMachine\My on the central server; it then appears here.

## Switch the certificate {#switch}

The dialog names the certificate. Tick **Switch the central server to this certificate now** to confirm, then **Switch certificate**: the central server presents it at once, signs a new binding, and agents accept it on their next contact. The thumbprint is stored in the registry, so it survives restarts and upgrades. **Cancel** closes the dialog. The switch is recorded in the [audit log](help/audit-log).
