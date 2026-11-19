<p align="center">
  <img src="docs/brand/rollcall-logo.svg" alt="Rollcall" width="320">
</p>

Rollcall is a free, open-source, self-hosted portal that turns Microsoft 365 onboarding and offboarding into a few reviewed clicks. Small IT teams manage groups, licenses, Conditional Access, user attributes, MFA, and hybrid on-prem Active Directory from one sign-in, installed with a single command.

Every change is previewed before it runs, sensitive changes can require a second approver, and everything is recorded in a tamper-evident audit log. Rollcall connects to Microsoft 365 with an Entra ID app registration and an RSA 3072 certificate only: no client secrets and no stored admin passwords.

## Features (v1.0)

- **One-command self-hosted install:** Linux host installer (`install/install.sh`) with automatic HTTPS (Let's Encrypt, custom cert, or internal CA), backup, restore, automatic-rollback updates, `admin-reset`, and `rotate-master-key`.
- **Preview → Approve → Run workflow engine:** Frozen SHA-256 plans, live drift detection (`Needs attention`), four-eyes approvals (self-approval blocked by default), step-level retry, and an append-only SHA-256 hash-chained audit log with CSV export.
- **User & cloud management:** Live directory search, single & bulk attribute edits, Entra security/M365 groups (`$batch`) and Exchange Online distribution lists (pooled runner), license & service-plan management, MFA methods, single-view Temporary Access Passes (RAM-only), session revocation, and password resets.
- **Role templates, onboarding & offboarding:** Live-validated versioned templates, collision blocking, two-phase scheduled activation, customizable Welcome Pack PDF (with optional Rollcall branding toggle), safety-ordered offboarding (mailbox converted to shared strictly *before* license removal), timed OneDrive handover reversal, and one-click user restore.
- **Conditional Access safety rails (Admin-only):** Patches strictly `conditions.users`, hard-blocks break-glass exclusion removal, requires typed policy confirmation + second approval, and supports timed temporary exclusions.
- **Hybrid Active Directory agent:** Outbound-only Windows service (`Rollcall.Agent`) running under a gMSA with ECDSA P-256 JWT auth, dual-layer OU allowlist enforcement, and automatic hybrid routing.

## Quick start

Follow the 10-step [Day-1 deployment guide](docs/day-1/README.md) from an empty Linux server to your first test onboarding and offboarding.

## Documentation

- [Day-1 deployment guide](docs/day-1/README.md)
- [Technician guides](docs/technician/dashboard.md)
- [Admin guides](docs/admin/team-and-roles.md) & [Installer commands](docs/admin/installer-commands.md)
- [Required permissions reference](docs/reference/permissions.md)
- [STRIDE threat model](docs/security/threat-model.md)
- [Troubleshooting](docs/troubleshooting.md)
- [v1.0.0 live-environment sign-off walkthrough](docs/release/v1.0.0-walkthrough.md)
- [Product spec](docs/product-spec.md) · [Implementation plan](docs/implementation-plan.md) · [Brand guide](docs/brand-guide.md) · [ADRs](docs/adr/)

## Contributing

Contributions are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md) and [docs/contributing/development.md](docs/contributing/development.md) for how to build, test, and sign off your commits, and follow the [code of conduct](CODE_OF_CONDUCT.md). To report a security issue privately, see [SECURITY.md](SECURITY.md).

## License

Rollcall is licensed under the [GNU General Public License v3.0 or later](LICENSE).

---

*Rollcall is an independent open-source project and is not affiliated with or endorsed by Microsoft. Microsoft 365, Microsoft Entra, and Exchange Online are trademarks of the Microsoft group of companies.*
