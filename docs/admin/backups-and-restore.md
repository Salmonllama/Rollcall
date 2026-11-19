# Backups and restore

Rollcall stores all state—portal users, role templates, workflow runs, audit log, and encrypted app certificates—in PostgreSQL, with secrets in `/opt/rollcall/.env`.

## Taking backups

```bash
sudo bash /opt/rollcall/install.sh backup
```

- Writes `/var/backups/rollcall/rollcall-backup-<timestamp>.tar.gz` (`0600 root:root`).
- Takes a consistent `pg_dump --format=custom` snapshot without stopping Rollcall.
- Updates `/var/lib/rollcall/last-backup.txt` so **Admin › Health** displays the most recent backup timestamp.

> **Warning:** Every backup archive includes `/opt/rollcall/.env` (which contains `ROLLCALL_MASTER_KEY`). Store backups in an encrypted, admin-only off-host location.

## Restoring a backup

```bash
sudo bash /opt/rollcall/install.sh restore /var/backups/rollcall/rollcall-backup-<timestamp>.tar.gz
```

1. Rollcall verifies the archive manifest and takes a safety backup of the current host first.
2. It restores `.env`, `docker-compose.yml`, `Caddyfile`, custom TLS certificates, and the PostgreSQL dump into a clean schema.
3. All services restart and wait until `/readyz` reports `{"db":"ok","migrations":"current","worker":"ok"}`.

## Migrating to a new server

1. Run `install.sh` on the new Linux host (see [Day-1 Step 2](../day-1/02-install.md)).
2. Copy your latest backup archive to the new host (`chmod 0600`).
3. Run `sudo bash /opt/rollcall/install.sh restore <archive>` and update DNS to point at the new host.
