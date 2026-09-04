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
- workload: one `azp`, no `oid`, and permissions in `roles`. Entra application-role
  values use the `.Application` suffix and are normalized to the canonical permission
  names before authorization.

Mixing delegated scopes and application roles, omitting the actor ID, or supplying
ambiguous claims fails closed. Canonical permissions are `EntitySync.Read`,
`EntitySync.Operate`, `EntitySync.Approve`, `EntitySync.Manage`, `EntitySync.Audit`, and
`EntitySync.Expert`. Canonical-change intake additionally requires an `azp` listed in
comma-separated `ENTITYSYNC_OM_WORKLOAD_AZP_ALLOWLIST`. The OAuth token is never
forwarded to a vendor.

## Required production configuration

Compose uses required interpolation for every deployment credential; never add secret
defaults or commit a populated `.env`.

| Group | Variables |
|---|---|
| OAuth resource server | `MCP_OAUTH_AUTHORITY`, `MCP_OAUTH_RESOURCE`, `MCP_OAUTH_AUDIENCE`; advertised `MCP_OAUTH_SCOPES`; single `MCP_OAUTH_REQUIRED_SCOPE`; optional authorization/token endpoint and public-client hints |
| Workload admission | `ENTITYSYNC_OM_WORKLOAD_AZP_ALLOWLIST`, containing comma-separated OrchestraMSP application/client UUIDs |
| Durable state | `POSTGRES_PASSWORD`, `DATABASE_URL`, `ENTITYSYNC_TENANT_IDS` |
| Shared encryption | `ENTITYSYNC_DATA_PROTECTION_KEY_PATH`; Compose fixes both hosts to `/var/lib/entitysync/keys` |
| Worker bounds | `ENTITYSYNC_WORKER_LEASE_SECONDS` (30–600), `ENTITYSYNC_WORKER_HEARTBEAT_SECONDS` (1–30 and less than the lease), `ENTITYSYNC_WORKER_RETRY_SECONDS` (1–60) |
| Telemetry | `OTEL_EXPORTER_OTLP_LOGS_ENDPOINT`, secret `OTEL_EXPORTER_OTLP_HEADERS`, `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf` |

`MCP_OAUTH_AUTHORITY` is the exact HTTPS single-tenant issuer.
`MCP_OAUTH_RESOURCE` is the canonical absolute HTTPS MCP URL advertised in protected
resource metadata; it is not an `api://` Application ID URI.
`MCP_OAUTH_AUDIENCE` separately matches the access token `aud`. The delegated scopes are
`EntitySync.Read`, `EntitySync.Operate`, `EntitySync.Approve`, `EntitySync.Manage`,
`EntitySync.Audit`, and `EntitySync.Expert`. Because Entra custom applications require
scope and application-role values to be unique, define workload roles as
`EntitySync.Read.Application`, `EntitySync.Operate.Application`,
`EntitySync.Approve.Application`, `EntitySync.Manage.Application`,
`EntitySync.Audit.Application`, and `EntitySync.Expert.Application`. EntitySync removes
the `.Application` suffix before policy evaluation. The token has exactly one `tid` from
the configured tenant. Delegated identities have one `oid` and permissions in `scp`;
workloads have one `azp` and permissions in `roles`. Mixed identity forms fail closed.

## Encrypted connection creation

No `ORCHESTRA_*` or vendor credential is steady-state Compose configuration. Connection
public configuration and secret configuration are versioned in PostgreSQL; secret
configuration is encrypted with the shared Data Protection key ring. API and scheduler
resolve the persisted connection at execution time.

The authenticated connection-create endpoint captures server-managed configuration.
For the current runtime, supply `ORCHESTRA_BASE_URL`, `ORCHESTRA_AUTHORITY`,
`ORCHESTRA_TENANT_ID`, `ORCHESTRA_CLIENT_ID`, `ORCHESTRA_RESOURCE`, and
`ORCHESTRA_CLIENT_SECRET` only to an isolated, short-lived API capture process. Keep
the regular scheduler and API free of those variables:

```bash
docker compose --env-file "$ENTITYSYNC_ENV_FILE" stop entitysync-mcp
docker compose --env-file "$ENTITYSYNC_ENV_FILE" run --rm --detach --no-deps \
  --name entitysync-orchestra-capture \
  --publish 127.0.0.1:18080:8080 \
  -e ORCHESTRA_BASE_URL \
  -e ORCHESTRA_AUTHORITY \
  -e ORCHESTRA_TENANT_ID \
  -e ORCHESTRA_CLIENT_ID \
  -e ORCHESTRA_RESOURCE \
  -e ORCHESTRA_CLIENT_SECRET \
  entitysync-mcp
curl --fail --silent --show-error \
  --request POST http://127.0.0.1:18080/api/v1/control/connections \
  --header "Authorization: Bearer $ENTITYSYNC_MANAGE_TOKEN" \
  --header "Idempotency-Key: $CONNECTION_COMMAND_KEY" \
  --header "X-Correlation-ID: $CORRELATION_ID" \
  --header "Content-Type: application/json" \
  --data '{"vendor":"OrchestraMSP","connectionId":"orchestra-primary","displayName":"OrchestraMSP Client Directory"}'
docker stop entitysync-orchestra-capture
docker compose --env-file "$ENTITYSYNC_ENV_FILE" up -d entitysync-mcp
```

Export the six capture values from a secret manager, not a checked-in env file; unset
them immediately after the capture container stops. Test the persisted connection
through the authenticated control API, then verify the restarted API remains ready
without the capture variables. The configured `ORCHESTRA_BASE_URL` ends in
`/api/v1/internal/client-directory/`; both it and the authority are HTTPS in Production.
The EntitySync workload used by that connection has OrchestraMSP application role
`OrchestraMSP.ClientDirectory`, and its client ID is allowlisted in OrchestraMSP's
encrypted `integrations.entitysync.om_workload_client_ids` setting.

## Container contract

`docker-compose.yaml` runs pinned PostgreSQL, API and scheduler images. All three use
explicit numeric non-root users, read-only root filesystems, dropped capabilities,
`no-new-privileges`, bounded tmpfs mounts, health checks and `unless-stopped` restart
behavior. There are no source bind mounts, Docker socket mounts, credential volumes, or
OrchestraMSP database URLs.

Only these named volumes are writable:

| Volume | Writers | Purpose |
|---|---|---|
| `entitysync-db-data` | PostgreSQL | Dedicated durable EntitySync database cluster |
| `entitysync-keyring` | API and scheduler | `/var/lib/entitysync/keys` shared Data Protection key ring |

Scheduler startup applies all forward-only EntitySync migrations before starting its
worker and heartbeat. The API waits for the database and healthy scheduler. The API
probe targets `/health/ready`; it performs no vendor I/O. Readiness is `503` when any
expected migration is absent, the key ring cannot protect and unprotect, or the newest
worker heartbeat is older than three configured heartbeat intervals. `/health` remains
a liveness probe and does not imply safe control operations.

Validate and build without starting:

```powershell
just mcp-compose-config
just mcp-docker-build
```

## Key-ring backup and recovery

Back up the EntitySync PostgreSQL volume and `entitysync-keyring` volume as one recovery
set before migration, deployment or key rotation. Restrict key backups like credentials,
encrypt them at rest, and test restore to an isolated environment.

To restore:

1. Stop both API and scheduler so neither writes state or rotates keys.
2. Restore PostgreSQL and the matching key-ring backup from the same recovery point.
3. Mount `entitysync-keyring` at `/var/lib/entitysync/keys` for both processes with the
   same `LISSTech.EntitySync.Control` application name.
4. Start the scheduler, wait for migrations and a fresh heartbeat, then start the API
   and require `/health/ready` to return `200`.
5. Test one existing connection and read one retained full audit value before resuming
   applies.

Loss of the key ring is not repaired by creating a new empty ring. Existing connection
secrets and retained full-value/snapshot ciphertext become undecryptable. Preserve
metadata, disable affected work, reconnect credentials, and reconcile every `Unknown`
item from authoritative vendor state; never blindly redispatch it.

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

1. Back up the EntitySync database and key ring together.
2. Validate required configuration with
   `docker compose --env-file "$ENTITYSYNC_ENV_FILE" config --quiet`.
3. Build both images and inspect their pinned base digests and numeric users.
4. Start PostgreSQL and wait for `pg_isready`.
5. Start the scheduler. Its migration hosted service runs before the control worker,
   then the worker records heartbeats.
6. Start the API. Require liveness and then readiness:

```bash
docker compose --env-file "$ENTITYSYNC_ENV_FILE" exec entitysync-db \
  sh -c 'pg_isready -U "$POSTGRES_USER" -d "$POSTGRES_DB"'
docker compose --env-file "$ENTITYSYNC_ENV_FILE" exec entitysync-scheduler \
  wget -qO- http://127.0.0.1:8080/health
docker compose --env-file "$ENTITYSYNC_ENV_FILE" exec entitysync-mcp \
  wget -qO- http://127.0.0.1:8080/health
docker compose --env-file "$ENTITYSYNC_ENV_FILE" exec entitysync-mcp \
  wget -qO- http://127.0.0.1:8080/health/ready
```

7. Test configured persisted connections explicitly, then resume schedules and
   canonical intake.

### Routine restart

Stop API and scheduler gracefully, leave both named volumes attached, start scheduler
first, wait for a fresh heartbeat, then start API. Verify that pre-restart plan,
operation and audit IDs return unchanged. Leases expire and are fenced; work checkpoints
resume from durable state. Do not manually reset leases or approvals.

### Rollback

Stop both application processes before changing binaries. Database migrations are
forward-only: roll back an image only when it is documented compatible with the current
schema. Otherwise restore the pre-deployment database and matching key ring together.
Never roll back only the database, delete migration rows, reuse a consumed approval, or
discard `Unknown` evidence. After rollback, require readiness and reconcile in-flight or
`Unknown` vendor outcomes before enabling work.

## Local development

```powershell
just mcp-build
just mcp-run
just scheduler-build
just scheduler-run
```

Local stdio mode may create a user-local key directory. Production HTTP and scheduler
modes require an explicit durable key path. DPAPI profiles are Windows-only and are not
a Linux-container credential mechanism.
