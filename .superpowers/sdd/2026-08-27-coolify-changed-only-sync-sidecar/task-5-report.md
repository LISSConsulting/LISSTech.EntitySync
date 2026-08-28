# Task 5 Report: Shared Server Hosting Composition

## Status

Complete. The shared Hosting project, server-managed vendor factory, canonical change-state scope, startup migration gate, MCP clean cutover, focused regressions, and MCP Release build are implemented and committed.

## Moved and Added Symbols

- Moved `AgentControllerTokenProvider`, `AgentControllerProviderConfiguration`, and `AgentControllerTokenExchange` from `mcp/AgentControllerTokenProvider.cs` to `src/Hosting/AgentControllerTokenProvider.cs` under `LISSTech.EntitySync.Hosting`.
- Moved `LogfireLoggingSettings` and `LogfireLogging` from `mcp/LogfireLogging.cs` to `src/Hosting/LogfireLogging.cs`; their generic settings/configuration APIs are public for MCP and the scheduler.
- Added `IServerManagedEntityAdapterFactory` and `ServerManagedEntityAdapterFactory` with all five production branches: HaloPSA, NetSuite, NCentral, AgentController, and Bill.com.
- Added `EntitySyncHostingServiceCollectionExtensions.AddEntitySyncPlatform(string)` and `EntitySyncDatabaseMigrationHostedService`.
- Added `src/Hosting/LISSTech.EntitySync.Hosting.csproj` with Core, Ports, Application, Adapters, Mapping, Matching, and Runtime project references plus Npgsql, DI/Hosting abstractions, and the OpenTelemetry dependencies moved from MCP.
- Replaced MCP's temporary `EntitySyncPlatformComposition` and local adapter/configuration helpers with shared Hosting registrations and factory delegation.

## Compatibility and Security Decisions

- `ConnectionTools.ConnectVendor` still owns tenant admission, connection-ID generation, local DPAPI profile selection, registration, generation increments, and adapter disposal. It passes only the selected profile settings to the shared factory.
- Profile values retain precedence over the injected environment dictionary. AgentController continues to reject profile credentials and uses only server-managed configuration.
- Halo token acquisition, every vendor option/default, the AgentController Entra exchange and forced refresh callback, endpoint-change rejection, and sanitized failure messages moved without aliases or duplicate MCP helpers.
- The factory's public dictionary constructor supplies a deterministic, explicit environment source for tests and other hosts; the parameterless production constructor snapshots only documented server variables.
- `GetNetSuiteHaloChangeStateScope()` hashes exactly `netsuite|<trimmed uppercase account id>|customer|netsuite|halopsa|<normalized HTTPS base URL>|client|halopsa` as UTF-8 SHA-256 lowercase hexadecimal. Credential rotation does not affect the scope. Halo route identity rejects user info, query, and fragment components without echoing their contents.
- Shared DI registers both PostgreSQL repositories, `TimeProvider.System`, platform services, the shared factory, and `EntitySyncDatabaseMigrationHostedService`.
- MCP registers shared Hosting before either stdio or HTTP MCP transport. The migration hosted service therefore runs before the MCP hosted service and propagates migration failure out of host startup.
- MCP no longer owns Npgsql/OpenTelemetry packages or direct Adapter/Mapping/Matching project references. Adapters' internals visibility moved from the MCP assembly to Hosting.

## TDD Red Evidence

The factory/composition/scope tests and Hosting project reference were added before production code.

Command:

```bash
dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --filter "FullyQualifiedName~HostingFactory|FullyQualifiedName~RouteScope|FullyQualifiedName~HostingComposition" --no-restore
```

Exit code: `1`

Exact expected failure:

```text
warning MSB9008: The referenced project ../../src/Hosting/LISSTech.EntitySync.Hosting.csproj does not exist.
PlatformTests.cs(8,27): error CS0234: The type or namespace name 'Hosting' does not exist in the namespace 'LISSTech.EntitySync' (are you missing an assembly reference?)
```

The startup-failure test was mutation-checked by temporarily replacing `StartAsync` with a completed task.

```bash
DOTNET_ROLL_FORWARD=Major dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --filter FullyQualifiedName~StartupMigrationFailurePropagatesFromHostedService --no-restore
```

Exit code: `1`

Exact failure:

```text
Assert.ThrowsAny() Failure: No exception was thrown
Expected: typeof(System.Exception)
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1
```

The non-secret route identity validation was also verified red before implementation:

```bash
DOTNET_ROLL_FORWARD=Major dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --filter FullyQualifiedName~RouteScopeRejectsUrlComponentsThatCouldContainSecrets --no-restore
```

Exit code: `1`; all three user-info/query/fragment cases failed because no exception was yet thrown.

## Focused Green Evidence

Factory, scope, DI activation, startup migration, MCP delegation/reflection, AgentController exchange/refresh/validation, connection generations, and Logfire validation/tracing:

```bash
DOTNET_ROLL_FORWARD=Major dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --no-restore --filter "FullyQualifiedName~HostingFactory|FullyQualifiedName~RouteScope|FullyQualifiedName~HostingComposition|FullyQualifiedName~StartupMigrationFailurePropagatesFromHostedService|FullyQualifiedName~ConnectVendorDelegatesToSharedFactoryAndPreservesConnectionGenerations|FullyQualifiedName~McpConnectionToolDoesNotExposeEndpointsOrSecrets|FullyQualifiedName~AgentControllerProvider|FullyQualifiedName~AgentControllerConnection|FullyQualifiedName~AgentControllerEnvironmentValidation|FullyQualifiedName~ReplacingConnectionIncrementsGeneration|FullyQualifiedName~ReplacingLeasedConnection|FullyQualifiedName~ConnectionsArePartitioned|FullyQualifiedName~McpExposesInspectApproveAndApplyWorkflow|FullyQualifiedName~LogfireConfiguration|FullyQualifiedName~AspNetCoreRequestTracing"
```

Exit code: `0`

Exact output:

```text
Passed!  - Failed:     0, Passed:    34, Skipped:     0, Total:    34, Duration: 210 ms - LISSTech.EntitySync.Platform.Tests.dll (net8.0)
```

Exact requested MCP Release build:

```bash
dotnet build mcp/LISSTech.EntitySync.Mcp.csproj -c Release
```

Exit code: `0`

Exact summary:

```text
Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:01.77
```

No formatter, linter, broad validation, or project-wide test suite was run.

## Startup Smoke Evidence

The built stdio MCP executable was given a valid initialize request while `DATABASE_URL` pointed to an unreachable PostgreSQL port. It returned no JSON-RPC response, logged `Hosting failed to start`, propagated `NpgsqlException: Failed to connect to 127.0.0.1:1`, and exited `134`. This directly verifies that a migration failure prevents tool serving rather than degrading to an unmigrated host.

## Self-Review

- Re-read every Task 5 checkbox and checked the final implementation, focused tests, project ownership, and callsites against it.
- Confirmed factory branches and defaults match the removed MCP code, profile fallback remains local-only, AgentController refresh retains provider ownership, and errors never interpolate credentials or response bodies.
- Confirmed the canonical scope's literal SHA-256 value, case/whitespace/URL normalization, credential-rotation stability, account/base-URL sensitivity, lowercase format, and secret-bearing URL rejection.
- Confirmed `ConnectionTools` keeps admission/profile/generation behavior while delegating only adapter creation.
- Confirmed the migration hosted service directly returns `EntitySyncDatabaseMigrator.ApplyAsync`, and both MCP transports are registered after shared Hosting.
- Confirmed the old MCP composition, adapter helpers, token-provider path, Logfire path, package ownership, project references, and obsolete Adapters internals grant were removed rather than aliased.
- Confirmed `git diff --check` returned no output before the feature commit.
- Independent review initially identified migration/MCP hosted-service ordering as Important. The registration order was corrected in both transports; the reviewer re-read the current file and confirmed the finding resolved with no remaining Task 5 findings.

## Commit

- `721f807` — `refactor: share server vendor composition`

## Concerns

No identified correctness concern. The workstation has only the .NET 10 runtime while tests target `net8.0`, so focused test execution required `DOTNET_ROLL_FORWARD=Major`. The startup smoke intentionally used an unreachable database and therefore expected a nonzero exit.
