---
external help file: LISSTech.EntitySync.dll-Help.xml
Module Name: LISSTech.EntitySync
online version:
schema: 2.0.0
---

# Get-EntitySyncTopLevel

## SYNOPSIS
Discovers vendor top-level records used to scope entity retrieval.

## SYNTAX

```powershell
Get-EntitySyncTopLevel -Vendor HaloPSA
```

## DESCRIPTION
Returns top-level records as objects. For HaloPSA, use the returned `Id` value with `Connect-EntitySyncVendor -HaloTopLevelId`.

## EXAMPLES

### Example 1
```powershell
Get-EntitySyncTopLevel -Vendor HaloPSA
```

Lists HaloPSA top-level IDs and names.
