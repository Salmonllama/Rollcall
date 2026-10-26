# Changelog

All notable changes to Rollcall are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and Rollcall uses [semantic versioning](https://semver.org/).

## Unreleased

### Added

- Health checks (milestone M1): the app answers `GET /healthz` once it has started and `GET /readyz` with the state of the database, migrations, and worker (`{"db":"ok","migrations":"current","worker":"ok"}`, or HTTP 503 with the failing part). The app applies database migrations on startup, and the worker writes a heartbeat every 15 seconds. Both images have a `healthcheck` command for container healthchecks.
- `install/install.sh`, the one-command installer (milestone M1). It checks the OS and architecture, installs Docker Engine and the Compose plugin from Docker's repository if they're missing, and asks for the hostname and certificate type. Certificates can come from Let's Encrypt, your own certificate and key files, or Caddy's local CA (`--tls internal`) for test machines. It then generates secrets into `/opt/rollcall/.env` (mode 0600), renders the configuration, starts the stack, waits for `/readyz`, and prints the portal URL and setup token. Every question has a flag for unattended installs. Rerunning it on an installed host changes nothing and points to `update`. `status` and `logs` work too.
- Installer lifecycle commands. `backup` writes a mode-0600 archive (database dump, `.env`, rendered configuration, certificates) to `/var/backups/rollcall`. `restore <archive>` saves the current state first, then restores the backup. `update` runs the latest installer, takes a backup, applies the new version, and puts the previous version and its data back if the new version fails to start, for example after a failed migration. `uninstall` keeps data unless you add `--purge`; backups are always kept.
- Compose and Caddy templates for the four-service stack (proxy, app, worker, db) with healthchecks. The database is on an internal network with no host ports. Caddy redirects HTTP to HTTPS and sets HSTS, a self-only Content Security Policy, `X-Content-Type-Options`, and `Referrer-Policy`.
- The app serves a placeholder page with the Rollcall logo and favicon until the setup wizard arrives.

- Repository foundations (milestone M0): .NET 10 solution with the project layout from the implementation plan, React and TypeScript web app, analyzers that treat warnings as errors, and CI for build, tests, lint, license compliance, SPDX headers, and container images.
- GPLv3 license, contributing guide with DCO sign-off, code of conduct, and security policy.
- ADR 0001 recording the v1 technology decisions.
- ADR 0002: the app certificate is RSA 3072, Exchange Online runs app-only on Linux with an in-memory certificate, and the worker fetches a pinned, hash-verified `ExchangeOnlineManagement` 3.10.1 at runtime instead of bundling it. The spike scripts that measured this are in `spikes/`.
