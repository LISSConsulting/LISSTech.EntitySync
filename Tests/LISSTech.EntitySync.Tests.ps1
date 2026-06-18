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
}
