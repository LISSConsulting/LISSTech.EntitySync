---
external help file: LISSTech.EntitySync.dll-Help.xml
Module Name: LISSTech.EntitySync
online version:
schema: 2.0.0
---

# Test-EntitySyncConnection

## SYNOPSIS
Validates connectivity to a registered vendor adapter.

## SYNTAX

```powershell
Test-EntitySyncConnection -Vendor <String>
```

## DESCRIPTION
Calls `TestConnectionAsync` on the adapter registered for `-Vendor` and returns `$true` when the underlying credentialed call succeeds, otherwise it throws a terminating `ErrorCategory.ConnectionError`.

Run `Connect-EntitySyncVendor -Vendor <Name>` first; an unregistered vendor raises `No EntitySync connection exists for vendor '<Vendor>'. Run Connect-EntitySyncVendor first.`.

`-Vendor LTAC` is accepted as an alias and normalizes to `LCAT`, but every downstream result and error still identifies the vendor as `LCAT` (spec FR-002). For LCAT, connection failures return only the operation, HTTP status where available, and the `rpc/sync_ncentral_customers` path — the bearer token, authorization header, and raw response body are never copied into the error.

## EXAMPLES

### Example 1
```powershell
Connect-EntitySyncVendor -Vendor HaloPSA
Test-EntitySyncConnection -Vendor HaloPSA
```

Returns `$true` when the HaloPSA bearer-token call to `auth/token` succeeds.

### Example 2
```powershell
Connect-EntitySyncVendor -Vendor NetSuite
Test-EntitySyncConnection -Vendor NetSuite
```

Posts a `SELECT id FROM customer FETCH FIRST 1 ROWS ONLY` probe through REST Web Services and returns `$true` when NetSuite answers with a 2xx status.

### Example 3
```powershell
Connect-EntitySyncVendor -Vendor LCAT -LCATBaseUrl 'https://lcat.example.com' -LCATBearerToken $env:LCAT_BEARER_TOKEN
Test-EntitySyncConnection -Vendor LTAC
```

Tests the registered LCAT adapter. The alias normalizes to `LCAT` internally; any error returned identifies the vendor as `LCAT` without echoing the bearer token.
