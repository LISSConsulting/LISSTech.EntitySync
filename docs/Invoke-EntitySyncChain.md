# Invoke-EntitySyncChain

Creates review workbooks for a chained sync and applies only reviewed workbooks.

## Plan

```powershell
Invoke-EntitySyncChain -Path .\review -LeafVendors NCentral -CreateMissing -PassThru
```

Default chain:

```text
NetSuite -> HaloPSA -> NCentral
```

The command exports one `.xlsx` workbook per edge. It does not write to vendors during planning.

## Apply

```powershell
Invoke-EntitySyncChain -ReviewedPlanPath .\review\*.xlsx -Apply -WhatIf -PassThru
```

Remove `-WhatIf` after reviewing the planned writes. Items still marked `Review` are skipped and returned as failures when `-PassThru` is used.

HaloPSA -> NCentral apply maintains both sides of the client relationship by setting N-central customer `externalId` to the HaloPSA client ID, updating configured N-central organization custom properties, and upserting HaloPSA `client_links` with `POST /api/ncentraldetails`. First-class HaloPSA Site -> NCentral Site plans can also create N-central sites and upsert HaloPSA `site_links`; N-central site field updates are no-op until a confirmed N-central site update endpoint is available.
