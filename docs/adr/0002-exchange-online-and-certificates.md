# 0002: Exchange Online on Linux and the app certificate algorithm

- Status: Proposed. Waiting for spike results from a test tenant.
- Date: Oct 22, 2026

## Context

Rollcall must convert mailboxes to shared and manage distribution lists, forwarding, auto-replies, and address list visibility. Microsoft Graph doesn't cover these, so the worker runs the `ExchangeOnlineManagement` PowerShell module app-only with a certificate ([ADR 0001](0001-technology-decisions.md)). Three things are unproven:

1. **S1:** that `Connect-ExchangeOnline -Certificate` works in a Linux container on .NET 10 with the certificate loaded only in memory, and what a session costs in start time and memory.
2. **Risk R1:** the module is Microsoft-licensed, so bundling it in a published image may conflict with the rule that every bundled component is GPLv3-compatible. The preferred approach is to download a pinned, checksum-verified version from the PowerShell Gallery when the worker first starts.
3. **S2:** whether the app certificate can be ECDSA P-256 (smaller, faster) or must be RSA 3072, for both Graph client assertions and Exchange Online app-only.

The scripts in [`spikes/`](../../spikes/) measure all three. They need a test tenant, so the results below are filled in by a maintainer.

## Results

To be recorded from `spikes/` runs:

| Measure | RSA 3072 | ECDSA P-256 |
| --- | --- | --- |
| Certificate accepted by the app registration upload | y | y |
| Graph app-only token (client assertion) | y | y |
| `Connect-ExchangeOnline` succeeds | n | y |
| Session start time (s) | 2.1 | n/a |
| Worker memory after connect (MB) | 344 | n/a |
| Memory after `Get-Mailbox` and `Set-Mailbox -Type Shared` (MB) | 368 | n/a |
| Time until `RecipientTypeDetails = SharedMailbox` (s) | 16.6 | n/a |

- `ExchangeOnlineManagement` version tested: 3.10.1
- Module download time and module tree SHA-256: 18B5D4B687DD0CC8BDA65310B344194F097BE0DD7441934D0C127344DFF77A34
- Certificate loaded with `EphemeralKeySet`, no key file written to disk: yes

## Decision

To be written from the results. The expected decision, if the spikes succeed, is:

- The worker obtains `ExchangeOnlineManagement` from the PowerShell Gallery at first start, pinned to the version above and verified against the recorded hash, and caches it in a volume. The module is not redistributed in Rollcall images.
- The worker hosts PowerShell in-process (`Microsoft.PowerShell.SDK`) and passes the certificate as an in-memory `X509Certificate2`.
- The app certificate algorithm is ECDSA P-256 if both Graph and Exchange Online accept it, otherwise RSA 3072.
- Certificates must stay RSA 3072. ECDSA not accepted by ExchangeOnline Module.

## Consequences

To be written with the decision. M5 (Exchange runner) and M2 (certificate generation) depend on this ADR.
