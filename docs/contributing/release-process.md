# Release process

Rollcall follows Semantic Versioning (`vMAJOR.MINOR.PATCH`).

## Before tagging a release

1. Verify `main` is green across all CI jobs (`.NET`, `web`, `licenses`, `headers`, `images`, `installer-unit`, `installer-e2e`, `installer-lifecycle`, `permissions-doc`, `security`).
2. Update `CHANGELOG.md` to move entries from `Unreleased` into the new version heading with the release date.
3. Complete the live-environment verification checklist (see [v1.0.0 walkthrough template](../release/v1.0.0-walkthrough.md)) against a live Microsoft 365 developer tenant and Windows Server AD lab.
4. Confirm `docs/reference/permissions.md` is up to date (`scripts/check-permissions-doc.sh`).

## Tagging & automated publishing

Push a signed or annotated `v*` tag on `main`:

```bash
git tag -a v1.0.0 -m "Rollcall v1.0.0"
git push origin v1.0.0
```

`.github/workflows/release.yml` automatically:

1. Builds and publishes multi-arch (`linux/amd64`, `linux/arm64`) `rollcall-app` and `rollcall-worker` images to GHCR with SPDX/CycloneDX SBOM attestations and Sigstore Cosign signatures.
2. Pins `ROLLCALL_VERSION` inside `install.sh`, bundles `docker-compose.yml.tmpl` and `Caddyfile.tmpl`, and generates `SHA256SUMS`.
3. Creates the GitHub Release with generated release notes and downloadable assets.
