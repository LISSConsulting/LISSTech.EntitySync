---
external help file: LISSTech.EntitySync.dll-Help.xml
Module Name: LISSTech.EntitySync
online version:
schema: 2.0.0
---

# Export-EntitySyncPlan

## SYNOPSIS
Persists a plan to a reviewer-friendly `.xlsx` workbook or fallback JSON.

## SYNTAX

```powershell
Export-EntitySyncPlan -Plan <EntitySyncPlan> [-Path] <String> [-PassThru]
```

## DESCRIPTION
Writes the supplied `EntitySyncPlan` so reviewers can examine and decide on every planned action before any vendor write happens.

`-Path` accepts either a full file path or an existing directory. When `-Path` resolves to a directory, the cmdlet generates a timestamped workbook name shaped like `EntitySync-NetSuite-Customer-to-HaloPSA-Client-20260625-115250.xlsx` (the timestamp reflects `DateTimeOffset.Now` at export time, not the plan's `CreatedAt`). When `-Path` has no extension, the workbook variant is used. When `-Path` ends in `.xlsx`, the cmdlet writes a reviewer workbook with a `Review` sheet, a hidden sheet pair preserving the full plan, a target sheet, and a legend sheet. When `-Path` ends in `.json`, the cmdlet writes a sanitized JSON copy of the plan as UTF-8 without a byte-order mark.

`-FilePath` is an alias for `-Path` for callers that prefer explicit file-path naming. Use `-PassThru` to emit a `FileInfo` for the created workbook so the caller can pass it to another cmdlet.

`Export-EntitySyncPlan` redacts sensitive entries before the artifact is written. Plan items whose `Reasons` or `ExternalIds`/`CustomFields` keys mention credential-shaped identifiers (`Authorization`, `BearerToken`, `Token`, `NCentralRegistrationToken`, `password_reset`, etc.) are rewritten or removed. The matcher is identifier-aware so English reviewer phrases that incidentally contain sensitive words — `tokenization policy review`, `password reset pending`, `secrets management policy`, `bearer of good news`, `reauthorization`, `credentialing` — are preserved. When a `Reason` is redacted it becomes `[credential redacted]`; redacted keys are removed entirely along with their values. Adapter-specific secrets are deliberately absent: the LTAC bearer token and N-central SOAP password never appear in workbook cells, JSON output, or the file name.

## EXAMPLES

### Example 1
```powershell
$plan | Export-EntitySyncPlan -Path .\netsuite-halo-client-plan.xlsx
```

Writes a reviewer workbook that mirrors `PowerShell API` flow's reviewer surface.

### Example 2
```powershell
$reviewWorkbook = $plan | Export-EntitySyncPlan -Path $HOME\Downloads -PassThru
```

Lets the cmdlet generate a timestamped workbook name under `~/Downloads` and returns the resulting `FileInfo` through the pipeline.

### Example 3
```powershell
$plan | Export-EntitySyncPlan -Path .\plan.json
```

Writes a sanitized JSON plan (UTF-8, no BOM) when `-Path` ends in `.json`. Reviewers cannot edit JSON, but operators can diff or archive the plan shape with version control.

## NOTES

Reviewers choose one `Decision` per row: `Accept Planned`, `Reject`, `Create`, `Link`, `Update`, `No Update`, or `Review`. A blank or `Review` cell imports as `Link` with the matched target, `MatchType 'ReviewerOverride'`, and an empty reviewer reason. A changed `TargetName` cannot be combined with `Create`, `Reject`, or `No Update`. When the source was matched to the wrong target, reviewers pick the correct existing target from the `TargetName` dropdown. Multiple targets with the same name fail import rather than guessing.
