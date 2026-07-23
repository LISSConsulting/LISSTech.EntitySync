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
Calls `TestConnectionAsync` on the adapter registered for `-Vendor`. It returns `$true` when the credentialed probe succeeds, `$false` when a reachable service rejects the credential or required permission, and a terminating `ErrorCategory.ConnectionError` for transport failures.

Run `Connect-EntitySyncVendor -Vendor <Name>` first; an unregistered vendor raises `No EntitySync connection exists for vendor '<Vendor>'. Run Connect-EntitySyncVendor first.`.

`-Vendor LTAC` is accepted as an alias and normalizes to `AgentController`. The generated AgentController client calls the non-mutating `POST /rpc/has_scope` probe for `operator_access:write`; administrators also pass through AgentController's `has_scope` implementation. Invalid, expired, or insufficient tokens return `false`. Transport errors remain redacted and never include the bearer token, authorization header, or raw response body.

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
Connect-EntitySyncVendor -Vendor AgentController -Url 'https://ltac.example.com' -Token $env:LTAC_BEARER_TOKEN
Test-EntitySyncConnection -Vendor AgentController
```

Tests that the registered AgentController token may execute the authoritative sync operation without mutating customer scopes. Legacy aliases normalize to `AgentController`; errors do not echo the bearer token.
