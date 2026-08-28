# Final whole-branch review fix report

## Result

The scheduler now performs eager, synchronous validation of its fixed NetSuite-to-HaloPSA route configuration while building the host, before endpoints are mapped, the server listens, or hosted services execute.

Shared Hosting exposes `ValidateNetSuiteHaloFixedRouteConfiguration()` through `IServerManagedEntityAdapterFactory`. The implementation performs no network calls and reuses the factory's existing parsers and route identity rules:

- `CreateNetSuiteOptions(null)` for all five required NetSuite settings.
- `Resolve(...)` for required HaloPSA client ID and client secret.
- `CreateHaloOptions(...)` for the HaloPSA base URL and existing option parsing, using a local non-secret validation token.
- `GetNetSuiteHaloChangeStateScope()` for the fixed route's canonical identity validation.

`EntitySyncSchedulerHost.Build` resolves the shared factory and invokes this validation immediately after building the service provider and before logging or mapping `/health` and `/status`. Vendor authentication and connectivity checks remain in scheduled-run execution; a failed run remains observable through status without making `/health` unhealthy.

## Red evidence

1. Before the production change:

   `DOTNET_ROLL_FORWARD=Major dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --filter FullyQualifiedName~EntitySyncSchedulerHostTests --no-restore`

   Result: exit 1; **4 failed, 4 passed, 0 skipped, 8 total**. Every new missing/blank/malformed/non-HTTPS case failed with `Assert.Throws() Failure: No exception was thrown`, reproducing the startup-policy defect.

2. Before adding the shared API:

   `DOTNET_ROLL_FORWARD=Major dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --filter FullyQualifiedName~FixedRouteValidation --no-restore`

   Result: exit 1 with `CS1061` at the three new call sites because `ServerManagedEntityAdapterFactory` did not yet expose `ValidateNetSuiteHaloFixedRouteConfiguration`.

## Green evidence

1. Focused host/factory coverage:

   `DOTNET_ROLL_FORWARD=Major dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --filter "FullyQualifiedName~EntitySyncSchedulerHostTests|FullyQualifiedName~FixedRouteValidation" --no-restore`

   Result: exit 0; **19 passed, 0 failed, 0 skipped, 19 total**, duration 468 ms on `net8.0`.

   Coverage includes all eight required fixed-route variables blank, malformed and non-HTTPS HaloPSA URLs, valid fake credentials without network access, host-build rejection, valid dependency-graph host build, and health remaining healthy for a failed vendor run status.

2. Scheduler Release build:

   `DOTNET_ROLL_FORWARD=Major dotnet build scheduler/LISSTech.EntitySync.Scheduler.csproj -c Release --no-restore`

   Result: exit 0; **Build succeeded, 0 warnings, 0 errors**, elapsed 2.01 s.

No formatter, linter, broad test suite, deployment, push, or live vendor call was run.

## Self-review

- Scope is limited to the shared factory contract/implementation, scheduler startup invocation, focused tests, and this requested report.
- Validation has one source of truth: it calls the same `Resolve`, HTTPS, options, and route-scope parsing paths used during real adapter creation and scheduled runs; it introduces no parallel configuration convention.
- Validation is synchronous and no-network. The fake `.example.test` credentials reach successful host construction, demonstrating that startup validation does not request a HaloPSA access token.
- The call occurs before endpoint mapping and before top-level `RunAsync`, so invalid configuration cannot expose a healthy endpoint or start the scheduler worker.
- Runtime vendor connection behavior is unchanged; only configuration syntax/presence is promoted to startup validation.
- Required-setting errors name configuration keys without including configured secret values.
- Test doubles implement the new interface member as a no-op because their scheduler behavior tests supply adapters directly and do not model process configuration.

## Concerns

None identified within the approved scope. Full process start with valid configuration still requires a reachable PostgreSQL database for the existing migration hosted service, so the focused host test validates construction and endpoint composition rather than opening a listening socket.
