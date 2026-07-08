---
external help file: LISSTech.EntitySync.dll-Help.xml
Module Name: LISSTech.EntitySync
online version:
schema: 2.0.0
---

# Get-EntitySyncEntity

## SYNOPSIS
Pulls canonical entities from a connected vendor adapter.

## SYNTAX

```powershell
Get-EntitySyncEntity -Vendor HaloPSA [-Type <String>] [-Search <String>] [-IncludeInactive] [-Count <Int32>] [-FullObjects] [-ThrottleLimit <Int32>]
Get-EntitySyncEntity -Vendor NetSuite [-Type <String>] [-Search <String>] [-IncludeInactive] [-Count <Int32>]
Get-EntitySyncEntity -Vendor NCentral [-Type <String>] [-Search <String>] [-IncludeInactive] [-Count <Int32>]
```

## DESCRIPTION
Reads `ExternalEntity` records from the vendor adapter registered by `Connect-EntitySyncVendor`. `-Type` defaults to the connected vendor's supported entity type and completes only to values that vendor supports.

`Get-EntitySyncEntity` is fast by default and returns the vendor's list payload. Use `-FullObjects` only when per-client detail or site address enrichment is required for HaloPSA; that mode is intentionally slower and shows standard PowerShell progress.

LCAT (planned): `Get-EntitySyncEntity -Vendor LCAT -Type Customer` will be the only LCAT entity read. `Customer` will be the only supported `-Type`. Reads will return active LCAT customer scopes when a read surface exists, or an empty set so N-central sources still plan as create/sync candidates. See `specs/001-lcat-sync-adapter/contracts/powershell-command-contract.md`.

## EXAMPLES

### Example 1
```powershell
Connect-EntitySyncVendor -Vendor HaloPSA
Get-EntitySyncEntity -Vendor HaloPSA -Type Client
```

Lists HaloPSA clients using the fast list payload.

### Example 2
```powershell
Get-EntitySyncEntity -Vendor NCentral -Type Site -IncludeInactive
```

Lists N-central sites, including inactive ones.
