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

HaloPSA -> NCentral apply maintains both sides of the client relationship by setting N-central customer `externalId` to the HaloPSA client ID, updating configured N-central organization custom properties, and upserting HaloPSA `client_links` with `POST /api/ncentraldetails`. First-class HaloPSA Site -> NCentral Site plans can also create N-central sites and upsert HaloPSA `site_links`; N-central site field updates are no-op until a confirmed N-central site update endpoint is available.
