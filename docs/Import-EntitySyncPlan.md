---
external help file: LISSTech.EntitySync.dll-Help.xml
Module Name: LISSTech.EntitySync
online version:
schema: 2.0.0
---

# Import-EntitySyncPlan

## SYNOPSIS
Reloads a reviewed `.xlsx` workbook or JSON plan back into an executable `EntitySyncPlan`.

## SYNTAX

```powershell
Import-EntitySyncPlan [-Path] <String>
```

## DESCRIPTION
Reads a plan that was previously exported with `Export-EntitySyncPlan`, applies reviewer decisions, and emits a single `EntitySyncPlan` object ready for `Invoke-EntitySyncPlan` or `Invoke-EntitySyncChain`.

When `-Path` ends in `.xlsx`, the cmdlet reads the reviewer workbook, restores the full plan from the hidden sheets, then applies each reviewer decision to the corresponding `EntitySyncPlanItem`. When `-Path` ends in `.json`, the cmdlet deserializes the JSON file directly; the JSON shape is the same sanitized structure written by `Export-EntitySyncPlan -Path *.json`, so any tools that read or write that JSON can interoperate.

Import is the boundary where reviewer notes turn into plan state. `ReviewerNotes` are appended to the item's `Reasons`, so the operator's decision context carries through to `-WhatIf` and apply output. Plan items whose `Reasons` mention credential-shaped identifiers (`Authorization`, `BearerToken`, `Token`, `password_reset`, etc.) were already redacted to `[credential redacted]` during export, and the redaction is preserved on import; English reviewer phrases that incidentally contain sensitive words (`tokenization`, `password reset`, `secrets management`, etc.) survive the round-trip unchanged. Adapter-specific secrets such as the LTAC bearer token are not stored in plan artifacts, so they cannot leak back into apply.

## EXAMPLES

### Example 1
```powershell
$plan = Import-EntitySyncPlan .\netsuite-halo-client-plan.xlsx
$plan | Invoke-EntitySyncPlan -Apply -WhatIf
```

Loads the reviewed workbook and dry-runs the approved actions.

### Example 2
```powershell
$plan = Import-EntitySyncPlan .\plan.json
$plan.Items | Where-Object Action -eq 'Create' | Format-Table Source, Target, Score
```

Loads a JSON plan and lists every planned create. Use this when reviewing or diffing plans outside Excel.

## NOTES

Reviewer decisions are applied as follows:

| Decision | Resulting plan item |
|---|---|
| `Accept Planned` | Keeps the generated action and marks the item accepted. |
| `Reject` | Changes the item to `Action 'None'`; apply skips it as a rejection. |
| `Create` | Forces a target create. |
| `Link` | Forces a link/update of the target external/custom field. |
| `Update` | Forces an update of the matched target. |
| `No Update` | Changes the item to `Action 'None'`; apply skips it without treating it as a rejection. |
| `Review` | Keeps the item blocked from apply. |
| Blank | Imports as `Link` with the matched target and `MatchType 'ReviewerOverride'`. |

A changed `TargetName` cannot be combined with `Create`, `Reject`, or `No Update`. When multiple targets share the same name, import fails with an ambiguity error rather than guessing.
