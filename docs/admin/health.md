# Health & monitoring

Open **Admin › Health** for a live check of every Rollcall subsystem:

| Component | Healthy condition |
| --- | --- |
| **PostgreSQL** | Database reachable and all EF Core migrations applied |
| **Background worker** | `worker_heartbeat` updated within the last 60 seconds |
| **Microsoft Graph** | App certificate token acquisition succeeds and tenant organization is readable |
| **Exchange Online** | Pinned `ExchangeOnlineManagement` 3.10.1 module verified (or pooled session ready) |
| **Permissions** | Every Microsoft Graph application permission and directory role required for enabled features is granted |
| **App certificate** | Active RSA 3072 certificate valid with more than 30 days remaining |
| **AD Agent (hybrid)** | At least one enrolled agent heartbeating within the last 2 minutes |
| **Last backup** | A recent backup recorded by `install.sh backup` |

## HTTP probes for external monitors

- `GET /healthz` — Lightweight liveness probe returning HTTP `200 OK` when the web process is serving.
- `GET /readyz` — Readiness probe returning HTTP `200 OK` with `{"db":"ok","migrations":"current","worker":"ok"}` (or `503 Service Unavailable` with diagnostic JSON if any dependency is down).
