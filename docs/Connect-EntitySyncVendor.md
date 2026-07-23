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
Connect-EntitySyncVendor -Vendor HaloPSA [-HaloBaseUrl <String>] [-HaloClientId <String>] [-HaloClientSecret <String>] [-HaloScope <String>] [-HaloTopLevelId <Int32>] [-HaloDefaultColour <String>] [-HaloNetSuiteCustomerIdField <String>] [-HaloNetSuiteCustomerIdFieldId <String>] [-HaloNetSuiteCustomerNameField <String>] [-HaloCustomerRelationshipId <Int32>] [-HaloCustomerRelationshipName <String>] [-HaloCustomerTypeId <Int32>] [-HaloCustomerTypeName <String>] [-HaloAccountManagerEmail <String>] [-HaloAccountManagerField <String>] [-HaloNCentralIntegrationId <Int32>]
Connect-EntitySyncVendor -Vendor NetSuite [-NetSuiteAccountId <String>] [-NetSuiteConsumerKey <String>] [-NetSuiteConsumerSecret <String>] [-NetSuiteTokenId <String>] [-NetSuiteTokenSecret <String>]
Connect-EntitySyncVendor -Vendor NCentral [-NCentralBaseUrl <String>] [-NCentralUserApiToken <String>] [-NCentralServiceOrgId <String>] [-NCentralSoapUsername <String>] [-NCentralSoapPassword <String>] [-NCentralSoapEndpointPath <String>] [-NCentralSoapNamespace <String>] [-NCentralHaloPsaIdPropertyLabel <String>] [-NCentralNetSuiteIdPropertyLabel <String>] [-NCentralNetSuiteNamePropertyLabel <String>]
Connect-EntitySyncVendor -Vendor AgentController [-Url <String>] [-Token <String>]
Connect-EntitySyncVendor -Vendor AgentController [-Url <String>] [-SecureToken <SecureString>]
Connect-EntitySyncVendor -Vendor AgentController -Session <Object>
Connect-EntitySyncVendor -Vendor AgentController -DeviceAssetOpsProfile <String>
Connect-EntitySyncVendor -Profile <String>
```

## DESCRIPTION
Connects a vendor adapter for later discovery, planning, and sync operations. Configuration may come from parameters or environment variables.

Add `-Profile <name> -SaveProfile` to a vendor connection command to save that vendor's resolved connection settings into a named local profile. Profiles are protected with Windows DPAPI for the current user. Later sessions can run `Connect-EntitySyncProfile <name>` or `Connect-EntitySyncVendor -Profile <name>` to connect every stored vendor in the profile.

AgentController is different: its bearer tokens are short-lived and are not saved. Save AgentController profiles with `-DeviceAssetOpsProfile <name>` so each profile connect can call `Connect-DeviceAssetOps -Profile <name>` and then mint a fresh session with `Get-DeviceAssetOpsAccessToken -AsSession`.

HaloPSA connections use client credentials. The cmdlet posts `grant_type=client_credentials`, `client_id`, `client_secret`, and `scope` to `auth/token`, falls back to `token` if needed, then uses the returned `access_token` for API calls.

NetSuite connections use standard REST Web Services SuiteQL. Pass the account ID only, such as `1234567` or `1234567_SB1`; the adapter derives the SuiteTalk REST host. The NetSuite role used by the token must have REST Web Services access and permissions to query customers with SuiteQL.

N-central connections use a User-API token for REST discovery and creation. The adapter exchanges it with `POST /api/auth/authenticate`, then uses the returned access token for customer discovery and creation. Set `NCENTRAL_SERVICE_ORG_ID` or `-NCentralServiceOrgId` because customer creation posts to `/api/service-orgs/{soId}/customers`.

N-central customer updates and organization custom-property writes use EI2 SOAP. Configure `-NCentralSoapUsername` and `-NCentralSoapPassword` for apply operations that update existing customers or maintain the `HaloPSA Client ID`, `NetSuite Customer ID`, and `NetSuite Customer Name` custom properties.

`Connect-EntitySyncVendor -Vendor AgentController -Session <Object>` consumes structured session data returned by `LISSTech.DeviceAssetOps` (`Get-DeviceAssetOpsAccessToken -AsSession`). The session supplies the AgentController ops/PostgREST base URL (`OpsBaseUrl`) and a SecureString operator JWT (`Token`). The generated client uses `POST /rpc/has_scope` for non-mutating connection validation and `POST /rpc/sync_ncentral_customers` for apply, both relative to that ops base URL. It does not call `/rest/rpc/...`, derive `ops-` from `api-`, or retry alternate paths after 404.

Manual break-glass mode remains available with `-Url <String> -Token <String>` or `-Url <String> -SecureToken <SecureString>`. In those parameter sets, `-Url` means the AgentController ops/PostgREST OpenAPI base URL, for example `https://ops-agent-controller.clfy-b.lissonline.com`, not the API/auth base URL `https://api-agent-controller.clfy-b.lissonline.com`. `-Vendor LTAC` is accepted and normalizes to `AgentController` in the returned connection. Tokens never appear in the returned connection object, `Get-EntitySyncConnection` output, exported plans, or common adapter error messages; Agent Controller HTTP failures report only the operation, HTTP status where available, and RPC path. See `specs/001-ltac-sync-adapter/contracts/powershell-command-contract.md`.

## EXAMPLES

### Example 1
```powershell
Connect-EntitySyncVendor -Vendor HaloPSA
Connect-EntitySyncVendor -Vendor NetSuite
Connect-EntitySyncVendor -Vendor NCentral
```

Connects adapters using environment variables.

Use `Get-EntitySyncLookup -Vendor HaloPSA -Type TopLevel` to discover values for `-HaloTopLevelId`, `Get-EntitySyncLookup -Vendor HaloPSA -Type NCentralIntegration` to discover values for `-HaloNCentralIntegrationId`, and `Get-EntitySyncLookup -Vendor NCentral -Type ServiceOrganization` to discover values for `-NCentralServiceOrgId`.

### Example 2
```powershell
Connect-EntitySyncVendor -Vendor AgentController -Url 'https://ops-agent-controller.clfy-b.lissonline.com' -Token $env:LTAC_BEARER_TOKEN
```

Connects the Agent Controller adapter in manual break-glass mode. `LTAC_BASE_URL`/`LTAC_BEARER_TOKEN` environment variables can be used instead of the parameters; `-Url` must point at the AgentController ops/PostgREST OpenAPI endpoint, not the API/auth host. The bearer token is used only for the Agent Controller authorization header and is not serialized into returned connection objects or plan artifacts.

### Example 3
```powershell
$session = Get-DeviceAssetOpsAccessToken -AsSession   # from LISSTech.DeviceAssetOps
Connect-EntitySyncVendor -Vendor AgentController -Session $session
```

Reuses an active `LISSTech.DeviceAssetOps` operator session without writing the cleartext token or manually copying endpoint URLs. EntitySync uses the session's `OpsBaseUrl` for the AgentController ops/PostgREST OpenAPI and sends `POST /rpc/sync_ncentral_customers` there. The SecureString is unwrapped in-process and used only for the Agent Controller authorization header. `-Session`, `-Token`, and `-SecureToken` are separate parameter sets; pass exactly one.

### Example 4
```powershell
Connect-EntitySyncVendor -Vendor NetSuite -Profile prod -SaveProfile -DefaultProfile
Connect-EntitySyncVendor -Vendor HaloPSA -Profile prod -SaveProfile
Connect-EntitySyncVendor -Vendor NCentral -Profile prod -SaveProfile
Connect-EntitySyncVendor -Vendor AgentController -DeviceAssetOpsProfile prod -Profile prod -SaveProfile

Connect-EntitySyncProfile prod
```

Saves a reusable profile during one-time setup, then reconnects all saved vendors from the profile in later sessions.
