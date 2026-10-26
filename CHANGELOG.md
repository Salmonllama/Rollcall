# Changelog

All notable changes to Rollcall are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and Rollcall uses [semantic versioning](https://semver.org/).

## Unreleased

### Added

- Health checks (milestone M1): the app answers `GET /healthz` once it has started and `GET /readyz` with the state of the database, migrations, and worker (`{"db":"ok","migrations":"current","worker":"ok"}`, or HTTP 503 with the failing part). The app applies database migrations on startup, and the worker writes a heartbeat every 15 seconds. Both images have a `healthcheck` command for container healthchecks.
- Compose and Caddy templates for the four-service stack (proxy, app, worker, db) with healthchecks. The database is on an internal network with no host ports. Caddy redirects HTTP to HTTPS and sets HSTS, a self-only Content Security Policy, `X-Content-Type-Options`, and `Referrer-Policy`.
- The app serves a placeholder page with the Rollcall logo and favicon until the setup wizard arrives.

- Repository foundations (milestone M0): .NET 10 solution with the project layout from the implementation plan, React and TypeScript web app, analyzers that treat warnings as errors, and CI for build, tests, lint, license compliance, SPDX headers, and container images.
- GPLv3 license, contributing guide with DCO sign-off, code of conduct, and security policy.
- ADR 0001 recording the v1 technology decisions.
- ADR 0002: the app certificate is RSA 3072, Exchange Online runs app-only on Linux with an in-memory certificate, and the worker fetches a pinned, hash-verified `ExchangeOnlineManagement` 3.10.1 at runtime instead of bundling it. The spike scripts that measured this are in `spikes/`.
