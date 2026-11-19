# Step 9: Set up and test backups

**What you're doing:** Scheduling automated daily backups of Rollcall's PostgreSQL database and encrypted configuration, copying them off-host, and verifying the backup status on the Health page.

## 1. Run a manual backup

On the Rollcall Linux host, run:

```bash
sudo bash /opt/rollcall/install.sh backup
```

Rollcall creates `/var/backups/rollcall/rollcall-backup-<timestamp>.tar.gz` (`0600 root:root`) containing a PostgreSQL custom-format dump, `/opt/rollcall/.env` (including `ROLLCALL_MASTER_KEY`), the rendered `docker-compose.yml` and `Caddyfile`, and any custom TLS certificates.

> **Warning:** The backup archive contains `ROLLCALL_MASTER_KEY`. Store copies in an encrypted, admin-only off-host location (for example a restricted S3 bucket, Azure Blob container, or backup appliance).

## 2. Schedule a daily backup timer

Create a daily systemd timer or cron job as `root`:

```bash
cat <<'EOF' | sudo tee /etc/cron.d/rollcall-backup
0 2 * * * root /opt/rollcall/install.sh backup >> /var/log/rollcall-backup.log 2>&1
EOF
sudo chmod 0644 /etc/cron.d/rollcall-backup
```

Add an off-host sync step (such as `rclone`, `restic`, or your enterprise backup agent) to copy `/var/backups/rollcall/` off the server and prune archives older than your retention window.

## Expected result

The manual backup prints `✓ Backup written to /var/backups/rollcall/rollcall-backup-<timestamp>.tar.gz.` with zero errors and no downtime.

## Check it worked

1. Verify the archive exists and is readable only by root:
   ```bash
   sudo ls -la /var/backups/rollcall/
   ```
2. Open **Admin › Health** in Rollcall and confirm **Last backup** shows today's UTC timestamp instead of a warning.

Next: [Step 10: Go-live checklist](10-go-live-checklist.md)
