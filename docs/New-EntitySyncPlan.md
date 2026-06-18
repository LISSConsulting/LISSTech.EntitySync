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

## EXAMPLES

### Example 1
```powershell
$plan = New-EntitySyncPlan -SourceVendor NetSuite -SourceEntityType Customer -TargetVendor HaloPSA -TargetEntityType Client -CreateMissing
$plan.Items | Format-Table Action, Score, MatchType, @{n='Source';e={$_.Source.Name}}, @{n='Target';e={$_.Target.Name}}
```

Creates a NetSuite customer to HaloPSA client plan and displays decisions.
