# Contract: PowerShell Operator Surface

## Connect-EntitySyncVendor

### LTAC Parameters

```powershell
Connect-EntitySyncVendor -Vendor LTAC -Url <string> -Token <string>
```

### Environment Fallbacks

```text
LTAC_BASE_URL
LTAC_BEARER_TOKEN
```

### Rules

- `-Vendor LTAC` registers an adapter whose public vendor name is `LTAC`.
- `LTAC` may be accepted as a connection alias only if generated plans still use `LTAC`.
- Returned connection objects must not expose `Token`.
- Missing or invalid connection information fails with a non-secret message.

## Get-EntitySyncEntity

### LTAC Entity Types

```powershell
Get-EntitySyncEntity -Vendor LTAC -Type Customer
```

### Rules

- `Customer` is the only LTAC entity type.
- Reads return active LTAC customer scopes when a read surface exists.
- If no read surface exists, reads may return an empty set so N-central sources plan as create/sync
  candidates.

## New-EntitySyncPlan

### Supported LTAC Target Flows

```powershell
New-EntitySyncPlan -SourceVendor NCentral -SourceEntityType Customer -TargetVendor LTAC -TargetEntityType Customer
New-EntitySyncPlan -SourceVendor NCentral -SourceEntityType Site -TargetVendor LTAC -TargetEntityType Customer
```

### Rules

- Planning is read-only.
- Planned items include source identifiers, derived slug evidence, parent customer evidence for
  sites, and safe-failure reasons.
- LTAC target vendor completion includes `LTAC`.
- LTAC target entity completion includes only `Customer`.

## Invoke-EntitySyncPlan

### LTAC Apply Rules

```powershell
$plan | Invoke-EntitySyncPlan -Apply -WhatIf
$plan | Invoke-EntitySyncPlan -Apply -PassThru
```

- Without `-Apply`, no LTAC writes occur.
- With `-WhatIf`, no LTAC writes occur and planned batch changes are reported.
- With `-Apply`, approved N-central customer/site items are sent as one LTAC batch.
- Review, reject, no-update, none, invalid, and incomplete items are skipped.
- Pass-through output reports batch success, inserted count, updated count, retired count,
  active count, audit event ID, and non-secret failure messages.

## Test-EntitySyncConnection

```powershell
Test-EntitySyncConnection -Vendor LTAC
```

- Returns successful connectivity without exposing the credential.

## Documentation and Manifest

- Public command help documents LTAC parameters, supported flows, safety behavior, and validation
  examples.
- Module load validation confirms exported commands remain available after build.
