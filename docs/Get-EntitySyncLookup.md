---
external help file: LISSTech.EntitySync.dll-Help.xml
Module Name: LISSTech.EntitySync
online version:
schema: 2.0.0
---

# Get-EntitySyncLookup

## SYNOPSIS
Returns vendor-specific lookup values used to configure sync connections.

## DESCRIPTION
Use this cmdlet to discover stable vendor IDs before connecting or applying sync workflows.

Supported lookups:

| Vendor | Type |
|---|---|
| `HaloPSA` | `TopLevel` |
| `HaloPSA` | `CustomerRelationship` |
| `HaloPSA` | `CustomerType` |
| `HaloPSA` | `NCentralIntegration` |
| `HaloPSA` | `NCentralIntegrationLink` |
| `NCentral` | `ServiceOrganization` |

## EXAMPLES

### Example 1
```powershell
Get-EntitySyncLookup -Vendor HaloPSA -Type TopLevel
```

Lists HaloPSA top-level IDs for `Connect-EntitySyncVendor -HaloTopLevelId`.

### Example 2
```powershell
Get-EntitySyncLookup -Vendor HaloPSA -Type CustomerRelationship
Get-EntitySyncLookup -Vendor HaloPSA -Type CustomerType
```

Lists HaloPSA customer relationship and customer type lookup values used when mapping NetSuite customers into HaloPSA clients.

### Example 3
```powershell
Get-EntitySyncLookup -Vendor NCentral -Type ServiceOrganization
```

Lists N-central service organization IDs for `Connect-EntitySyncVendor -NCentralServiceOrgId`.

### Example 4
```powershell
Get-EntitySyncLookup -Vendor HaloPSA -Type NCentralIntegration
```

Lists configured HaloPSA N-central integration accounts. Use the `Id` as `Connect-EntitySyncVendor -HaloNCentralIntegrationId` when there is more than one integration account.

### Example 5
```powershell
Get-EntitySyncLookup -Vendor HaloPSA -Type NCentralIntegrationLink
```

Lists HaloPSA's N-central client and site links. Client links are used as authoritative HaloPSA client to N-central customer matches and site links are used as authoritative HaloPSA site to N-central site matches. Both link types are upserted during matching apply flows after successful N-central writes.
