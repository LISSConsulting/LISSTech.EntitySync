# External Help Build-Path Repair Design

## Problem

`just test` validates the generated external-help XML in both `en-US/` and legacy `Module/en-US/`. The repository no longer builds into `Module/`: `src/LISSTech.EntitySync.csproj` copies source help into `Build/Module/en-US/`, and the test suite imports `Build/Module/LISSTech.EntitySync.psd1`. `Module/en-US/` is ignored and absent by design, so the stale assertion fails even though the actual module artifact contains help.

## Decision

Keep `en-US/LISSTech.EntitySync.dll-Help.xml` as the committed source of truth. Update the Pester assertion to validate:

1. the committed source help under repository `en-US/`; and
2. the generated help beside the module under test, derived from the parent directory of `$script:ModulePath`.

Both copies must contain `LtacSourceInvalid` and must not contain the obsolete `LtacSiteParentMissing` value.

Do not recreate `Module/en-US/` or make the build write a second output tree. That would duplicate generated artifacts under an explicitly ignored legacy directory and reintroduce drift.

## Scope

Change only `Tests/LISSTech.EntitySync.Tests.ps1`. Module code, documentation content, build output paths, public commands, vendor behavior, scheduler behavior, database state, and synchronization policy remain unchanged.

## Verification

1. Reproduce the focused external-help test failure before editing.
2. Run the focused external-help test after editing.
3. Run `just test`, the platform test project, and `just test-load`.
4. Confirm the source and built help XML both exist and contain the current LTAC match type.

## Release

Commit the repair to `main` and push `origin/main`. Redeploy the configured Coolify Compose application as a full stack. Do not invoke a sync plan or mutate vendor/database entities. After deployment, verify the deployed revision, container health, MCP connectivity, scheduler status, and existing vendor connections.
