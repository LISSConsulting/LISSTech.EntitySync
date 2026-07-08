# Quickstart: LCAT Customer Scope Sync

## Prerequisites

- PowerShell 7.4+ and .NET 8 SDK are installed.
- Pester is available for the current user.
- N-central connection variables are configured for the test environment.
- LCAT connection variables are configured for the test environment:
  - `LCAT_BASE_URL`
  - `LCAT_BEARER_TOKEN`
- Use a non-production or approved validation tenant for first apply testing.

## Build and Load Validation

```powershell
just build
just test-load
```

Expected outcome: the module imports successfully and exported commands include the existing
EntitySync command surface.

## Automated Test Validation

```powershell
just test
```

Expected outcome: Pester tests pass, including LCAT vendor completion, entity type validation,
mapping, credential redaction, batch apply, and safe-failure coverage.

## Customer Plan Dry Run

```powershell
Import-Module .\Module\LISSTech.EntitySync.psd1 -Force

Connect-EntitySyncVendor -Vendor NCentral
Connect-EntitySyncVendor -Vendor LCAT

$plan = New-EntitySyncPlan `
  -SourceVendor NCentral `
  -SourceEntityType Customer `
  -TargetVendor LCAT `
  -TargetEntityType Customer `
  -CreateMissing

$plan | Invoke-EntitySyncPlan -Apply -WhatIf -PassThru
```

Expected outcome: no LCAT customer scope state changes; output identifies the approved customer
scope batch that would be sent.

## Site Plan Dry Run

```powershell
$sitePlan = New-EntitySyncPlan `
  -SourceVendor NCentral `
  -SourceEntityType Site `
  -TargetVendor LCAT `
  -TargetEntityType Customer `
  -CreateMissing

$sitePlan | Invoke-EntitySyncPlan -Apply -WhatIf -PassThru
```

Expected outcome: no LCAT customer scope state changes; site-derived scopes include parent N-central
customer identifiers or are blocked with clear non-secret reasons.

## Reviewed Apply Validation

```powershell
$plan | Invoke-EntitySyncPlan -Apply -PassThru
```

Expected outcome: approved N-central customer records are applied as one authoritative LCAT batch and
the result reports inserted, updated, retired, active, and audit information when supplied by LCAT.

## Safety Checks

- Inspect exported plan files and pass-through output for the LCAT credential; expected count is
  zero occurrences.
- Confirm Review, Reject, No Update, and invalid items are skipped.
- Confirm unsafe slug, duplicate identifier, and missing parent identifier cases produce non-secret
  operator-readable reasons.

## Contract References

- [LCAT sync RPC contract](./contracts/lcat-sync-rpc.md)
- [PowerShell command contract](./contracts/powershell-command-contract.md)
- [Data model](./data-model.md)
