# M0 spikes S1 and S2

These scripts answer the two open questions from milestone M0 that need a real Microsoft 365 tenant. Their results go into [ADR 0002](../docs/adr/0002-exchange-online-and-certificates.md). Run them **only against a test tenant**, never production.

- **S1, Exchange Online on Linux:** can a Linux container on .NET 10 run `Connect-ExchangeOnline` with a certificate held only in memory, then `Get-Mailbox` and `Set-Mailbox -Type Shared`? How long does a session take to start, how much memory does it use, and can the module be fetched at runtime instead of bundled (risk R1)?
- **S2, certificate algorithm:** does an ECDSA P-256 certificate work for Graph client assertions and Exchange Online app-only, or must Rollcall use RSA 3072?

## What you need

- A test tenant and an account that can create app registrations and grant admin consent.
- A test mailbox you don't mind converting to shared and back (it needs a license, since it starts as a user mailbox).
- Docker on Linux or macOS.

## 1. Build the spike image and create certificates

```bash
cd spikes
docker build -t rollcall-spike .
docker run --rm -v "$PWD/out:/spike/out" rollcall-spike New-SpikeCertificates.ps1
```

Expected result: `out/` holds `rsa.cer`, `ecdsa.cer`, and their private keys as `*.pfx.b64`.

## 2. Create the test app registration

In the Entra admin center for the test tenant:

1. Create an app registration named `Rollcall spike`, single tenant.
2. Under **Certificates & secrets**, upload both `out/rsa.cer` and `out/ecdsa.cer`. Note which upload fails, if one does: that is an S2 result.
3. Under **API permissions**, add Microsoft Graph application permission `Organization.Read.All` and Office 365 Exchange Online application permission `Exchange.ManageAsApp`, then grant admin consent.
4. Under **Roles and administrators**, assign the **Exchange Administrator** role to the app's service principal.

Note the tenant ID, the client ID, and the tenant's `*.onmicrosoft.com` domain.

## 3. S2: Graph client assertion with each certificate

```bash
docker run --rm -v "$PWD/out:/spike/out" rollcall-spike Test-GraphClientAssertion.ps1 -TenantId <tenant-id> -ClientId <client-id> -PfxBase64Path out/rsa.pfx.b64
docker run --rm -v "$PWD/out:/spike/out" rollcall-spike Test-GraphClientAssertion.ps1 -TenantId <tenant-id> -ClientId <client-id> -PfxBase64Path out/ecdsa.pfx.b64
```

Expected result: `✓ PS256 works for Graph` or `✓ ES256 works for Graph`, or a `✕` line with the error from Entra ID.

## 4. S1: Exchange Online with each certificate

```bash
docker run --rm -v "$PWD/out:/spike/out" rollcall-spike Test-ExchangeOnline.ps1 -ClientId <client-id> -Organization <tenant>.onmicrosoft.com -PfxBase64Path out/rsa.pfx.b64 -Mailbox <test-mailbox-upn>
docker run --rm -v "$PWD/out:/spike/out" rollcall-spike Test-ExchangeOnline.ps1 -ClientId <client-id> -Organization <tenant>.onmicrosoft.com -PfxBase64Path out/ecdsa.pfx.b64 -Mailbox <test-mailbox-upn>
```

Expected result: a table with the module version and hash, session start time, memory before and after, and the mailbox conversion result, then a `✓` line. The mailbox ends as a regular user mailbox again.

## 5. Record and clean up

1. Copy each run's output into ADR 0002 (the Results section lists what to record), and change its status to Accepted.
2. Remove the `Rollcall spike` app registration and delete `spikes/out/`, which contains private keys.
