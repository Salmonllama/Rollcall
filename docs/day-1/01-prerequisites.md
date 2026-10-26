# Step 1: Prerequisites checklist

**What you're doing:** confirming you have a server, a DNS name, network access, and the right people before you install anything.

Work through each list and tick off every item. The install in [step 2](02-install.md) checks some of these for you, but finding a blocked port or a missing DNS record now saves a second attempt.

## Server

- [ ] A Linux server running one of these 64-bit (x86_64 or arm64) distributions:
  - Ubuntu 22.04 or 24.04 LTS
  - Debian 12 or 13
  - RHEL, Rocky Linux, or AlmaLinux 9
- [ ] At least **2 vCPU, 4 GB RAM, and 20 GB disk**. We recommend 2 vCPU, 8 GB RAM, and 40 GB disk.
- [ ] Root access through `sudo`.
- [ ] Nothing else listening on ports 80 or 443. Rollcall's proxy needs both.

You don't need to install Docker first. If Docker Engine and the Compose plugin are missing, the installer adds them from Docker's official repository. If Docker is already installed, it must be Docker Engine 25 or later with Compose 2.20 or later.

## DNS name and certificate

- [ ] A hostname for Rollcall, for example `rollcall.contoso.com`. Technicians type this into their browser.
- [ ] An HTTPS certificate source. Pick one:

| Option | When to use it | What you need |
| --- | --- | --- |
| Let's Encrypt | The server is reachable from the internet | A public DNS record pointing the hostname at the server, and inbound port 80 open so Let's Encrypt can verify it |
| Your own certificate | Internal servers, or your organization issues its own certificates | A PEM certificate (full chain) and its unencrypted PEM private key, issued for the hostname |
| Local certificate (`--tls internal`) | Test machines and labs only | Nothing. Browsers warn until you trust the local certificate the installer saves for you |

## Network

Inbound to the server:

- [ ] **TCP 443** from the computers technicians use.
- [ ] **TCP 80**, for the HTTP-to-HTTPS redirect and for Let's Encrypt.

Outbound from the server, on **TCP 443**:

| Host | Why |
| --- | --- |
| `download.docker.com` | Installing Docker Engine, only if it's missing |
| `github.com`, `raw.githubusercontent.com`, `objects.githubusercontent.com` | The installer and its configuration templates |
| `ghcr.io`, `pkg-containers.githubusercontent.com` | Rollcall's container images |
| `registry-1.docker.io`, `auth.docker.io`, `production.cloudflare.docker.com` | The Caddy and PostgreSQL images |
| `acme-v02.api.letsencrypt.org` | Let's Encrypt certificates, only with the Let's Encrypt option |
| `www.powershellgallery.com` | The Exchange Online PowerShell module, downloaded and hash-checked when the worker first starts. If your proxy allows hosts by name, also allow `*.powershellgallery.com`, because package downloads can redirect |
| `login.microsoftonline.com` | Signing in to Microsoft Entra ID |
| `graph.microsoft.com` | Microsoft Graph, for users, groups, licenses, and Conditional Access |
| `outlook.office365.com` | Exchange Online, for mailbox changes |

## People and permissions

- [ ] Someone with the **Global Administrator** role in your Microsoft 365 tenant. They grant admin consent to Rollcall's app registration in step 3. Rollcall never asks for or stores their password.
- [ ] **Hybrid only:** a domain-joined Windows Server 2019 or later for the AD agent, and someone who can create a group Managed Service Account (gMSA) and delegate rights on your OUs. You need this in step 6.

## Check it worked

Run these on the server. Each one should succeed.

```bash
grep -E '^(PRETTY_NAME|VERSION_ID)=' /etc/os-release
```

Expected result: one of the distributions listed above.

```bash
sudo ss -ltnp 'sport = :80 or sport = :443'
```

Expected result: only the header line, which means nothing is using port 80 or 443.

```bash
curl -fsS -o /dev/null -w '%{http_code}\n' https://ghcr.io/v2/
```

Expected result: `401`. GitHub's registry answered, so outbound HTTPS works. An error or a timeout means a firewall or proxy is blocking the server.

```bash
getent hosts rollcall.contoso.com
```

Replace `rollcall.contoso.com` with your hostname. Expected result: your server's IP address. You can skip this check for the local certificate option.

**Next:** [Step 2: Run the install command](02-install.md)
