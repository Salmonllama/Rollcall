<p align="center">
  <img src="docs/brand/rollcall-logo.svg" alt="Rollcall" width="320">
</p>

Rollcall is a free, open-source, self-hosted portal that turns Microsoft 365 onboarding and offboarding into a few reviewed clicks. Small IT teams manage groups, licenses, Conditional Access, user attributes, MFA, and hybrid on-prem Active Directory from one sign-in, installed with a single command.

Every change is previewed before it runs, sensitive changes can require a second approver, and everything is recorded in an audit log. Rollcall connects to Microsoft 365 with an Entra ID app registration and a certificate only: no client secrets and no stored admin passwords.

## Status

Rollcall is in early development and is not ready to install. The repository currently holds the foundations (milestone M0): the solution layout, CI, and licensing checks. Progress follows the milestones in the [implementation plan](docs/implementation-plan.md).

## Documentation

- [Product spec](docs/product-spec.md): what Rollcall does and why.
- [Implementation plan](docs/implementation-plan.md): architecture, conventions, and milestones.
- [Brand and design guide](docs/brand-guide.md): visual identity, voice, and UI components.
- [Architecture decision records](docs/adr/)

## Contributing

Contributions are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md) for how to build, test, and sign off your commits, and follow the [code of conduct](CODE_OF_CONDUCT.md). To report a security issue privately, see [SECURITY.md](SECURITY.md).

## License

Rollcall is licensed under the [GNU General Public License v3.0 or later](LICENSE).

---

*Rollcall is an independent open-source project and is not affiliated with or endorsed by Microsoft. Microsoft 365, Microsoft Entra, and Exchange Online are trademarks of the Microsoft group of companies.*
