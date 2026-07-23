---
external help file: LISSTech.EntitySync.dll-Help.xml
Module Name: LISSTech.EntitySync
online version:
schema: 2.0.0
---

# Connect-EntitySyncProfile

## SYNOPSIS

Connects all vendors stored in a named EntitySync profile.

## SYNTAX

```powershell
Connect-EntitySyncProfile [[-Name] <String>] [<CommonParameters>]
```

## DESCRIPTION

Loads a saved profile from the current user's EntitySync profile store and connects every vendor entry in that profile. If `-Name` is omitted, the configured default profile is used.

Profiles are saved by passing `-Profile <name> -SaveProfile` to `Connect-EntitySyncVendor` during one-time setup. Stored settings are protected with Windows DPAPI for the current user. AgentController profiles store a DeviceAssetOps profile reference and mint a fresh short-lived token during connect; AgentController bearer tokens are not persisted.

## EXAMPLES

```powershell
Connect-EntitySyncProfile prod
```

Connects every vendor stored in the `prod` profile.

```powershell
Connect-EntitySyncProfile
```

Connects the default profile.
