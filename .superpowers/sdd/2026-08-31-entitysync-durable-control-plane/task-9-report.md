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
