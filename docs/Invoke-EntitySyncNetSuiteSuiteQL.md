---
external help file: LISSTech.EntitySync.dll-Help.xml
Module Name: LISSTech.EntitySync
online version:
schema: 2.0.0
---

# Invoke-EntitySyncNetSuiteSuiteQL

## SYNOPSIS
Executes a SuiteQL query against the active NetSuite connection.

## SYNTAX

```powershell
Invoke-EntitySyncNetSuiteSuiteQL -Query <String>
```

## DESCRIPTION
Runs the supplied SuiteQL through the registered `NetSuite` adapter and emits each result row as a `PSObject`. The cmdlet requires a connected NetSuite adapter; call `Connect-EntitySyncVendor -Vendor NetSuite` first.

The query is forwarded to NetSuite's REST Web Services SuiteQL endpoint, the same surface `Get-EntitySyncEntity -Vendor NetSuite` uses for customer discovery. Empty or whitespace-only queries are rejected before the HTTP call. Non-JSON responses (including HTML error pages that usually mean REST Web Services is disabled or the account host is wrong) raise `InvalidOperationException` with a redacted preview; an authentication failure or non-success status carries the same sanitized message instead of leaking the raw body.

Each SuiteQL row is converted into a `PSObject` whose `PSNoteProperty` names match the column names returned by NetSuite. NetSuite `T`/`F` booleans become `$true`/`$false`, integers become `Int64`, decimals become `Decimal`, strings stay strings, and unrecognized shapes fall back to the raw JSON text.

## EXAMPLES

### Example 1
```powershell
Connect-EntitySyncVendor -Vendor NetSuite
Invoke-EntitySyncNetSuiteSuiteQL -Query 'SELECT id, entityid, companyname FROM customer WHERE isinactive = ''F'' ORDER BY entityid FETCH FIRST 25 ROWS ONLY'
```

Lists up to 25 active NetSuite customers as `PSObject` rows.

### Example 2
```powershell
'SELECT id, tranid, status FROM transaction WHERE recordtype = ''invoice'' FETCH FIRST 10 ROWS ONLY' |
    Invoke-EntitySyncNetSuiteSuiteQL
```

Pipes a SuiteQL string into the cmdlet and returns one `PSObject` per invoice row.
