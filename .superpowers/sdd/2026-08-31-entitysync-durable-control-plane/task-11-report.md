# Task 11 Report — EntitySync Production Verification and Configuration

**Date:** 2026-09-01  
**Reviewed base:** `c513b9828a7f84afcadb3221dd780c0fda1e0dfd`  
**Commit message:** `chore(control): harden durable control deployment` (SHA returned with the completed task)

## Result

Task 11 is complete. The API, scheduler, PostgreSQL schema, production container contract, release checks, and signed actual-process restart scenario now exercise the same durable control plane. The final full verification commands exit zero. The only skips are the two explicit non-Windows DPAPI profile tests.

## RED/GREEN Record

### Focused durable restart test

The focused `ControlPlaneEndToEndTests` were added before production deployment edits. Initial RED exposed that reconstructed hosts could not read protected snapshot values when the original provider/key configuration was discarded. The test also exposed a missing pre-dispatch snapshot enrichment path. After wiring the shared persistent data-protection key ring and preserving the before/desired ciphertext evidence, the focused suite passed.

Final focused durable E2E result:

```text
Passed: 3, Failed: 0, Skipped: 0
```

The tests prove:

- an isolated PostgreSQL database is migrated from embedded production migrations;
- two production adapter contracts are represented by loopback Orchestra Client Directory and N-central HTTP servers;
- connections, immutable policy, plan, every inspection page, one-time approval, queued operation, operation items, and audit evidence survive reconstructed service providers;
- the reconstructed worker consumes the approved operation once;
- the target adapter observes exactly one write;
- the same plan/run identifiers and terminal result remain queryable after restart;
- the persisted before, desired, and result evidence is correlated to the same operation item and audit event.

### Actual-process smoke RED/GREEN

The actual-host scenario deliberately uses child `dotnet run` processes rather than `WebApplicationFactory`. Intermediate RED runs found and corrected:

1. child processes starting outside the repository root;
2. a Unix key-directory mode that did not satisfy the production key-ring accessibility check;
3. an Orchestra source selection mismatch in the fake contract;
4. a missing N-central service-organization route parameter;
5. stale test configuration that did not include the exact Orchestra Client Directory path and bounded worker intervals.

Final focused actual-process result:

```text
Actual_hosts_complete_signed_control_lifecycle_across_restart
Passed: 1, Failed: 0, Skipped: 0
```

The scenario launches real API and scheduler executables, a PostgreSQL container, a loopback RSA JWT issuer with OIDC discovery/JWKS, a loopback Orchestra token/client-directory server, and a loopback N-central server. It verifies `/health`, waits for `/health/ready`, uses a signed delegated token with `tid`, `oid`, `scp`, resource, and audience claims, creates and tests both connections, creates the policy and plan, follows every opaque inspection page, approves once, dry-runs, applies, restarts both child processes, and reads the same terminal run and audit. Exactly one N-central target write is asserted.

## Production Host and Readiness Contract

- `/health` is process liveness only.
- `/health/ready` checks the database connection, exact migration count, data-protection key-ring write/protect/unprotect access, and a non-stale scheduler heartbeat.
- Readiness performs no vendor HTTP calls.
- Missing database, key-ring, migration, or worker-heartbeat prerequisites produce an unhealthy readiness result without leaking configuration or credentials.
- API and scheduler use the same `LISSTech.EntitySync.ControlPlane.Readiness.v1` probe purpose and the same `LISSTech.EntitySync.ControlPlane` data-protection application name.
- HTTP and scheduler modes require an explicit `ENTITYSYNC_DATA_PROTECTION_KEY_PATH`; local stdio retains its user-local path.
- HTTP startup validates an HTTPS OAuth authority/resource, explicit audience containing the access-token audience, and at least one configured scope.
- HTTP and scheduler startup validate the Orchestra Client Directory base path, HTTPS authority, tenant ID, client ID, resource, and client secret.
- Scheduler lease, heartbeat, and retry values are required integer seconds within bounded ranges; heartbeat must be shorter than the lease.
- Safe configuration failures use stable `ENTITYSYNC_CONFIG_*` codes.

## Container Security Matrix

| Service | Image/user | Root filesystem | Linux privileges | Writable paths | Health/readiness |
|---|---|---|---|---|---|
| PostgreSQL | pinned `postgres:18-trixie` digest; UID/GID `999:999` | read-only | `cap_drop: ALL`, `no-new-privileges` | named `entitysync-db-data`; bounded `/tmp` and `/var/run/postgresql` tmpfs | `pg_isready`; API/worker wait for service health |
| API | pinned .NET 8 Alpine SDK/runtime digests; UID/GID `1654:1654` | read-only | `cap_drop: ALL`, `no-new-privileges` | shared named data-protection key volume; bounded `/tmp` tmpfs | `/health/ready`; DB migration/key ring/worker heartbeat |
| Scheduler | pinned .NET 8 Alpine SDK/runtime digests; UID/GID `1654:1654` | read-only | `cap_drop: ALL`, `no-new-privileges` | same shared named data-protection key volume; bounded `/tmp` tmpfs | `/health`; bounded start/stop; heartbeat persisted to PostgreSQL |

Both production Dockerfiles use multi-stage `dotnet publish`, copy only published output, pin the runtime UID before `ENTRYPOINT`, and create the key directory with mode `0700`. There are no source bind mounts and no writable application root.

Observed runtime inspection confirmed UID `999` for PostgreSQL and UID `1654` for both .NET hosts, read-only roots, `ALL` capability drop, `no-new-privileges`, only the documented named volumes, and bounded tmpfs mounts. `docker compose up --wait` reached healthy for PostgreSQL, scheduler, and API; teardown removed containers, network, and test volumes.

## Environment and Volume Contract

Compose uses required interpolation for every credential and authority value. No production default supplies a password, bearer token, client secret, or private endpoint.

Required shared/durable configuration:

- `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD`, `DATABASE_URL`;
- `ENTITYSYNC_TENANT_IDS`;
- `ENTITYSYNC_WORKER_LEASE_SECONDS`, `ENTITYSYNC_WORKER_HEARTBEAT_SECONDS`, `ENTITYSYNC_WORKER_RETRY_SECONDS`;
- `MCP_OAUTH_AUTHORITY`, `MCP_OAUTH_RESOURCE`, `MCP_OAUTH_AUDIENCE`, `MCP_OAUTH_SCOPES`, `MCP_OAUTH_REQUIRED_SCOPE`;
- `ORCHESTRA_BASE_URL`, `ORCHESTRA_AUTHORITY`, `ORCHESTRA_TENANT_ID`, `ORCHESTRA_CLIENT_ID`, `ORCHESTRA_RESOURCE`, `ORCHESTRA_CLIENT_SECRET`;
- `OTEL_EXPORTER_OTLP_LOGS_ENDPOINT`, `OTEL_EXPORTER_OTLP_HEADERS`.

The API and scheduler receive the same `ENTITYSYNC_DATA_PROTECTION_KEY_PATH` inside the shared `entitysync-data-protection-keys` volume. Only PostgreSQL receives `entitysync-db-data`. The README records backup/restore order, the consequence of key-ring loss, migration/start/restart/rollback procedures, and how to validate readiness after recovery.

## Persistence and Migration Corrections

Task 11 verification found and corrected several real upgrade/retention defects:

- migration `018_snapshot_evidence_enrichment.sql` upgrades existing installations with enriched operation snapshot evidence while retaining immutable metadata;
- expired operation/audit ciphertext is scrubbed by setting ciphertext to `NULL` and recording `values_redacted_at`; metadata rows cannot be deleted;
- inspection completion now requires exact, gap-free, non-overlapping ordinal coverage, both on fresh installs and upgrades;
- the connection generation counter claim is atomic under first-write concurrency;
- the legacy schedule repository now supplies the required receipt ID, outbox identity, and payload digest when inserting canonical change events;
- repository tests use PostgreSQL time as authoritative for lease reclamation and approval expiry rather than pretending a caller timestamp advances the database clock;
- durable creation recovery fixtures include the pinned-canonical fields in the exact request digest, preserving response-loss recovery without weakening idempotency conflicts.

Platform tests are assembly-serialized because multiple production-host fixtures temporarily set process-wide environment variables and launch child processes. This removes cross-test credential/database contamination; it does not change production concurrency.

## Release Workflow

`.github/workflows/release-mcp.yml` now has least-privilege `contents: read` permissions and a pinned PostgreSQL service. The release verification job:

1. checks out and installs .NET;
2. builds both production images, records their SHA-256 image IDs, and asserts runtime UID `1654`;
3. runs migration, control API, scheduler, repository, and end-to-end Release tests against PostgreSQL;
4. asserts the literal 16-command manifest/import set;
5. launches the just-built API and scheduler images, rather than substituting host `dotnet run`;
6. runs both images with a read-only root, bounded `/tmp`, and their shared named key volume, then inspects those runtime controls;
7. calls scheduler `/health` and the real API `/health/ready` against PostgreSQL;
8. removes the image containers and key volume even after failure;
9. publishes the release manifest only from the release path.

No credential value is echoed by the workflow. PostgreSQL, Orchestra, and telemetry credentials are referenced only through GitHub Actions secrets; non-secret platform identifiers remain job-scoped values.

## Operations and Security Documentation

`mcp/README.md` now documents:

- delegated and workload identity claim shapes;
- exact role/scope boundaries and the MCP resource/scope contract;
- required API, worker, OAuth, Orchestra, telemetry, and database settings;
- why the Orchestra base is the Client Directory root, not an arbitrary service URL;
- key-ring backup/restore and the irrecoverability of protected connection/snapshot values after key loss;
- the 365-day ciphertext scrub while immutable audit metadata remains;
- `Unknown` reconciliation before retry and the prohibition on blind redispatch;
- migration-first startup, safe restart, rollback, health validation, and failure handling.

## Complete Verification

All requested commands were run from the Task 11 worktree against the verification PostgreSQL instance.

| Command | Result |
|---|---|
| `just build` | exit `0`; build succeeded, `0` warnings, `0` errors |
| `just test-load` | exit `0`; exact 16 commands imported/listed |
| `just test` | exit `0`; Pester `195` passed, `0` failed, `2` skipped; platform `447` passed, `0` failed, `0` skipped |
| `dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --configuration Release --no-restore --verbosity minimal` | exit `0`; `447` passed, `0` failed, `0` skipped |
| focused actual-process smoke | exit `0`; `1` passed, `0` failed, `0` skipped |
| `docker compose --file docker-compose.yaml config --quiet` | exit `0` |
| `docker compose --file docker-compose.yaml build entitysync-mcp entitysync-scheduler` | exit `0`; corrected API image `sha256:ff6b134a…`, scheduler image `sha256:23f399e7…` |
| hardened compose `up --wait` and runtime inspection | all three services healthy; expected users/security/mounts; clean `down --volumes --remove-orphans` |
| post-review authority-policy tests | exit `0`; `5` passed, proving Production rejects loopback HTTP and only explicit Testing/Development override permits it |
| post-review built-image readiness smoke | API and scheduler images ran as `1654:1654`, read-only root, bounded `/tmp`, shared key volume; `/health/ready` returned database/key-ring/heartbeat `true`; containers and volume removed |
| post-review signed actual-process restart smoke | exit `0`; `1` passed with the explicit `Testing` plus test-only authority override |

## Warnings and Explicit Skips

- The two DPAPI profile persistence tests are explicitly skipped on non-Windows platforms with `-Skip:(-not $IsWindows)`. Production DPAPI behavior was not changed.
- ASP.NET Data Protection emits the expected warning that test key files are not additionally encrypted at rest. Production isolation is provided by the dedicated restricted key volume; platform backup/encryption controls remain an operator responsibility.
- The signed process smoke uses loopback test authorities only with `ASPNETCORE_ENVIRONMENT=Testing`, `ENTITYSYNC_TEST_ALLOW_HTTP_OAUTH_AUTHORITY=true`, and `ENTITYSYNC_TEST_ALLOW_HTTP_ORCHESTRA=true`; Production rejects the same authorities even when either flag is set. No test credential or generated RSA private key is persisted in the repository.

## Security Self-Review

- No committed secret, token, private key, credential-bearing endpoint, or permissive production authentication fallback was introduced.
- Required compose values use failing interpolation rather than defaults.
- JWT validation continues to require authority, resource metadata, audience, authentication, tenant/actor identity, and exact permission claims.
- Production URI validation requires HTTPS and the expected Orchestra Client Directory path. Loopback HTTP OAuth authority is possible only when both the dedicated test flag and a `Testing`/`Development` host environment are present; Production cannot enable it.
- Readiness is local-control-plane only and cannot trigger vendor traffic.
- Database time governs lease and approval expiry, preventing callers from advancing or extending authoritative expiry decisions.
- Approval is consumed once inside the durable transaction; operation items and snapshots are inserted before dispatch; stale workers are fenced by attempt and lease owner.
- Unknown vendor outcomes require reconciliation before retry; no blind mutation redispatch was added.
- Logs and HTTP problems are redacted; the dependency-failure test proves nested vendor details do not reach the response or captured logs.

## Concerns

No release-blocking concern remains. Operators must treat the PostgreSQL volume and data-protection key volume as one recovery set: restoring the database without the corresponding key ring leaves protected connection and evidence ciphertext unreadable. Base-image digests and the external telemetry endpoint must continue to be updated through normal dependency/security maintenance.

## Fix Round 1

Five Important post-commit review findings were reproduced and closed in a separate fix commit.

### Corrections

1. Both `ORCHESTRA_BASE_URL` and `ORCHESTRA_AUTHORITY` are HTTPS-only in Production. Loopback HTTP requires both a `Testing`/`Development` host environment and `ENTITYSYNC_TEST_ALLOW_HTTP_ORCHESTRA=true`. API and scheduler use the same validation path; the signed smoke supplies the explicit test gate.
2. API and scheduler register `EntitySyncOperationWorkerOptions` from the already parsed `EntitySyncWorkerSettings.LeaseDuration`. The scheduler host test proves a configured 30-second lease reaches the operation worker; the durable operation worker and scheduler route/control work therefore use the same bounded `ENTITYSYNC_WORKER_LEASE_SECONDS`, and invalid interval bounds still fail configuration.
3. Migration 018 permits fill-once snapshot enrichment only while `OLD.expires_at > clock_timestamp()`. Expired unsrubbed ciphertext cannot be populated, existing ciphertext remains immutable, and post-expiry scrub behavior is unchanged.
4. `EntitySyncDatabaseMigrator.ExpectedVersions` is derived from the single embedded migration resource inventory used by the migrator. Readiness now compares the complete exact applied version set, rejecting a missing old/latest version or unknown version. The schema primary key rejects duplicates.
5. The release image smoke adds `--cap-drop ALL` and `--security-opt no-new-privileges` to both containers and asserts both settings through Docker inspection before readiness can pass.

### RED/GREEN and Verification

- Focused RED exited `1` for the absent Orchestra matrix API and migrator expected-version contract. The first executable run reached `10` passes and one fixture-only composite-expiry mismatch; using transaction-stable `now()` for the matching foreign-key timestamps corrected the fixture.
- Focused GREEN: `11` passed, `0` failed, `0` skipped. Coverage includes API/scheduler Orchestra matrices, API/scheduler operation lease injection, exact migration readiness, live PostgreSQL pre-expiry/expired fill boundaries, and reconstructed-worker DB-clock reclaim.
- Signed actual-process restart smoke: `1` passed, `0` failed, `0` skipped with both explicit test-only loopback gates.
- `just build`: exit `0`, zero warnings/errors.
- `just test-load`: exit `0`, exact 16 exports.
- `just test`: exit `0`; Pester `195` passed, `0` failed, `2` explicit non-Windows DPAPI skips; platform `460` passed, `0` failed, `0` skipped.
- Direct full Release platform suite: exit `0`; `460` passed, `0` failed, `0` skipped.
- Workflow YAML parse: exit `0`.
- Corrected images built as API `sha256:007d503e53888382557f47282d0ba7067718708bcb05eaf60dc5d21a96126411` and scheduler `sha256:799832a23fd5954ebeb8d72b8b148be8a5073bbcb00358e171b35b1181b6cdda`.
- Both built images ran as `1654:1654`, read-only root, `CapDrop=[ALL]`, `SecurityOpt=[no-new-privileges]`, bounded no-exec/no-suid/no-device `/tmp`, and only the shared key volume writable. `/health/ready` returned all database-migration, key-ring, and worker-heartbeat prerequisites true. Containers and temporary key volume were removed.

### Fix-Round Security Review

Production cannot opt into either cleartext OAuth or cleartext Orchestra trust roots. Readiness returns only prerequisite booleans and performs no vendor I/O. Migration-set drift and rollback now fail closed. Lease duration is consistent across the operation and scheduler layers, and database time remains authoritative for expiry/reclamation. No credentials, tokens, signing material, or secret endpoints were added.
