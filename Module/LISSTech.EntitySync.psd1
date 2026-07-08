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
      Tags         = @('lisstech', 'sync', 'netsuite', 'halopsa', 'matching', 'integration')
      ProjectUri   = 'https://github.com/LISSConsulting/LISSTech.EntitySync'
      ReleaseNotes = 'Initial PowerShell 7 entity sync module with NetSuite and HaloPSA adapters.'
    }
  }
}
