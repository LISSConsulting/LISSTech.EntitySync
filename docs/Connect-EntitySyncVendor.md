---
external help file: LISSTech.EntitySync.dll-Help.xml
Module Name: LISSTech.EntitySync
online version:
schema: 2.0.0
---

# Connect-EntitySyncVendor

## SYNOPSIS
Creates an in-session connection to a supported vendor adapter.

## SYNTAX

```powershell
Connect-EntitySyncVendor -Vendor HaloPSA [-HaloBaseUrl <String>] [-HaloAccessToken <String>] [-HaloTopLevelId <Int32>] [-HaloDefaultColour <String>] [-HaloNetSuiteCustomerIdField <String>]
Connect-EntitySyncVendor -Vendor NetSuite [-NetSuiteRestletUrl <String>] [-NetSuiteAccountId <String>] [-NetSuiteConsumerKey <String>] [-NetSuiteConsumerSecret <String>] [-NetSuiteTokenId <String>] [-NetSuiteTokenSecret <String>]
```

## DESCRIPTION
Connects a vendor adapter for later discovery, planning, and sync operations. Configuration may come from parameters or environment variables.

## EXAMPLES

### Example 1
```powershell
Connect-EntitySyncVendor -Vendor HaloPSA
Connect-EntitySyncVendor -Vendor NetSuite
```

Connects both adapters using environment variables.
