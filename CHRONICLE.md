> Project working memory for Ralph agents.
> Keep this file compact: unresolved state only, not a changelog. Completed history belongs in git, releases, and .ralph/logs/*.jsonl.

## Current Focus

- Active spec: `specs/001-lcat-sync-adapter/`.
- Spec tasks T001-T053 are complete. The current queue is empty; continue with the Empty Queue workflow unless a new spec task, blocker, or finding appears.
- Empty Queue sweep done: LCAT planning now blocks duplicate derived customer-scope slugs for review; `just test` passes.
- Empty Queue sweep done: LCAT adapter now rejects malformed customer-scope batch rows before HTTP send; `just test` passes.
- Empty Queue sweep done: LCAT apply and adapter paths now reject duplicate customer-scope slugs before HTTP send; `just test` passes.
- Empty Queue sweep done: LCAT apply and adapter paths now reject case-only duplicate N-central customer-scope IDs before HTTP send; `just test` passes.
- Empty Queue sweep done: LCAT planning now blocks non-NCentral source records before apply; `just test` passes.
- Empty Queue sweep done: LCAT adapter now trims customer-scope request values before validation and serialization, blocking whitespace-hidden duplicate N-central IDs before HTTP send; `just test` passes.
- Empty Queue sweep done: LCAT apply validation now compares trimmed approved customer-scope IDs/slugs before batch composition; `just test` passes.
- Empty Queue sweep done: LCAT slug fallback now sanitizes usable N-central IDs without allowing punctuation-only IDs through review safety; `just test` passes.
- Empty Queue sweep done: LCAT apply now returns a non-secret no-op result when a plan has no approved customer-scope items; `just build`, `just test-load`, and `just test` pass.
- Empty Queue sweep done: LCAT adapter now reports malformed successful batch responses as redacted path-scoped errors without echoing raw response bodies; `just test` passes.
- Empty Queue sweep done: LCAT adapter now treats successful non-object batch responses as malformed redacted errors instead of zero-count successes; `just test` passes.
- Empty Queue sweep done: LCAT adapter now treats present but non-numeric batch count fields as malformed redacted errors; `just build`, `just test-load`, and `just test` pass.
- Empty Queue sweep done: LCAT adapter now treats present but non-string audit event IDs as malformed redacted errors; `just build`, `just test-load`, and `just test` pass.
- Empty Queue sweep done: LCAT adapter now treats negative batch response counts as malformed instead of reporting impossible sync totals; `just build`, `just test-load`, and `just test` pass.
- Empty Queue sweep done: LCAT adapter now rejects null customer-scope batch rows before HTTP send; `just build`, `just test-load`, and `just test` pass.
- Empty Queue sweep done: LCAT adapter now rejects empty customer-scope batches before HTTP send; `just build`, `just test-load`, and `just test` pass.
- Empty Queue sweep done: LCAT apply now skips tampered approved plan items whose source is not an N-central Customer or Site before HTTP send; `just build`, `just test-load`, and `just test` pass.
- Empty Queue sweep done: LCAT adapter duplicate-slug coverage now includes whitespace-hidden duplicates before HTTP send; `just test` passes.
- Empty Queue sweep done: LCAT response parsing now explicitly covers explicit-null and empty-string `audit_event_id` per the contract's "preserved when present" rule; `just test` passes.
- Empty Queue sweep done: LCAT response parsing now explicitly covers non-string `audit_event_id` shapes (number, boolean, object, array) at the unit level, mirroring the secret-leak integration guard; `just test` passes.
- Empty Queue sweep done: LCAT `TestConnectionAsync` now has Pester coverage for both the redacted transport-failure path and the non-success status path, closing the last unverified credential-leak vector in the connection test surface; `just test` passes.
- Empty Queue sweep done: LCAT adapter and apply command now share the `DefaultEntityMapper.IsValidLcatSlug` contract validator instead of carrying duplicate `LcatSlugPattern` regex fields, with a Pester guard that exercises the central validator against contract boundary cases; `just build`, `just test-load`, and `just test` pass.
- Empty Queue sweep done: dead `ConnectionRegistry.Names()` helper removed (no callers in `src/`, `Tests/`, `Module/`, or `docs/`); `just build` and `just test` pass (117 tests).
- Empty Queue sweep done: README "PowerShell API" table now lists all 12 exported cmdlets including `Set-EntitySyncCustomProperty` and `Invoke-EntitySyncNetSuiteSuiteQL`, and `docs/Connect-EntitySyncVendor.md` SYNTAX for `-Vendor HaloPSA` now advertises the 7 HaloPSA parameters actually exposed in code (`HaloNetSuiteCustomerIdFieldId`, `HaloCustomerRelationshipId`, `HaloCustomerRelationshipName`, `HaloCustomerTypeId`, `HaloCustomerTypeName`, `HaloAccountManagerEmail`, `HaloAccountManagerField`); `just test` passes (117 tests).
- Empty Queue sweep done: external help now covers every exported cmdlet — `docs/Get-EntitySyncConnection.md`, `docs/Test-EntitySyncConnection.md`, `docs/Invoke-EntitySyncNetSuiteSuiteQL.md`, `docs/Export-EntitySyncPlan.md`, and `docs/Import-EntitySyncPlan.md` close the 5-doc gap called out as a follow-up in `Module/LISSTech.EntitySync.psd1:12-25` vs `docs/`; `just build`, `just test-load`, and `just test` pass (117 tests).
- Empty Queue sweep done: `EntitySyncPlanArtifactSanitizer.IsSensitiveName` now uses identifier-boundary matching (not preceded by lowercase letter, not followed by lowercase letter or whitespace, with an end-of-string exception for trailing CamelCase credential components) instead of substring `Contains`, so English-phrase reviewer notes such as "tokenization policy review", "password reset pending", "secrets management policy", "bearer of good news", "reauthorization", and "credentialing" no longer get rewritten to `[credential redacted]`, while `LCATBearerToken`, `Authorization`, `BearerToken`, `NCentralRegistrationToken`, `password_reset`, and `LCATBearerToken=...` reason leaks still get redacted; Pester coverage locks in both directions and `just test` passes (118 tests).
- Empty Queue sweep done: HaloPSA and N-central `TestConnectionAsync` now have Pester coverage for the success path, the non-success path, and the transport-failure path (the same three vectors as the existing LCAT coverage); the transport-failure tests prove the bearer token and `Authorization` header never leak into the raised exception's message, and the success/non-success tests prove the bearer/user-api token is still sent on the wire when the host is reachable; a new `MultiShotHttpServer` helper queues canned responses so the N-central two-request authenticate-then-validate flow can be exercised in one test, and `just test` passes (124 tests).
- Empty Queue sweep done: HaloPSA rate-limit/backoff now has Pester coverage — `SendWithRateLimitAsync` is exercised end-to-end with `Retry-After: 1` (one 429 then 200, proving the bearer token rides each retry) and with seven consecutive 429s (proving the adapter stops after `MaxRateLimitRetries` = 6 and returns the final 429 while keeping the bearer on every retry), and the private `RateLimitDelay` helper is exercised via reflection across all three branches (`Retry-After` delta-seconds, future-date, past-date falling through to exponential) plus the `Math.Min(300, 15 * 2^attempt)` ladder from attempts 0..7; the `OneShotHttpServer` and `MultiShotHttpServer.ResponseSpec` test fixtures gained an optional `ExtraHeaders` collection so Retry-After can be sent without hand-rolling a parallel response writer, and `just test` passes (129 tests, +7s suite budget for the 6s exhaustion test).

## Open Blockers

| Priority | Blocker | Evidence | Next Action |
|----------|---------|----------|-------------|

## Open Findings

| Priority | Finding | Evidence | Status |
|----------|---------|----------|--------|
| follow-up | `EntitySyncPlanArtifactSanitizer.IsSensitiveName` uses a substring `Contains` match (`authorization|bearer|credential|password|secret|token`) that silently rewrites legitimate reviewer notes such as "tokenization policy review" or "password reset pending" into `[credential redacted]`; affects reason strings plus `ExternalIds`/`CustomFields` keys | `src/Core/EntitySyncPlanArtifactSanitizer.cs:7-15,51-54` | Resolved by empty queue sweep above (matcher is now identifier-boundary aware) |
| follow-up | 5 exported cmdlets have no `docs/*.md` (no external help): `Get-EntitySyncConnection`, `Test-EntitySyncConnection`, `Invoke-EntitySyncNetSuiteSuiteQL`, `Export-EntitySyncPlan`, `Import-EntitySyncPlan` | `Module/LISSTech.EntitySync.psd1:12-25` vs `docs/` | Resolved by empty queue sweep above |
| follow-up | Halo/NetSuite/NCentral adapters have minimal direct Pester coverage for `TestConnectionAsync`, rate-limit/backoff, and lookups | largest source files; recent LCAT `TestConnectionAsync` work (commit `40244f3`) is unmirrored | Logged |

## Decisions To Preserve

-
