#Requires -Modules Pester

Describe 'LISSTech.EntitySync' {
  BeforeAll {
    $script:ModulePath = Join-Path $PSScriptRoot '..\Module\LISSTech.EntitySync.psd1'
    if (-not (Test-Path $script:ModulePath)) {
      throw "Module not found at $script:ModulePath. Run 'just build' first."
    }
    Import-Module $script:ModulePath -Force
  }

  AfterAll {
    Remove-Module LISSTech.EntitySync -Force -ErrorAction SilentlyContinue
  }

  It 'Imports successfully' {
    Get-Module LISSTech.EntitySync | Should -Not -BeNullOrEmpty
  }

  It 'Exports expected cmdlets' {
    $commands = Get-Command -Module LISSTech.EntitySync | Select-Object -ExpandProperty Name
    $commands | Should -Contain 'Connect-EntitySyncVendor'
    $commands | Should -Contain 'Get-EntitySyncConnection'
    $commands | Should -Contain 'Test-EntitySyncConnection'
    $commands | Should -Contain 'Get-EntitySyncTopLevel'
    $commands | Should -Contain 'Get-EntitySyncEntity'
    $commands | Should -Contain 'New-EntitySyncPlan'
    $commands | Should -Contain 'Invoke-EntitySyncPlan'
    $commands | Should -Contain 'Export-EntitySyncPlan'
    $commands | Should -Contain 'Import-EntitySyncPlan'
  }

  It 'Has an about topic' {
    $help = Get-Help about_LISSTech.EntitySync
    $help.Name | Should -Be 'about_LISSTech.EntitySync'
  }

  It 'Completes only vendor-specific entity types for Get-EntitySyncEntity' {
    $haloInput = 'Get-EntitySyncEntity -Vendor HaloPSA -Type '
    $haloTypes = [System.Management.Automation.CommandCompletion]::CompleteInput($haloInput, $haloInput.Length, $null).CompletionMatches.CompletionText
    $haloTypes | Should -Contain 'Client'
    $haloTypes | Should -Not -Contain 'Customer'

    $netSuiteInput = 'Get-EntitySyncEntity -Vendor NetSuite -Type '
    $netSuiteTypes = [System.Management.Automation.CommandCompletion]::CompleteInput($netSuiteInput, $netSuiteInput.Length, $null).CompletionMatches.CompletionText
    $netSuiteTypes | Should -Contain 'Customer'
    $netSuiteTypes | Should -Not -Contain 'Client'
  }

  It 'Completes only vendor-specific Connect-EntitySyncVendor parameters' {
    $haloInput = 'Connect-EntitySyncVendor -Vendor HaloPSA -'
    $halo = [System.Management.Automation.CommandCompletion]::CompleteInput($haloInput, $haloInput.Length, $null).CompletionMatches.CompletionText
    $halo | Should -Contain '-HaloBaseUrl'
    $halo | Should -Contain '-HaloClientId'
    $halo | Should -Not -Contain '-NetSuiteRestletUrl'

    $netSuiteInput = 'Connect-EntitySyncVendor -Vendor NetSuite -'
    $netSuite = [System.Management.Automation.CommandCompletion]::CompleteInput($netSuiteInput, $netSuiteInput.Length, $null).CompletionMatches.CompletionText
    $netSuite | Should -Contain '-NetSuiteRestletUrl'
    $netSuite | Should -Contain '-NetSuiteAccountId'
    $netSuite | Should -Not -Contain '-HaloBaseUrl'
  }

  It 'Declares object output for Get-EntitySyncConnection' {
    (Get-Command Get-EntitySyncConnection).OutputType.Type.Name | Should -Contain 'EntitySyncConnection'
  }

  It 'Normalizes legal suffixes from entity names' -ForEach @(
    @{ Name = 'Acme, Inc.'; Expected = 'acme' }
    @{ Name = 'The Acme Corporation'; Expected = 'acme' }
    @{ Name = 'Contoso Pty Ltd'; Expected = 'contoso' }
    @{ Name = 'Northwind GmbH & Co. KG'; Expected = 'northwind' }
    @{ Name = 'Fabrikam S. de R.L. de C.V.'; Expected = 'fabrikam' }
    @{ Name = 'Adventure Works Sp. z o.o.'; Expected = 'adventure works' }
    @{ Name = 'Tailspin S.A.R.L.'; Expected = 'tailspin' }
    @{ Name = 'Wingtip Sdn Bhd'; Expected = 'wingtip' }
  ) {
    [LISSTech.EntitySync.Core.EntityNormalizer]::NormalizeName($Name) | Should -Be $Expected
  }

  It 'Does not strip legal words from the middle of names' {
    [LISSTech.EntitySync.Core.EntityNormalizer]::NormalizeName('The Limited Company Shop LLC') | Should -Be 'limited company shop'
  }
}
