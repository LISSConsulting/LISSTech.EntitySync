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
Connect-EntitySyncVendor -Vendor HaloPSA [-HaloBaseUrl <String>] [-HaloClientId <String>] [-HaloClientSecret <String>] [-HaloScope <String>] [-HaloTopLevelId <Int32>] [-HaloDefaultColour <String>] [-HaloNetSuiteCustomerIdField <String>]
Connect-EntitySyncVendor -Vendor NetSuite [-NetSuiteRestletUrl <String>] [-NetSuiteAccountId <String>] [-NetSuiteConsumerKey <String>] [-NetSuiteConsumerSecret <String>] [-NetSuiteTokenId <String>] [-NetSuiteTokenSecret <String>]
```

## DESCRIPTION
Connects a vendor adapter for later discovery, planning, and sync operations. Configuration may come from parameters or environment variables.

HaloPSA connections use client credentials. The cmdlet posts `grant_type=client_credentials`, `client_id`, `client_secret`, and `scope` to `auth/token`, falls back to `token` if needed, then uses the returned `access_token` for API calls.

NetSuite connections require the RESTlet external URL, not the SuiteTalk account host root. Use a URL shaped like `https://<account>.restlets.api.netsuite.com/app/site/hosting/restlet.nl?script=<script>&deploy=<deploy>`.

## EXAMPLES

### Example 1
```powershell
Connect-EntitySyncVendor -Vendor HaloPSA
Connect-EntitySyncVendor -Vendor NetSuite
```

Connects both adapters using environment variables.

Use `Get-EntitySyncTopLevel -Vendor HaloPSA` to discover values for `-HaloTopLevelId`.
