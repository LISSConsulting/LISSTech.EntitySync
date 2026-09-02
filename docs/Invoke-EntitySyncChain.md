---
external help file: LISSTech.EntitySync.dll-Help.xml
Module Name: LISSTech.EntitySync
online version:
schema: 2.0.0
---

# Invoke-EntitySyncChain

## SYNOPSIS

Creates review workbooks for a chained sync and applies only reviewed workbooks.

## DESCRIPTION

Creates one review workbook per sync edge during planning, then applies only reviewed workbooks when
`-ReviewedPlanPath` and `-Apply` are supplied. The default chain is `NetSuite -> HaloPSA -> NCentral`.

`-RootVendor` defaults to `NetSuite`, `-HubVendor` defaults to `HaloPSA`, and `-LeafVendors` defaults to `@('NCentral')`. `-Path` accepts a directory; one workbook is written per edge.

`-ReviewedPlanPath` accepts one or more `.xlsx` workbooks from a previous `Invoke-EntitySyncChain -Path` run. Items still marked `Review` are skipped.

## SYNTAX

### Plan parameter set (default)

```powershell
Invoke-EntitySyncChain [-Path] <String> [-RootVendor <String>] [-HubVendor <String>] [-LeafVendors <String[]>] [-IncludeInactive] [-CreateMissing] [-FullTargetObjects] [-AutoLinkScore <Int32>] [-ReviewScore <Int32>] [-SourceExternalIdName <String>] [-TargetCustomFieldName <String>] [-ThrottleLimit <Int32>] [-PassThru] [-WhatIf] [-Confirm] [<CommonParameters>]
```

### Apply parameter set

```powershell
Invoke-EntitySyncChain [-ReviewedPlanPath] <String[]> [-Apply] [-PassThru] [-SourceExternalIdName <String>] [-TargetCustomFieldName <String>] [-WhatIf] [-Confirm] [<CommonParameters>]
```

## EXAMPLES

### Example 1

```powershell
Invoke-EntitySyncChain -Path .\review -LeafVendors NCentral -CreateMissing -PassThru
```

Exports one `.xlsx` workbook per edge without writing to vendors.

### Example 2

```powershell
Invoke-EntitySyncChain -ReviewedPlanPath .\review\*.xlsx -Apply -WhatIf -PassThru
```

Shows the writes that would be applied for reviewed workbooks. Remove `-WhatIf` after reviewing the
planned writes. Items still marked `Review` are skipped and returned as failures when `-PassThru` is
used.

## NOTES

HaloPSA -> NCentral apply maintains both sides of the client relationship by setting N-central customer `externalId` to the HaloPSA client ID, updating configured N-central organization custom properties, and upserting HaloPSA `client_links` with `POST /api/ncentraldetails`. First-class HaloPSA Site -> NCentral Site plans create N-central sites through REST, update existing site fields through EI2 SOAP `customerModify`, and upsert HaloPSA `site_links`.
