# Contract: PowerShell Operator Surface

## Connect-EntitySyncVendor

### LCAT Parameters

```powershell
Connect-EntitySyncVendor -Vendor LCAT -LCATBaseUrl <string> -LCATBearerToken <string>
```

### Environment Fallbacks

```text
LCAT_BASE_URL
LCAT_BEARER_TOKEN
```

### Rules

- `-Vendor LCAT` registers an adapter whose public vendor name is `LCAT`.
- `LTAC` may be accepted as a connection alias only if generated plans still use `LCAT`.
- Returned connection objects must not expose `LCATBearerToken`.
- Missing or invalid connection information fails with a non-secret message.

## Get-EntitySyncEntity

### LCAT Entity Types

```powershell
Get-EntitySyncEntity -Vendor LCAT -Type Customer
```

### Rules

- `Customer` is the only LCAT entity type.
- Reads return active LCAT customer scopes when a read surface exists.
- If no read surface exists, reads may return an empty set so N-central sources plan as create/sync
  candidates.

## New-EntitySyncPlan

### Supported LCAT Target Flows

```powershell
New-EntitySyncPlan -SourceVendor NCentral -SourceEntityType Customer -TargetVendor LCAT -TargetEntityType Customer
New-EntitySyncPlan -SourceVendor NCentral -SourceEntityType Site -TargetVendor LCAT -TargetEntityType Customer
```

### Rules

- Planning is read-only.
- Planned items include source identifiers, derived slug evidence, parent customer evidence for
  sites, and safe-failure reasons.
- LCAT target vendor completion includes `LCAT`.
- LCAT target entity completion includes only `Customer`.

## Invoke-EntitySyncPlan

### LCAT Apply Rules

```powershell
$plan | Invoke-EntitySyncPlan -Apply -WhatIf
$plan | Invoke-EntitySyncPlan -Apply -PassThru
```

- Without `-Apply`, no LCAT writes occur.
- With `-WhatIf`, no LCAT writes occur and planned batch changes are reported.
- With `-Apply`, approved N-central customer/site items are sent as one LCAT batch.
- Review, reject, no-update, none, invalid, and incomplete items are skipped.
- Pass-through output reports batch success, inserted count, updated count, retired count,
  active count, audit event ID, and non-secret failure messages.

## Test-EntitySyncConnection

```powershell
Test-EntitySyncConnection -Vendor LCAT
```

- Returns successful connectivity without exposing the credential.

## Documentation and Manifest

- Public command help documents LCAT parameters, supported flows, safety behavior, and validation
  examples.
- Module load validation confirms exported commands remain available after build.
