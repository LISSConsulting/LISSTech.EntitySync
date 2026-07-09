---
external help file: LISSTech.EntitySync.dll-Help.xml
Module Name: LISSTech.EntitySync
online version:
schema: 2.0.0
---

# New-EntitySyncPlan

## SYNOPSIS
Builds a safe, reviewable synchronization plan between two vendors.

## DESCRIPTION
Retrieves target entities, performs explainable fuzzy matching, and emits an EntitySync plan. This command does not write to any vendor.

When HaloPSA is the target, planning reads full client records by default so custom fields such as `CFNetSuiteCustomerID` are available for link detection. Pass `-FullTargetObjects` only when every HaloPSA client must also be enriched with site details for address/postal matching; this can be slower on large tenants.

## EXAMPLES

### Example 1
```powershell
$plan = New-EntitySyncPlan -SourceVendor NetSuite -SourceEntityType Customer -TargetVendor HaloPSA -TargetEntityType Client -CreateMissing
$plan.Items | Format-Table Action, Score, MatchType, @{n='Source';e={$_.Source.Name}}, @{n='Target';e={$_.Target.Name}}
```

Creates a NetSuite customer to HaloPSA client plan and displays decisions.

### Example 2
```powershell
$plan = New-EntitySyncPlan -SourceVendor NetSuite -SourceEntityType Customer -TargetVendor HaloPSA -TargetEntityType Client -FullTargetObjects
```

Uses exhaustive HaloPSA client and site enrichment for matching at the cost of additional API calls.

### Example 3
```powershell
$plan = New-EntitySyncPlan -SourceVendor HaloPSA -SourceEntityType Site -TargetVendor NCentral -TargetEntityType Site -CreateMissing
```

Creates a HaloPSA site to N-central site plan. HaloPSA N-central `site_links` are authoritative when present. If a Halo site has no existing site link, the parent Halo client must be linked to an N-central customer so new N-central sites can be created under the correct customer.

### Example 4
```powershell
$plan = New-EntitySyncPlan -SourceVendor NCentral -SourceEntityType Customer -TargetVendor LCAT -TargetEntityType Customer -CreateMissing
```

Creates an N-central Customer to LCAT Customer scope plan. `LCAT` is a valid `-TargetVendor` (`LTAC` is also accepted and normalizes to `LCAT`), and `Customer` is the only `-TargetEntityType` LCAT supports. LCAT has no customer-scope read endpoint, so it never returns target candidates; every source plans as `Create`/`NoMatch` with `-CreateMissing`. See `specs/001-lcat-sync-adapter/contracts/powershell-command-contract.md`.
