---
external help file: LISSTech.EntitySync.dll-Help.xml
Module Name: LISSTech.EntitySync
online version:
schema: 2.0.0
---

# Get-EntitySyncConnection

## SYNOPSIS
Lists the vendor adapters currently registered for the session.

## SYNTAX

```powershell
Get-EntitySyncConnection
```

## DESCRIPTION
Returns one `EntitySyncConnection` object per vendor registered by `Connect-EntitySyncVendor` during the current PowerShell session. Each object exposes the registered adapter so scripts can verify which vendors are available before running discovery, planning, or apply steps.

`LTAC` connections registered through `Connect-EntitySyncVendor -Vendor LTAC` are reported back under the canonical `LCAT` vendor label (spec FR-002).

The credential fields passed to `Connect-EntitySyncVendor` are intentionally not exposed here. For example, the LCAT bearer token and N-central SOAP password are absent from the returned object, `Export-EntitySyncPlan` artifacts, and adapter error messages. Use `Connect-EntitySyncVendor` with environment-variable fallback to keep secrets out of transcripts.

## EXAMPLES

### Example 1
```powershell
Connect-EntitySyncVendor -Vendor NetSuite
Connect-EntitySyncVendor -Vendor HaloPSA
Get-EntitySyncConnection
```

Registers NetSuite and HaloPSA adapters using environment variables, then lists both registered connections.

### Example 2
```powershell
Get-EntitySyncConnection | Select-Object -ExpandProperty Vendor
```

Lists the vendor names of every registered adapter in alphabetical, case-insensitive order. Useful as a precondition check before `Test-EntitySyncConnection`.
