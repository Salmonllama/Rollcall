# Updating Rollcall

Updates are automated through the host installer:

```bash
sudo bash /opt/rollcall/install.sh update
```

## What `install.sh update` does

1. Downloads the latest release's version-pinned `install.sh` and templates.
2. Takes an automatic backup before touching any container or database schema.
3. Pulls the new `rollcall-app` and `rollcall-worker` images from GHCR.
4. Restarts the stack so `Rollcall.Api` applies EF Core migrations automatically on startup, then waits for `/readyz` to report ready.

## Automatic rollback on failed updates

If the new release fails health checks or a database migration fails, `install.sh update` automatically restores the previous configuration, database dump, and container versions, brings the previous release back online, and prints the backup path.
