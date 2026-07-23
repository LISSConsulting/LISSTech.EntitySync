# Quickstart: LTAC Customer Scope Sync

## Prerequisites

- PowerShell 7.4+ and .NET 8 SDK are installed.
- Pester is available for the current user.
- N-central connection variables are configured for the test environment.
- LTAC connection variables are configured for the test environment:
  - `LTAC_BASE_URL`
  - `LTAC_BEARER_TOKEN`
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

Expected outcome: Pester tests pass, including LTAC vendor completion, entity type validation,
mapping, credential redaction, batch apply, and safe-failure coverage.

## Complete Customer-Scope Snapshot Dry Run

```powershell
Import-Module .\Module\LISSTech.EntitySync.psd1 -Force

Connect-EntitySyncVendor -Vendor NCentral
Connect-EntitySyncVendor -Vendor LTAC

$plan = New-EntitySyncPlan `
  -SourceVendor NCentral `
  -SourceEntityType CustomerScope `
  -TargetVendor LTAC `
  -TargetEntityType Customer `
  -CreateMissing

$plan | Invoke-EntitySyncPlan -Apply -WhatIf -PassThru
```

Expected outcome: no LTAC customer scope state changes; output identifies one batch containing both
customer-derived and site-derived scopes. Site rows include parent N-central customer identifiers.

## Reviewed Apply Validation

```powershell
$plan | Invoke-EntitySyncPlan -Apply -PassThru
```

Expected outcome: approved N-central customer records are applied as one authoritative LTAC batch and
the result reports inserted, updated, retired, active, and audit information when supplied by LTAC.

## Safety Checks

- Inspect exported plan files and pass-through output for the LTAC credential; expected count is
  zero occurrences.
- Confirm Review, Reject, No Update, and invalid items block the whole apply before HTTP.
- Confirm unsafe slug, duplicate identifier, and missing parent identifier cases produce non-secret
  operator-readable reasons.

## Contract References

- [LTAC sync RPC contract](./contracts/ltac-sync-rpc.md)
- [PowerShell command contract](./contracts/powershell-command-contract.md)
- [Data model](./data-model.md)
