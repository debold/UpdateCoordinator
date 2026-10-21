# Backup {#top}

Backs up the database and the trust anchor in one archive, encrypted with a password you choose. With archive and password, a central server can be restored on new hardware and every agent keeps trusting it. Without the trust anchor all agents would have to be reset. Administrators only.

Keep archive and password apart. Passwords of notifications and of the scheduled backup itself are not part of the archive.

## Download a backup {#download}

Creates an archive now and downloads it to your browser.

- **Backup password** and **Repeat password**: the password that encrypts the archive, at least 16 characters. It is not stored anywhere: without it the archive cannot be restored.
- **Create and download** builds a consistent copy of the database (servers, history, rules, accounts, audit log) and the trust anchor and downloads the encrypted archive. The download is recorded in the [audit log](help/audit-log).

## Scheduled backup {#scheduled}

**Create a backup every day** switches the daily backup on; the fields are only editable while it is ticked. The tag shows whether it is on or off.

- **Folder**: a local folder or a share, for example `D:\Backup\SUSyphos` or `\\fileserver\backup\susyphos`. The service runs as LocalSystem; for a share, give the computer account of the central server write access.
- **Time (server time)**: when the backup runs each day, in the time zone of the central server.
- **Keep**: how many scheduled archives to keep; older ones are deleted.
- **Backup password** and **Repeat password**: the password for the scheduled archives, at least 16 characters. It is stored protected on this server; once stored, leaving the fields empty keeps it. Keep a copy elsewhere, because without it an archive cannot be restored.

**Save** stores the schedule; **Run now** creates a scheduled backup immediately (only while it is switched on). "Last run" shows the time and file name, or the error of a failed run. A failed scheduled backup also triggers a [notification](help/notifications).

## Restore {#restore}

Restoring happens on the command line of the central server, not in the dashboard:

1. Install the central server (same or newer version) and stop its service.
2. Run as administrator in the installation folder: `SUSyphos.Server.exe restore <backup.zip>`.
3. Enter the backup password when asked (or set it in the environment variable `SUSYPHOS_BACKUP_PASSWORD`). The archive is checked first; only then are the database and trust anchor replaced.
4. Start the service.
