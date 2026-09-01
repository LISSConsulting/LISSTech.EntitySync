# Task 9 Report — OrchestraMSP canonical client directory

Date: 2026-09-01  
Reviewed base: `9df841c69c68ae6edd0aadda8307b1ab57551a92`

## RED / GREEN evidence

- RED: `dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --configuration Release --filter FullyQualifiedName~OrchestraEntityAdapterTests` exited 1 because `LISSTech.EntitySync.Adapters.OrchestraMSP`, `OrchestraTokenProvider`, and `OrchestraEntityAdapter` did not exist (`artifact://1767`).
- Final minimal compile: `dotnet build src/Adapters/LISSTech.EntitySync.Adapters.csproj --configuration Release && dotnet build src/Hosting/LISSTech.EntitySync.Hosting.csproj --configuration Release` passed after final secret-cleanup and identity hardening; the adapter build reported 0 warnings and 0 errors and the hosting build succeeded (`artifact://1802`).
- Final GREEN: the exact focused command above passed 14/14 socket-loopback tests with 0 failures and 0 skips in 237 ms (`artifact://1804`).

The focused tests use real asynchronous HTTP over a loopback socket. They do not sleep or perform blocking network I/O.

## HTTP contract

| Operation | Method and path | Required request contract | Result handling |
|---|---|---|---|
| Client-credentials token | `POST {authority}/{tenant}/oauth2/v2.0/token` | Form body `grant_type=client_credentials`, configured client ID/secret, scope `{resource}/.default` | Typed token JSON; refresh synchronized until five minutes before expiry; safe error codes only |
| List clients | `GET /api/v1/internal/client-directory/clients` | Bearer token; explicit `include_inactive`, `limit=100`, opaque cursor token | Typed bounded pages; cursor cannot alter scheme/origin/path |
| Read client | `GET /api/v1/internal/client-directory/clients/{client_uuid}` | Bearer token; UUID path segment | Returns observed UUID/current version; Task 7 classifies asserted event version as exact, stale, or identity mismatch |
| Read site/address | Bounded traversal of typed client pages | Bearer token; `include_inactive=true` | Finds immutable nested UUID and observed current version; conflicting duplicate parent/version identity fails closed |
| Resolve Site/Address parent | Bounded traversal of typed client pages and platform links | Bearer token; foreign parent vendor/type/external ID | Resolves one active canonical Client/Site UUID; missing, stale, or duplicate links hold for review before write |
| Create client | `POST /api/v1/internal/client-directory/clients` | Bearer token, stable `Idempotency-Key`, typed command/correlation | Receipt-safe same-key/body replay only after an ambiguous response, then exact authoritative readback |
| Update client | `PATCH /api/v1/internal/client-directory/clients/{client_uuid}` | Bearer token, stable `Idempotency-Key`, required `If-Match` | `409` becomes `StaleCanonicalVersionException` and is never retried; exact authoritative readback required |
| Create/update site | `POST/PATCH /api/v1/internal/client-directory/clients/{client_uuid}/sites[/site_uuid]` | Stable idempotency key; update requires `If-Match` | Same optimistic/idempotent response and readback rules |
| Create/update address | `POST/PATCH /api/v1/internal/client-directory/addresses[/address_uuid]` | Stable idempotency key; update requires `If-Match` | Same optimistic/idempotent response and readback rules |
| Platform link upsert | `PUT /api/v1/internal/client-directory/platform-links` | Bearer token, stable `Idempotency-Key`, typed platform/entity identity | Typed result followed by authoritative link lookup/readback |

## Canonical mapping and focused work

- `Client`, `Site`, and `Address` preserve immutable UUID, version, parent identity, lifecycle/deletion state, sorted tags, deterministic custom-field JSON, nested address/site shape, and platform links.
- A merge donor carries its survivor UUID and is inactive; a survivor carries sorted donor UUIDs. Deleted and donor records cannot appear as active duplicates.
- `ICanonicalEntityVersionAdapter` is a narrow Ports contract so the adapter project does not depend on Application. `CanonicalChangeService` and `EntitySyncControlWorker` use that one existing exact-version pipeline.
- `ServerManagedEntityAdapterFactory` returns the first-class `OrchestraMSP` adapter. `ConnectionRuntimeFactory` supplies the persisted generation, so each runtime lease owns its own provider/cache. The prior `CANONICAL_VERSION_READER_UNAVAILABLE` hold is no longer reached for a valid Orchestra connection.
- The operation worker copies its deterministic vendor request ID into `IdempotencyKey`; update mapping carries the target canonical version into `ExpectedVersion`.

## Security self-review

- No static/process-global token cache exists. Refresh state and `SemaphoreSlim` belong to one generation-local provider and are disposed with the connection lease.
- The provider copies caller secret bytes and zeroes its owned bytes on dispose. Factory-created temporary UTF-8 secret bytes are zeroed on success and failure. Factory/runtime dictionaries are cleared in `finally`; immutable .NET strings cannot be zeroed, so no additional long-lived string copy is retained.
- Token JSON, access tokens, secrets, and vendor response bodies are never included in exceptions, results, logs, or database values.
- Base URI validation requires HTTPS except numeric loopback test hosts, rejects user info/query/fragment, and requires the exact Client Directory base path. Paging accepts only bounded URL-safe opaque cursor tokens and always constructs the next request against the configured URI.
- Cancellation propagates without write replay. A `409` is never retried. Only an ambiguous successful/transport response is replayed once with identical bytes and idempotency key, relying on Orchestra's durable command-receipt contract; a second ambiguous outcome is surfaced as unknown.

## Concerns

- The reviewed Orchestra backend contract does not expose a request-ID outcome endpoint. `LookupWriteByRequestIdAsync` therefore reports `Unsupported` without issuing HTTP; it never infers `Applied` from a mutable field readback. Ambiguous in-flight writes use only documented durable same-key/same-body command-receipt replay followed by authoritative identity/version readback.
- The backend route inventory does not expose standalone `GET` routes for site/address versions. Exact site/address reads use bounded typed traversal of client aggregates and fail closed on missing, stale, or ambiguous identity.

## Post-review fix round

- RED: the exact focused command compiled and ran 24 tests, with 14 passing and 10 intentional failures proving that the prior implementation lost Site parent identity, emitted one anonymous Client-shaped payload for Site/Address, defaulted every update lifecycle to `active`, collapsed asserted version mismatch into not-found, and inferred request application from unchanged mutable state (`artifact://1818`). A separate focused RED proved conflicting duplicate Address parent/version records were silently collapsed (`artifact://1824`).
- GREEN: the mapper-to-adapter socket tests now assert distinct typed Client/Site/Address command bodies, route/body parent UUID agreement, required safe missing/conflicting-parent failures before HTTP, and `If-Match`/idempotency/readback for both creates and updates. The lifecycle matrix covers active, suspended, deleted, and merge-donor targets and proves ordinary updates omit lifecycle, deletion, and merge fields while retaining approved fields.
- Outcome evidence covers no-request-landed/two transport failures as unknown, unchanged old target state as request-ID lookup unsupported, receipt replay plus authoritative readback as applied, stale `409` as no-retry conflict, and cancellation as no replay. Event version $N$ with an authoritative current version $N+1$ now returns the observed identity/version so the existing Task 7 service holds it as `StaleVersion`, not `NotFound`.
- Final focused GREEN: `dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --configuration Release --filter FullyQualifiedName~OrchestraEntityAdapterTests` passed 25/25 asynchronous socket-loopback tests with 0 failures and 0 skips in 340 ms (`artifact://1826`).
- Final minimal compile: `dotnet build src/Adapters/LISSTech.EntitySync.Adapters.csproj --configuration Release && dotnet build src/Hosting/LISSTech.EntitySync.Hosting.csproj --configuration Release` passed with 0 warnings and 0 errors in both projects (`artifact://1828`).

## Cross-vendor parent-resolution fix round

- RED: the focused suite's added production-shape cases intentionally failed to compile because the write model had no typed parent contract, no `Address` payload, no planner/worker parent resolver, and no stale/ambiguous parent status (`artifact://1838`). This established that a foreign source parent ID could not safely become an Orchestra Client/Site UUID without new explicit behavior.
- GREEN: Orchestra Site/Address creates now resolve the foreign parent through authoritative platform links into `EntityWriteParent`. The planner records the resolved parent or visibly changes the item to `Review`/`ParentLinkReview` with a safe missing, ambiguous, or stale code. The operation worker independently resolves again through the generation-pinned target lease immediately before mapping, hashing, and dispatch, so link drift cannot produce a write to a stale parent.
- The mapper never forwards foreign parent metadata to Orchestra. Its resolved-parent overload sets only canonical Client/Site UUIDs, produces typed `EntityAddress`, preserves custom fields, and includes the Address in deterministic manifest before/desired payloads. Existing mapper implementations retain source compatibility through the original four-argument interface method.
- Address identity traversal groups duplicate UUIDs across root and site aggregates, compares deterministic mapped payloads, and fails closed with `ORCHESTRA_IDENTITY_CONFLICT` if parent/version/payload disagree. Ambiguous address write recovery succeeds only when exact typed parent and address fields match the authoritative readback.
- Final focused GREEN: `dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --configuration Release --filter FullyQualifiedName~OrchestraEntityAdapterTests` passed 35/35 tests with 0 failures and 0 skips in 482 ms (`artifact://1854`). This includes real loopback HTTP for platform-link resolution and cross-vendor NCentral Site-to-Orchestra planning for both resolved and visibly held missing-parent outcomes.
- Final minimal compile: `dotnet build src/Adapters/LISSTech.EntitySync.Adapters.csproj --configuration Release` and `dotnet build src/Hosting/LISSTech.EntitySync.Hosting.csproj --configuration Release` passed with 0 warnings and 0 errors (`artifact://1858` for hosting; adapter output captured in the same parallel verification).

## Durable approved-parent evidence fix round

- RED: the focused command failed against the wished-for contract because parent resolution still accepted an entire mutable source entity, approval evidence held only three canonical identity fields, the durable plan/operation schemas did not persist it, request hashing omitted typed Address and parent evidence, and legacy mappers could silently ignore a non-null parent (`artifact://1872`).
- Parent resolution now accepts only an explicit source vendor, source platform-instance connection ID, parent entity type, and parent external ID. It matches the exact platform instance and records immutable approval evidence: canonical Client/Site UUIDs, parent kind, platform instance, linked external ID/status, a deterministic link token, and the observed owner version. Inactive/deleted/merge-donor owners and missing, stale, wrong-instance, or duplicate links hold visibly before plan approval.
- Migration `015_persist_resolved_target_parent` adds constrained JSONB evidence to both `sync_plan_items` and `sync_operation_items`. Plan creation, operation creation, every operation item copy, PostgreSQL binary import/insert, reads, reconciliation, and request/plan digests preserve the same evidence. `EntityWriteRequestDigest` schema version 2 includes typed Address content and all approved parent evidence, preventing changed-only checkpoints from treating either change as unchanged.
- The worker performs one live explicit resolution through the generation-pinned target lease immediately before mapping, desired-state hashing, and dispatch. It compares every evidence field against the approved persisted value. Missing/stale/ambiguous resolution preserves the resolver safe code; resolved evidence drift fails with `ORCHESTRA_PARENT_EVIDENCE_CHANGED`; a legacy mapper that cannot consume the approved evidence fails with `ENTITY_WRITE_PARENT_MAPPING_UNSUPPORTED`. These paths complete before the dispatch boundary and send no write.
- Final focused GREEN: `dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --configuration Release --filter FullyQualifiedName~OrchestraEntityAdapterTests` passed 39/39 with 0 failures and 0 skips (`artifact://1896`). This includes exact platform-instance/active-owner resolution, changed-only Address/parent digest coverage, and live worker re-resolution proving version drift and inactive-owner safe codes with GET-only traffic.
- Live PostgreSQL GREEN: the complete migration set through 015 applied twice idempotently (`artifact://1909`); focused plan and operation JSONB insert/read hydration tests passed 2/2 (`artifact://1911`).
- Final minimal compile: Adapter and Hosting Release builds both passed with 0 warnings and 0 errors (`artifact://1904`; adapter output in the same parallel verification).
- Security review: persisted parent evidence contains canonical IDs, non-secret platform/link identity, status, version, and SHA-256 link token only. No credentials, access tokens, vendor bodies, or mutable source payloads are persisted. Drift exceptions and operation outcomes contain fixed safe codes only.

## Explicit platform-instance and live source-parent fix round

- RED: the focused compile failed against the requested contract because the durable connection model, in-memory runtime registration, Control API DTOs, and worker parent recheck had no typed platform-instance UUID or fresh-source parent input (`artifact://1925`). This proved the prior implementation could only substitute the arbitrary EntitySync connection key.
- Migration `016_connection_platform_instance_id` adds a nullable PostgreSQL `uuid`, rejects the empty UUID, and uniquely maps a non-null platform instance to one connection per tenant. Repository insert, update, list/get hydration, generation replacement, and the runtime lease definition preserve it. Existing connections remain valid with `NULL`; only nested Orchestra Site/Address creates require the value.
- Control API create/update inputs and responses, MCP connect/list inputs and outputs, server-managed connection configuration, local registration, and durable runtime metadata now expose the typed UUID. The factory accepts it only from a named account-scoped profile; API/MCP callers can supply the explicit typed connection value. Neither the connection ID, vendor, display name, nor process environment is used as a substitute.
- The planner passes the source definition's configured UUID to authoritative link resolution and visibly holds a missing value as `Review` with `ORCHESTRA_SOURCE_PLATFORM_INSTANCE_UNCONFIGURED`. Tests use connection ID `ncentral-prod` and a distinct UUID; only the UUID resolves. The missing-configuration case performs the ordinary target listing but no parent-resolution request.
- Before target parent re-resolution, mapping, hashing, or write dispatch, the worker compares the freshly read source `ParentId` exactly and `ParentEntityType` case-insensitively with the approved linked external ID and canonical parent kind. Reparenting and semantic parent-kind drift fail with `ORCHESTRA_SOURCE_PARENT_CHANGED` and zero target HTTP; an unchanged parent proceeds with the generation-pinned UUID.
- GREEN: the final review-focused command passed all 43 Orchestra tests plus the local MCP factory-fallback/explicit-override precedence test, 44/44 (`artifact://1963`). Connection-definition and Control API tests passed 46/46 (`artifact://1947`). Live PostgreSQL migration application/idempotency passed 1/1 (`artifact://1941`), and a newly constructed repository rehydrated the exact UUID in the focused connection round trip, 1/1 (`artifact://1955`).
- Final minimal compile after review fixes: Adapter, Hosting, and MCP Release builds passed with 0 errors (`artifact://1961`).
- Security review: the new value is a non-secret UUID stored separately from encrypted configuration. API/MCP responses disclose only that registry identity, never credentials or vendor payloads. Missing or changed identity produces fixed safe codes; the worker performs no target dependency call after detecting source-parent drift.

## Final fix round 5 — connection-scoped platform identity

- RED: four focused tests produced three intentional failures while the source/target nullable-isolation case already passed. A process-global fallback attached one UUID to a generic vendor configuration, direct in-memory registration admitted `Guid.Empty`, and local MCP reported that invalid registration as successful (`artifact://1970`).
- The server-managed factory no longer reads any process-global or vendor-only platform-instance fallback. Only an account-scoped named profile's `EntitySyncPlatformInstanceId` is exported by the factory; explicit typed API/MCP input remains connection-scoped and takes precedence. Tests register NCentral with UUID X and an Orchestra target with `NULL` in the same tenant, prove generic vendor configuration remains `NULL`, prove profile X does not leak to the Orchestra target, and preserve explicit-over-profile precedence.
- `InMemoryEntityConnectionRepository.Register` rejects `Guid.Empty` before computing a key or entering its mutation lock. Local MCP disposes the unregistered adapter, returns a safe failure, and leaves the tenant with no connection generation.
- GREEN: the exact affected Orchestra, connection-definition, Control API, factory, and MCP filters passed 93/93 with no failures or skips after removal of the obsolete global setting (`artifact://1978`). The narrow RED cases passed 5/5 after implementation (`artifact://1972`).
- Final minimal compile: Adapter, Hosting, and MCP Release builds passed with 0 errors (`artifact://1976`).
- Security self-review: platform identity is non-secret but account-specific. Removing process-global inheritance prevents cross-connection misbinding and avoids accidental collisions with the tenant-scoped unique index. Empty UUID failure occurs before registration state changes and does not expose credentials or vendor payloads.

## Cross-repository Task 7 canonical audit-correlation fix

- Pre-migration incompatibility: durable operations stored operation/plan/idempotency only; neither an authoritative run UUID nor the original request correlation UUID existed, and operation items did not persist their plan ordinal. Migration `019_operation_audit_correlation` therefore adds nullable legacy-safe operation run/correlation UUIDs plus a required item index, backfills only the index from the authoritative plan item ordinal, and fails rather than fabricating missing identity for any replayable legacy operation.
- New operations persist distinct operation, plan, run, and correlation UUIDs. The application worker and unknown-outcome reconciler reconstruct the immutable typed tuple with the persisted item index. Retry/restart paths reuse it while the opaque idempotency key remains independent.
- Orchestra client/site/address create and update commands and platform-link PUT use one snake-case command-correlation shape (`operation_id`, `plan_id`, `run_id`, `item_index`) and the exact UUID `X-Correlation-ID` header. No `correlation_id` or `request_id` alias is serialized in the body.
- Verification: Orchestra adapter contract tests passed 43/43; durable operation/restart tests passed 14/14; migration application/idempotency passed 1/1; the full focused test project Release build passed with 0 errors.
- Security review: correlation values originate at the control boundary and are persisted before dispatch; no payload, secret, token, or raw vendor value was added to logs or storage. Invalid/missing replay identity fails closed.

## Canonical audit-correlation fix round 1 — migration fixtures

- Updated all post-019 raw-SQL operation fixtures with distinct run/correlation UUIDs and all operation-item fixtures with their authoritative plan ordinal. Existing tests now reach the constraints they were designed to verify; the production migration constraints were not weakened.
- GREEN: the complete `ControlPlaneMigrationTests` class passed 23/23, including application/idempotency and the seven formerly shadowed constraint cases. `DurableOperationTests` remained green at 14/14.

## Canonical audit-correlation fix round 2 — exact wire shape and immutable item index

- Removed the header-only correlation UUID from the command body DTO and consolidated all Orchestra entity and platform-link writes on the same explicit four-field wire type. The exact `X-Correlation-ID` remains sourced independently from the durable typed correlation, and `Idempotency-Key` is unchanged.
- Made the persisted plan item index a required operation-item constructor and rehydration argument. Queueing, worker checkpoint copies, reconciliation copies, and PostgreSQL hydration now preserve it explicitly rather than relying on a mutable initializer default.
- Local nondurable `Invoke-EntitySyncPlan` and `Invoke-EntitySyncChain` Orchestra apply paths reject before connection acquisition, mapping, or adapter dispatch with `ORCHESTRA_DURABLE_CONTROL_REQUIRED`; they do not invent operation/run/correlation identity. Durable PowerShell queueing remains the supported write surface.
- GREEN: Orchestra adapter contract tests passed 43/43; the nondurable guard contract passed 1/1; the complete Platform.Tests project compiled with 0 errors; exact Adapter, Runtime, Hosting, MCP, PowerShell module, and Scheduler Release builds passed. The migration fixture correction remained green at 23/23 and durable operation/restart coverage at 14/14 from fix round 1.
- Security self-review: body and header correlation cannot be conflated, replay identity is immutable and fail-closed, nondurable writes do not synthesize audit identity, and no payload, secret, token, or raw vendor value is logged or persisted.
