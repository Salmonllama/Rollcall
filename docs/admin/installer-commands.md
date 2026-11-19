# Installer commands

The installer copies itself to `/opt/rollcall/install.sh`. Use that copy to check, update, back up, restore, and remove Rollcall. Every command needs `sudo`, and `--help` after any command shows its options.

```bash
sudo bash /opt/rollcall/install.sh help
```

## Check the services

```bash
sudo bash /opt/rollcall/install.sh status
```

Shows each service's state and health, then whether Rollcall is ready.

```bash
sudo bash /opt/rollcall/install.sh logs worker --follow
```

Shows the last 200 log lines. Name a service (`proxy`, `app`, `worker`, or `db`) to see only that one, and add `--follow` to keep watching.

## Back up

```bash
sudo bash /opt/rollcall/install.sh backup
```

Writes `rollcall-backup-<timestamp>.tar.gz` to `/var/backups/rollcall`. Use `--output <dir>` to write it somewhere else. The archive holds a full database dump, `.env`, the rendered configuration, and your own certificate files if you supplied them. Rollcall stays available during a backup.

> **Warning:** the archive contains `ROLLCALL_MASTER_KEY`. Store it somewhere only admins can read, and copy it off the server so a lost server doesn't take your backups with it.

## Restore

```bash
sudo bash /opt/rollcall/install.sh restore /var/backups/rollcall/rollcall-backup-20261026T215308Z.tar.gz
```

Replaces all Rollcall data with the backup's. Rollcall is unavailable for a minute or two. Before it changes anything, the restore saves the current state as a new backup and prints its path, so you can go back.

To move Rollcall to a new server, install Rollcall there, copy the archive across, and restore it. The restored server uses the hostname and certificate settings from the backup, so point the DNS record at the new server.

> **Danger:** everything changed after the backup was taken is lost.

## Update

```bash
sudo bash /opt/rollcall/install.sh update
```

Downloads the latest installer, takes a backup, then starts the new version, which applies any database changes. Rollcall is unavailable for a minute or two.

If the new version doesn't start, for example because a database migration failed, the update puts the previous version and its database back, starts it again, and prints the backup path. Nothing is lost. Send the output of `install.sh logs app` with your bug report.

## Uninstall

```bash
sudo bash /opt/rollcall/install.sh uninstall
```

Removes the Rollcall containers. The database, secrets, and configuration stay, so `sudo bash /opt/rollcall/install.sh install` starts Rollcall again with the same data.

```bash
sudo bash /opt/rollcall/install.sh uninstall --purge
```

Also deletes the database, the secrets, and `/opt/rollcall`. Backups in `/var/backups/rollcall` are kept.

> **Danger:** `--purge` can't be undone. Take a backup first if you might need the data.

`update`, `restore`, and `uninstall` ask you to type `yes` first. Add `--yes` to skip the question in scripts.
