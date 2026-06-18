---
external help file: LISSTech.EntitySync.dll-Help.xml
Module Name: LISSTech.EntitySync
online version:
schema: 2.0.0
---

# Invoke-EntitySyncPlan

## SYNOPSIS
Applies a reviewed EntitySync plan.

## DESCRIPTION
Applies create and link actions from a plan. Review items are skipped. The command requires -Apply for writes and supports -WhatIf and -Confirm.

## EXAMPLES

### Example 1
```powershell
$plan | Invoke-EntitySyncPlan -Apply -WhatIf
```

Shows the HaloPSA updates and creates that would be made.

### Example 2
```powershell
Import-EntitySyncPlan .\netsuite-halo-client-plan.json | Invoke-EntitySyncPlan -Apply -Confirm
```

Applies a previously reviewed plan with confirmation.
