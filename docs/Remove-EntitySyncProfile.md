---
external help file: LISSTech.EntitySync.dll-Help.xml
Module Name: LISSTech.EntitySync
online version:
schema: 2.0.0
---

# Remove-EntitySyncProfile

## SYNOPSIS

Removes a saved EntitySync connection profile.

## SYNTAX

```powershell
Remove-EntitySyncProfile [-Name] <String> [-WhatIf] [-Confirm] [<CommonParameters>]
```

## DESCRIPTION

Deletes a named profile from the current user's EntitySync profile store. If the removed profile was the default profile, another saved profile is selected as default when one exists.

## EXAMPLES

```powershell
Remove-EntitySyncProfile old-profile
```
