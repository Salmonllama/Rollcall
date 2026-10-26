# Changelog

All notable changes to Rollcall are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and Rollcall uses [semantic versioning](https://semver.org/).

## Unreleased

### Added

- Repository foundations (milestone M0): .NET 10 solution with the project layout from the implementation plan, React and TypeScript web app, analyzers that treat warnings as errors, and CI for build, tests, lint, license compliance, SPDX headers, and container images.
- GPLv3 license, contributing guide with DCO sign-off, code of conduct, and security policy.
- ADR 0001 recording the v1 technology decisions.
- ADR 0002: the app certificate is RSA 3072, Exchange Online runs app-only on Linux with an in-memory certificate, and the worker fetches a pinned, hash-verified `ExchangeOnlineManagement` 3.10.1 at runtime instead of bundling it. The spike scripts that measured this are in `spikes/`.
