# LISSTech EntitySync MCP and Control Plane

The MCP executable has two modes:

| Mode | Configuration | Scope |
|---|---|---|
| Local stdio | `MCP_TRANSPORT=stdio` (default) | Local MCP tools and a user-local key ring |
| Production HTTP | `MCP_TRANSPORT=http` | OAuth-protected MCP tools and the durable `/api/v1/control/*` API |

The scheduler is a separate lease-based worker. PostgreSQL is authoritative for connections, immutable policy versions, plans and inspections, one-time approvals, operations, item evidence, schedules, canonical changes, work checkpoints, and audit metadata. API and scheduler processes can restart without changing those IDs or losing terminal state.

## HTTP surfaces and authorization

| Route | Authentication | Purpose |
|---|---|---|
| `/mcp` | JWT bearer with `MCP_OAUTH_REQUIRED_SCOPE` | Streamable HTTP MCP |
| `/api/v1/control/*` | JWT bearer plus the endpoint permission | Durable control API |
| `/openapi/v1.json` | `EntitySync.Read` | Control OpenAPI document |
| `/.well-known/oauth-protected-resource/mcp` | None | RFC 9728 metadata |
| `/health` | None | Process liveness only |
| `/health/ready` | None | Database migration, key-ring and recent worker-heartbeat readiness |

JWT validation checks signature, issuer, expiration, audience and the relevant permission. A control identity must contain exactly one `tid` and exactly one actor form:

- delegated: one `oid`, no `azp`, and permissions in `scp`;
- workload: one `azp`, no `oid`, and permissions in `roles`.

Mixing delegated scopes and application roles, omitting the actor ID, or supplying ambiguous claims fails closed. Permissions are `EntitySync.Read`, `EntitySync.Operate`, `EntitySync.Approve`, `EntitySync.Manage`, `EntitySync.Audit`, and `EntitySync.Expert`. Canonical-change intake additionally requires an `azp` listed in comma-separated `ENTITYSYNC_OM_WORKLOAD_AZP_ALLOWLIST`. The OAuth token is never forwarded to a vendor.

## Required production configuration

Compose uses required interpolation for every credential; never add secret defaults or commit a populated `.env`.

| Group | Variables |
|---|---|
| OAuth resource server | `MCP_OAUTH_AUTHORITY`, `MCP_OAUTH_RESOURCE`, `MCP_OAUTH_AUDIENCE`; optional advertised `MCP_OAUTH_SCOPES`, single `MCP_OAUTH_REQUIRED_SCOPE`, authorization/token endpoint and public-client hints |
| Durable state | `POSTGRES_PASSWORD`, `DATABASE_URL`, `ENTITYSYNC_TENANT_IDS` |
| Shared encryption | `ENTITYSYNC_DATA_PROTECTION_KEY_PATH` (Compose fixes both hosts to the same named volume and application name) |
| Worker bounds | `ENTITYSYNC_WORKER_LEASE_SECONDS` (30–600), `ENTITYSYNC_WORKER_HEARTBEAT_SECONDS` (1–30 and less than the lease), `ENTITYSYNC_WORKER_RETRY_SECONDS` (1–60) |
| Orchestra Client Directory | `ORCHESTRA_BASE_URL` ending `/api/v1/internal/client-directory/`, `ORCHESTRA_AUTHORITY`, `ORCHESTRA_TENANT_ID`, `ORCHESTRA_CLIENT_ID`, `ORCHESTRA_RESOURCE`, `ORCHESTRA_CLIENT_SECRET` |
| Telemetry | official Logfire `OTEL_EXPORTER_OTLP_LOGS_ENDPOINT`, secret `OTEL_EXPORTER_OTLP_HEADERS`, `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf` |

Orchestra authority and Client Directory URLs and `MCP_OAUTH_AUTHORITY` are HTTPS-only in Production. The executable smoke may use loopback HTTP only when the host environment is `Testing` or `Development` and the corresponding dedicated flag is true: `ENTITYSYNC_TEST_ALLOW_HTTP_ORCHESTRA` for both Orchestra URLs and `ENTITYSYNC_TEST_ALLOW_HTTP_OAUTH_AUTHORITY` for OAuth. A flag or non-production environment alone still fails closed. Other adapter credentials remain optional until a connection for that vendor is created.

Non-secret platform IDs such as Orchestra platform-instance IDs, `NCENTRAL_SERVICE_ORG_ID`, connection IDs, tenant IDs, policy IDs and route scopes may be stored in normal configuration. They are identity and fencing inputs, not credentials. Client secrets, tokens, passwords, signing keys and data-protection keys remain secret material.

## Container contract

`docker-compose.yaml` runs pinned PostgreSQL, API and scheduler images. All three containers use explicit numeric non-root users, read-only root filesystems, dropped capabilities, `no-new-privileges`, bounded tmpfs mounts and bounded health/start/stop behavior. There are no source bind mounts.

Only these named volumes are writable:

| Volume | Writers | Purpose |
|---|---|---|
| `entitysync-db-data` | PostgreSQL | Durable database cluster |
| `entitysync-data-protection-keys` | API and scheduler | Shared ASP.NET Data Protection key ring |

The API probe targets `/health/ready`; it performs no vendor I/O. Readiness is `503` when migration `018_snapshot_evidence_enrichment` is absent, the key ring cannot protect and unprotect, or the newest worker heartbeat is older than three configured heartbeat intervals. `/health` remains a liveness probe and does not imply safe control operations.

Validate and build without starting:

```powershell
just mcp-compose-config
just mcp-docker-build
```

## Key-ring backup and recovery

Back up the PostgreSQL volume and data-protection key volume as one recovery set before migration, deployment or key rotation. Restrict key backups like credentials, encrypt them at rest, and test restore to an isolated environment.

To restore:

1. Stop both API and scheduler so neither writes state or rotates keys.
2. Restore PostgreSQL and the matching key-ring backup from the same recovery point.
3. Mount the key ring at the configured path for both processes with the same `LISSTech.EntitySync.Control` application name.
4. Start the scheduler, wait for migrations and a fresh heartbeat, then start the API and require `/health/ready` to return `200`.
5. Test one existing connection and read one retained full audit value before resuming applies.

Loss of the key ring is not repaired by creating a new empty ring. Existing connection secrets and retained full-value/snapshot ciphertext become undecryptable. Preserve metadata, disable affected work, reconnect credentials, and reconcile every `Unknown` item from authoritative vendor state; never blindly redispatch it.

## Planning and apply workflow

1. Create and test source and target connections. Remote callers provide identity and non-secret platform-instance values only; server configuration owns endpoints and credentials.
2. Create an immutable policy version and a bounded plan. Planning performs no writes.
3. Fetch every plan page. Inspection coverage is persisted and digest-bound.
4. Approve the exact inspected digest. Approval is one-time and consumed atomically by apply.
5. Queue a dry run and inspect its terminal operation.
6. Queue apply once and poll the durable operation. A repeated idempotent request returns the same operation; it does not create another write.
7. Inspect item snapshots and correlated audit events. Before/desired/result evidence uses the shared key ring.

An item with outcome `Unknown` means dispatch may have reached the vendor but the outcome was not proved. The worker must read authoritative vendor state and prove the desired state before marking it succeeded. Inconclusive reconciliation remains `Unknown`; it must not be blindly retried or redispatched.

## Retention and audit

Audit metadata is immutable and retained after full values expire. Full audit values and operation item snapshots are encrypted and expire after 365 days. The retention worker scrubs ciphertext only after database time reaches `expires_at`, records `values_redacted_at`, and preserves metadata and hashes. Before expiry, ciphertext cannot be cleared or mutated. A snapshot may fill a previously-null before or after half once while preserving identity and expiry; already populated ciphertext is immutable.

Backups can retain older ciphertext beyond the live 365-day window. Apply equivalent backup expiration and destruction controls.

## Start, restart and rollback runbook

### Initial start or forward deployment

1. Back up database and key ring together.
2. Validate required configuration and `docker compose config --quiet`.
3. Build both images and inspect their pinned base digests and numeric users.
4. Start PostgreSQL and wait for `pg_isready`.
5. Start the scheduler. Startup applies forward-only idempotent migrations before work and then records heartbeats.
6. Start the API. Require `/health`, then `/health/ready`; do not use vendor probes for readiness.
7. Test configured connections explicitly, then resume schedules and canonical intake.

### Routine restart

Stop API and scheduler gracefully, leave both named volumes attached, start scheduler first, wait for a fresh heartbeat, then start API. Verify that pre-restart plan, operation and audit IDs return unchanged. Leases expire and are fenced; work checkpoints resume from durable state. Do not manually reset leases or approvals.

### Rollback

Stop both application processes before changing binaries. Database migrations are forward-only: roll back an image only when it is documented compatible with the current schema. Otherwise restore the pre-deployment database and matching key ring together. Never roll back only the database, delete migration rows, reuse a consumed approval, or discard `Unknown` evidence. After rollback, require readiness and reconcile in-flight/Unknown vendor outcomes before enabling work.

## Local development

```powershell
just mcp-build
just mcp-run
just scheduler-build
just scheduler-run
```

Local stdio mode may create a user-local key directory. Production HTTP and scheduler modes require an explicit durable key path. DPAPI profiles are Windows-only and are not a Linux-container credential mechanism.
