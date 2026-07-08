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
Connect-EntitySyncVendor -Vendor HaloPSA [-HaloBaseUrl <String>] [-HaloClientId <String>] [-HaloClientSecret <String>] [-HaloScope <String>] [-HaloTopLevelId <Int32>] [-HaloDefaultColour <String>] [-HaloNetSuiteCustomerIdField <String>] [-HaloNetSuiteCustomerNameField <String>] [-HaloNCentralIntegrationId <Int32>]
Connect-EntitySyncVendor -Vendor NetSuite [-NetSuiteAccountId <String>] [-NetSuiteConsumerKey <String>] [-NetSuiteConsumerSecret <String>] [-NetSuiteTokenId <String>] [-NetSuiteTokenSecret <String>]
Connect-EntitySyncVendor -Vendor NCentral [-NCentralBaseUrl <String>] [-NCentralUserApiToken <String>] [-NCentralServiceOrgId <String>] [-NCentralSoapUsername <String>] [-NCentralSoapPassword <String>] [-NCentralSoapEndpointPath <String>] [-NCentralSoapNamespace <String>] [-NCentralHaloPsaIdPropertyLabel <String>] [-NCentralNetSuiteIdPropertyLabel <String>] [-NCentralNetSuiteNamePropertyLabel <String>]
```

## DESCRIPTION
Connects a vendor adapter for later discovery, planning, and sync operations. Configuration may come from parameters or environment variables.

HaloPSA connections use client credentials. The cmdlet posts `grant_type=client_credentials`, `client_id`, `client_secret`, and `scope` to `auth/token`, falls back to `token` if needed, then uses the returned `access_token` for API calls.

NetSuite connections use standard REST Web Services SuiteQL. Pass the account ID only, such as `1234567` or `1234567_SB1`; the adapter derives the SuiteTalk REST host. The NetSuite role used by the token must have REST Web Services access and permissions to query customers with SuiteQL.

N-central connections use a User-API token for REST discovery and creation. The adapter exchanges it with `POST /api/auth/authenticate`, then uses the returned access token for customer discovery and creation. Set `NCENTRAL_SERVICE_ORG_ID` or `-NCentralServiceOrgId` because customer creation posts to `/api/service-orgs/{soId}/customers`.

N-central customer updates and organization custom-property writes use EI2 SOAP. Configure `-NCentralSoapUsername` and `-NCentralSoapPassword` for apply operations that update existing customers or maintain the `HaloPSA Client ID`, `NetSuite Customer ID`, and `NetSuite Customer Name` custom properties.

## EXAMPLES

### Example 1
```powershell
Connect-EntitySyncVendor -Vendor HaloPSA
Connect-EntitySyncVendor -Vendor NetSuite
Connect-EntitySyncVendor -Vendor NCentral
```

Connects adapters using environment variables.

Use `Get-EntitySyncLookup -Vendor HaloPSA -Type TopLevel` to discover values for `-HaloTopLevelId`, `Get-EntitySyncLookup -Vendor HaloPSA -Type NCentralIntegration` to discover values for `-HaloNCentralIntegrationId`, and `Get-EntitySyncLookup -Vendor NCentral -Type ServiceOrganization` to discover values for `-NCentralServiceOrgId`.
