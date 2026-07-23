---
external help file: LISSTech.EntitySync.dll-Help.xml
Module Name: LISSTech.EntitySync
online version:
schema: 2.0.0
---

# Set-EntitySyncDefaultProfile

## SYNOPSIS

Sets the default EntitySync connection profile.

## SYNTAX

```powershell
Set-EntitySyncDefaultProfile [-Name] <String> [<CommonParameters>]
```

## DESCRIPTION

Marks an existing profile as the default used by `Connect-EntitySyncProfile` when no profile name is supplied.

## EXAMPLES

```powershell
Set-EntitySyncDefaultProfile prod
```
