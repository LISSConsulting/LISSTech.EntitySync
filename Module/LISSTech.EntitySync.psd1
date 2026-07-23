@{
  RootModule           = 'LISSTech.EntitySync.dll'
  ModuleVersion        = '26.6.1'
  CompatiblePSEditions = @('Core')
  GUID                 = '6f7c17c1-7a36-4c7f-a5f7-197ffceec0ee'
  Author               = 'Marcin Wisniowski <mwisniowski@lisstech.com>'
  CompanyName          = 'LISS Consulting, Corp.'
  Copyright            = '(c) LISS Consulting, Corp. All rights reserved.'
  Description          = 'PowerShell 7 binary module for safe, repeatable vendor entity synchronization with explainable fuzzy matching.'
  PowerShellVersion    = '7.4'
  FunctionsToExport    = @()
  CmdletsToExport      = @(
    'Connect-EntitySyncVendor'
    'Connect-EntitySyncProfile'
    'Get-EntitySyncProfile'
    'Remove-EntitySyncProfile'
    'Set-EntitySyncDefaultProfile'
    'Get-EntitySyncConnection'
    'Test-EntitySyncConnection'
    'Get-EntitySyncLookup'
    'Get-EntitySyncEntity'
    'Invoke-EntitySyncNetSuiteSuiteQL'
    'New-EntitySyncPlan'
    'Invoke-EntitySyncPlan'
    'Invoke-EntitySyncChain'
    'Set-EntitySyncCustomProperty'
    'Export-EntitySyncPlan'
    'Import-EntitySyncPlan'
  )
  VariablesToExport    = '*'
  AliasesToExport      = @()
  FileList             = @(
    'LISSTech.EntitySync.dll'
    'en-US\about_LISSTech.EntitySync.help.txt'
  )
  PrivateData          = @{
    PSData = @{
      Tags         = @('lisstech', 'sync', 'netsuite', 'halopsa', 'ncentral', 'agentcontroller', 'ltac', 'ltac', 'matching', 'integration')
      ProjectUri   = 'https://github.com/LISSConsulting/LISSTech.EntitySync'
      ReleaseNotes = 'PowerShell 7 entity sync module with adapters for NetSuite, HaloPSA, N-central, and AgentController (LTAC). Plans are explainable and reviewer-friendly via Excel/JSON artifacts, and apply operations support -WhatIf/-Confirm. HaloPSA, N-central, and NetSuite honour 429 TooManyRequests with up to six retries (Retry-After delta-seconds or future-date, else Math.Min(300, 15 * 2^attempt)) backed by a 500ms inter-request throttle. Bearer tokens, OAuth secrets, registration tokens, and other credentials are redacted from plan artifacts, connection output, and adapter error paths; AgentController accepts the operator JWT either as -Token or as a -SecureToken SecureString. Named connection profiles can be saved locally with current-user Windows DPAPI protection and reused with Connect-EntitySyncProfile; AgentController profiles save a DeviceAssetOps profile reference and mint fresh short-lived tokens instead of persisting bearer tokens.'
    }
  }
}
