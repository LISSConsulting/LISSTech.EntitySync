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
Get-EntitySyncEntity -Vendor AgentController [-Type <String>] [-Search <String>] [-IncludeInactive] [-Count <Int32>]
```

## DESCRIPTION
Reads `ExternalEntity` records from the vendor adapter registered by `Connect-EntitySyncVendor`. `-Type` defaults to the connected vendor's supported entity type and completes only to values that vendor supports.

`Get-EntitySyncEntity` is fast by default and returns the vendor's list payload. Use `-FullObjects` only when per-client detail or site address enrichment is required for HaloPSA; that mode is intentionally slower and shows standard PowerShell progress.

`Get-EntitySyncEntity -Vendor AgentController -Type Customer` is the only supported Agent Controller read shape. `LTAC` is also accepted and normalizes to `AgentController`.

LTAC currently has no customer-scope list/read endpoint in the sync contract, so Customer reads return an empty set instead of calling the batch write endpoint or fabricating target candidates. This empty-target fallback is intentional: N-central Customer and Site sources still plan as create/sync candidates, and the reviewed plan is applied later through the authoritative LTAC batch path. If LTAC adds a read surface later, this command should return active LTAC customer scopes without changing the plan-first flow. See `specs/001-ltac-sync-adapter/contracts/powershell-command-contract.md`.

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

### Example 3
```powershell
Get-EntitySyncEntity -Vendor AgentController -Type Customer
```

Returns no records when the connected LTAC target has no customer-scope read surface. This keeps planning read-only while still allowing N-central sources to become LTAC customer-scope sync candidates.
