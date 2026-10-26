# 0002: Exchange Online on Linux and the app certificate algorithm

- Status: Accepted
- Date: Oct 26, 2026

## Context

Rollcall must convert mailboxes to shared and manage distribution lists, forwarding, auto-replies, and address list visibility. Microsoft Graph doesn't cover these, so the worker runs the `ExchangeOnlineManagement` PowerShell module app-only with a certificate ([ADR 0001](0001-technology-decisions.md)). Three things were unproven:

1. **S1:** that `Connect-ExchangeOnline -Certificate` works in a Linux container on .NET 10 with the certificate loaded only in memory, and what a session costs in start time and memory.
2. **Risk R1:** the module is Microsoft-licensed, so bundling it in a published image may conflict with the rule that every bundled component is GPLv3-compatible. The preferred approach is to download a pinned, checksum-verified version from the PowerShell Gallery when the worker first starts.
3. **S2:** whether the app certificate can be ECDSA P-256 (smaller, faster) or must be RSA 3072, for both Graph client assertions and Exchange Online app-only.

The scripts in [`spikes/`](../../spikes/) measured all three against a test tenant on Oct 26, 2026.

## Results

| Measure | RSA 3072 | ECDSA P-256 |
| --- | --- | --- |
| Certificate accepted by the app registration upload | Yes | Yes |
| Graph app-only token (client assertion) | Yes | Yes |
| `Connect-ExchangeOnline` succeeds | Yes | No |
| Session start time (s) | 2.1 | n/a |
| Worker memory after connect (MB) | 344 | n/a |
| Memory after `Get-Mailbox` and `Set-Mailbox -Type Shared` (MB) | 368 | n/a |
| Time until `RecipientTypeDetails = SharedMailbox` (s) | 16.6 | n/a |

- `ExchangeOnlineManagement` version tested: 3.10.1
- Module tree SHA-256: `18B5D4B687DD0CC8BDA65310B344194F097BE0DD7441934D0C127344DFF77A34`
- Module download time: not measured
- Certificate loaded with `EphemeralKeySet`, no key file written to disk: yes

## Decision

- **The app certificate is RSA 3072.** Graph accepts ECDSA P-256, but `Connect-ExchangeOnline` doesn't, and mailbox actions are required in v1. Rollcall uses one certificate for Graph, Exchange Online, and technician sign-in, so it must be RSA 3072.
- **Exchange Online runs on Linux in the worker.** An in-memory certificate works: the worker passes an `X509Certificate2` loaded with `EphemeralKeySet` to `Connect-ExchangeOnline -Certificate`, and no key file is written to disk. The worker hosts PowerShell in-process through `Microsoft.PowerShell.SDK`.
- **The module is fetched, not bundled (risk R1).** On first start the worker downloads `ExchangeOnlineManagement` 3.10.1 from the PowerShell Gallery, verifies it against the module tree SHA-256 above, and caches it in a volume. Rollcall images never contain the module. Changing the pinned version means recording the new version and hash in an ADR update.

## Consequences

- **M2 (certificate generation):** the setup wizard generates RSA 3072 certificates only. This ADR settles the open choice in implementation plan section 4.2, so the ECDSA option there doesn't apply.
- **M5 (Exchange runner):** the session pool can budget about 2 seconds to start a session and about 370 MB of worker memory with one warm session, well inside the 1 GB limit in M5 criterion 7.
- **Worker startup needs the PowerShell Gallery.** The first worker start, and the first start after an update that changes the pinned version, needs outbound HTTPS to `www.powershellgallery.com`. The day-1 prerequisites page (written in M1) must list this host alongside the Microsoft endpoints. If the download or hash check fails, mailbox features report the problem on the health page instead of running with an unverified module.
- **A Microsoft change can break Exchange Online access.** Microsoft can retire a module version, or start accepting ECDSA. Bumping the pinned version, or revisiting the algorithm, needs an update to this ADR with new spike results.
