---
external help file: LISSTech.EntitySync.dll-Help.xml
Module Name: LISSTech.EntitySync
online version:
schema: 2.0.0
---

# Set-EntitySyncCustomProperty

## SYNOPSIS
Sets an existing vendor custom-property value.

## DESCRIPTION
Sets an existing custom-property value on a connected vendor entity. The command requires `-Apply` before writes are allowed and supports `-WhatIf` and `-Confirm`.

## SYNTAX

```powershell
Set-EntitySyncCustomProperty [-Vendor] <NCentral> [-EntityType] <Customer> [-Id] <String> [-Name] <String> [-Value] <String> [-Apply] [-WhatIf] [-Confirm] [<CommonParameters>]
```

`-Id` accepts the alias `-CustomerId`. `-Name` accepts the alias `-PropertyName`. `-Value` accepts empty strings. `-Vendor` is currently restricted to `NCentral` and `-EntityType` is currently restricted to `Customer`.

N-central support uses EI2 SOAP `organizationPropertyList` to resolve the property label to an ID, then `organizationPropertyModify` to update the value. It updates existing N-central organization custom properties; it does not create custom-property definitions.

Current support is limited to N-central customer organization properties.

## EXAMPLES

### Example 1
```powershell
Set-EntitySyncCustomProperty -Vendor NCentral -Id 390 -Name 'HaloPSA Client ID' -Value 684
```

Plans the custom-property update only. No vendor changes are made without `-Apply`.

### Example 2
```powershell
Set-EntitySyncCustomProperty -Vendor NCentral -CustomerId 390 -Name 'HaloPSA Client ID' -Value 684 -Apply -WhatIf
```

Shows the PowerShell WhatIf message for the N-central custom-property update.

### Example 3
```powershell
Set-EntitySyncCustomProperty -Vendor NCentral -CustomerId 390 -Name 'NetSuite Customer ID' -Value 12345 -Apply
```

Sets the `NetSuite Customer ID` custom property on N-central customer `390`.
