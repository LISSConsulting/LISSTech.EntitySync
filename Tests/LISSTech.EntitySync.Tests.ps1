#Requires -Modules Pester

Describe 'LISSTech.EntitySync' {
  BeforeAll {
    $script:ModulePath = Join-Path $PSScriptRoot '..\Module\LISSTech.EntitySync.psd1'
    if (-not (Test-Path $script:ModulePath)) {
      throw "Module not found at $script:ModulePath. Run 'just build' first."
    }
    Import-Module $script:ModulePath -Force

    if (-not ([System.Management.Automation.PSTypeName]'EntitySyncTests.OneShotHttpServer').Type) {
      Add-Type -TypeDefinition @'
namespace EntitySyncTests
{
    using System;
    using System.Net;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading.Tasks;

    public sealed class OneShotHttpServer : IDisposable
    {
        private readonly TcpListener listener;
        private readonly int statusCode;
        private readonly string reasonPhrase;
        private readonly string responseBody;
        private Task task;

        public OneShotHttpServer(int statusCode, string reasonPhrase, string responseBody)
        {
            this.listener = new TcpListener(IPAddress.Loopback, 0);
            this.statusCode = statusCode;
            this.reasonPhrase = reasonPhrase;
            this.responseBody = responseBody;
        }

        public string BaseUrl { get; private set; } = string.Empty;

        public string RequestText { get; private set; } = string.Empty;

        public void Start()
        {
            listener.Start();
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            BaseUrl = $"http://127.0.0.1:{endpoint.Port}/";
            task = Task.Run(ServeOnce);
        }

        public void Wait()
        {
            task?.GetAwaiter().GetResult();
        }

        private void ServeOnce()
        {
            using var client = listener.AcceptTcpClient();
            using var stream = client.GetStream();
            var buffer = new byte[8192];
            var builder = new StringBuilder();
            do
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                builder.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }
            while (!builder.ToString().Contains("\r\n\r\n", StringComparison.Ordinal));

            var request = builder.ToString();
            var headerEnd = request.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var contentLength = 0;
            if (headerEnd >= 0)
            {
                foreach (var line in request.Substring(0, headerEnd).Split(new[] { "\r\n" }, StringSplitOptions.None))
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        int.TryParse(line.Substring("Content-Length:".Length).Trim(), out contentLength);
                    }
                }
            }

            while (headerEnd >= 0 && Encoding.ASCII.GetByteCount(builder.ToString().Substring(headerEnd + 4)) < contentLength)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                builder.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            RequestText = builder.ToString();
            var bodyBytes = Encoding.UTF8.GetBytes(responseBody);
            var header =
                $"HTTP/1.1 {statusCode} {reasonPhrase}\r\n" +
                "Content-Type: application/json\r\n" +
                $"Content-Length: {bodyBytes.Length}\r\n" +
                "Connection: close\r\n\r\n";
            var headerBytes = Encoding.ASCII.GetBytes(header);
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Write(bodyBytes, 0, bodyBytes.Length);
        }

        public void Dispose()
        {
            listener.Stop();
        }
    }
}
'@
    }

    # These helpers build valid LCAT options/adapters so LCAT tests can override only the fields
    # they exercise instead of repeating adapter setup. Defined in BeforeAll (not directly in the
    # Describe body) so they are still in scope when Pester v5 invokes It blocks during the run
    # phase rather than only during discovery.
    function New-TestLCATOptions {
      param(
        [string]$BaseUrl = 'https://lcat.example.test/',
        [string]$BearerToken = 'token'
      )
      $options = [LISSTech.EntitySync.Adapters.LCAT.LCATOptions]::new()
      $options.BaseUrl = $BaseUrl
      $options.BearerToken = $BearerToken
      return $options
    }

    function New-TestLCATAdapter {
      param(
        [LISSTech.EntitySync.Adapters.LCAT.LCATOptions]$Options = (New-TestLCATOptions)
      )
      return [LISSTech.EntitySync.Adapters.LCAT.LCATEntityAdapter]::new($Options)
    }
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
    $commands | Should -Contain 'Get-EntitySyncLookup'
    $commands | Should -Contain 'Get-EntitySyncEntity'
    $commands | Should -Contain 'Invoke-EntitySyncNetSuiteSuiteQL'
    $commands | Should -Contain 'New-EntitySyncPlan'
    $commands | Should -Contain 'Invoke-EntitySyncPlan'
    $commands | Should -Contain 'Invoke-EntitySyncChain'
    $commands | Should -Contain 'Set-EntitySyncCustomProperty'
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
    $haloTypes | Should -Contain 'Site'
    $haloTypes | Should -Not -Contain 'Customer'

    $netSuiteInput = 'Get-EntitySyncEntity -Vendor NetSuite -Type '
    $netSuiteTypes = [System.Management.Automation.CommandCompletion]::CompleteInput($netSuiteInput, $netSuiteInput.Length, $null).CompletionMatches.CompletionText
    $netSuiteTypes | Should -Contain 'Customer'
    $netSuiteTypes | Should -Not -Contain 'Client'

    $nCentralInput = 'Get-EntitySyncEntity -Vendor NCentral -Type '
    $nCentralTypes = [System.Management.Automation.CommandCompletion]::CompleteInput($nCentralInput, $nCentralInput.Length, $null).CompletionMatches.CompletionText
    $nCentralTypes | Should -Contain 'Customer'
    $nCentralTypes | Should -Contain 'Site'
    $nCentralTypes | Should -Not -Contain 'Client'
  }

  It 'Completes vendors for New-EntitySyncPlan' {
    $sourceInput = 'New-EntitySyncPlan -SourceVendor '
    $sourceVendors = [System.Management.Automation.CommandCompletion]::CompleteInput($sourceInput, $sourceInput.Length, $null).CompletionMatches.CompletionText
    $sourceVendors | Should -Contain 'HaloPSA'
    $sourceVendors | Should -Contain 'NetSuite'
    $sourceVendors | Should -Contain 'NCentral'

    $targetInput = 'New-EntitySyncPlan -TargetVendor '
    $targetVendors = [System.Management.Automation.CommandCompletion]::CompleteInput($targetInput, $targetInput.Length, $null).CompletionMatches.CompletionText
    $targetVendors | Should -Contain 'HaloPSA'
    $targetVendors | Should -Contain 'NetSuite'
    $targetVendors | Should -Contain 'NCentral'
  }

  It 'Completes vendor-specific entity types for New-EntitySyncPlan' {
    $sourceInput = 'New-EntitySyncPlan -SourceVendor NetSuite -SourceEntityType '
    $sourceTypes = [System.Management.Automation.CommandCompletion]::CompleteInput($sourceInput, $sourceInput.Length, $null).CompletionMatches.CompletionText
    $sourceTypes | Should -Contain 'Customer'

    $targetInput = 'New-EntitySyncPlan -TargetVendor HaloPSA -TargetEntityType '
    $targetTypes = [System.Management.Automation.CommandCompletion]::CompleteInput($targetInput, $targetInput.Length, $null).CompletionMatches.CompletionText
    $targetTypes | Should -Contain 'Client'
    $targetTypes | Should -Contain 'Site'

    $nCentralTargetInput = 'New-EntitySyncPlan -TargetVendor NCentral -TargetEntityType '
    $nCentralTargetTypes = [System.Management.Automation.CommandCompletion]::CompleteInput($nCentralTargetInput, $nCentralTargetInput.Length, $null).CompletionMatches.CompletionText
    $nCentralTargetTypes | Should -Contain 'Customer'
    $nCentralTargetTypes | Should -Contain 'Site'
  }

  It 'Completes vendor-specific lookup types for Get-EntitySyncLookup' {
    $haloInput = 'Get-EntitySyncLookup -Vendor HaloPSA -Type '
    $haloTypes = [System.Management.Automation.CommandCompletion]::CompleteInput($haloInput, $haloInput.Length, $null).CompletionMatches.CompletionText
    $haloTypes | Should -Contain 'TopLevel'
    $haloTypes | Should -Contain 'CustomerRelationship'
    $haloTypes | Should -Contain 'CustomerType'
    $haloTypes | Should -Contain 'NCentralIntegration'
    $haloTypes | Should -Contain 'NCentralIntegrationLink'
    $haloTypes | Should -Not -Contain 'ServiceOrganization'

    $nCentralInput = 'Get-EntitySyncLookup -Vendor NCentral -Type '
    $nCentralTypes = [System.Management.Automation.CommandCompletion]::CompleteInput($nCentralInput, $nCentralInput.Length, $null).CompletionMatches.CompletionText
    $nCentralTypes | Should -Contain 'ServiceOrganization'
    $nCentralTypes | Should -Not -Contain 'TopLevel'

    $lcatParameterInput = 'Get-EntitySyncLookup -Vendor LCAT -'
    $lcatParameters = [System.Management.Automation.CommandCompletion]::CompleteInput($lcatParameterInput, $lcatParameterInput.Length, $null).CompletionMatches.CompletionText
    $lcatParameters | Should -Not -Contain '-Type'
  }

  It 'Exposes no lookup types for the LCAT target adapter and public lookup command' {
    $lookupTypes = [LISSTech.EntitySync.Core.EntitySyncLookupTypes]::ForVendor('LCAT')
    $lookupTypes | Should -BeNullOrEmpty

    $lcatAdapter = New-TestLCATAdapter
    try {
      $lcatAdapter.LookupTypes | Should -BeNullOrEmpty
    }
    finally {
      $lcatAdapter.Dispose()
    }

    @(Get-EntitySyncLookup -Vendor LCAT) | Should -BeNullOrEmpty
    @(Get-EntitySyncLookup -Vendor LTAC) | Should -BeNullOrEmpty
  }

  It 'Completes only vendor-specific Connect-EntitySyncVendor parameters' {
    $haloInput = 'Connect-EntitySyncVendor -Vendor HaloPSA -'
    $halo = [System.Management.Automation.CommandCompletion]::CompleteInput($haloInput, $haloInput.Length, $null).CompletionMatches.CompletionText
    $halo | Should -Contain '-HaloBaseUrl'
    $halo | Should -Contain '-HaloClientId'
    $halo | Should -Contain '-HaloNetSuiteCustomerIdFieldId'
    $halo | Should -Contain '-HaloAccountManagerEmail'
    $halo | Should -Contain '-HaloCustomerTypeName'
    $halo | Should -Not -Contain '-NetSuiteAccountId'
    $halo | Should -Contain '-HaloNCentralIntegrationId'

    $netSuiteInput = 'Connect-EntitySyncVendor -Vendor NetSuite -'
    $netSuite = [System.Management.Automation.CommandCompletion]::CompleteInput($netSuiteInput, $netSuiteInput.Length, $null).CompletionMatches.CompletionText
    $netSuite | Should -Contain '-NetSuiteAccountId'
    $netSuite | Should -Contain '-NetSuiteConsumerKey'
    $netSuite | Should -Not -Contain '-HaloBaseUrl'

    $nCentralInput = 'Connect-EntitySyncVendor -Vendor NCentral -'
    $nCentral = [System.Management.Automation.CommandCompletion]::CompleteInput($nCentralInput, $nCentralInput.Length, $null).CompletionMatches.CompletionText
    $nCentral | Should -Contain '-NCentralBaseUrl'
    $nCentral | Should -Contain '-NCentralUserApiToken'
    $nCentral | Should -Contain '-NCentralServiceOrgId'
    $nCentral | Should -Contain '-NCentralSoapUsername'
    $nCentral | Should -Contain '-NCentralSoapPassword'
    $nCentral | Should -Contain '-NCentralSoapEndpointPath'
    $nCentral | Should -Contain '-NCentralSoapNamespace'
    $nCentral | Should -Contain '-NCentralHaloPsaIdPropertyLabel'
    $nCentral | Should -Contain '-NCentralNetSuiteIdPropertyLabel'
    $nCentral | Should -Contain '-NCentralNetSuiteNamePropertyLabel'
    $nCentral | Should -Not -Contain '-HaloBaseUrl'
    $nCentral | Should -Not -Contain '-NetSuiteAccountId'
  }

  It 'Completes LCAT and LTAC as vendors on command surfaces that support LCAT (T013, US1)' {
    $getEntityInput = 'Get-EntitySyncEntity -Vendor '
    $getEntityVendors = [System.Management.Automation.CommandCompletion]::CompleteInput($getEntityInput, $getEntityInput.Length, $null).CompletionMatches.CompletionText
    $getEntityVendors | Should -Contain 'LCAT'
    $getEntityVendors | Should -Contain 'LTAC'

    $connectInput = 'Connect-EntitySyncVendor -Vendor '
    $connectVendors = [System.Management.Automation.CommandCompletion]::CompleteInput($connectInput, $connectInput.Length, $null).CompletionMatches.CompletionText
    $connectVendors | Should -Contain 'LCAT'
    $connectVendors | Should -Contain 'LTAC'

    $testConnectionInput = 'Test-EntitySyncConnection -Vendor '
    $testConnectionVendors = [System.Management.Automation.CommandCompletion]::CompleteInput($testConnectionInput, $testConnectionInput.Length, $null).CompletionMatches.CompletionText
    $testConnectionVendors | Should -Contain 'LCAT'
    $testConnectionVendors | Should -Contain 'LTAC'

    $targetInput = 'New-EntitySyncPlan -TargetVendor '
    $targetVendors = [System.Management.Automation.CommandCompletion]::CompleteInput($targetInput, $targetInput.Length, $null).CompletionMatches.CompletionText
    $targetVendors | Should -Contain 'LCAT'
    $targetVendors | Should -Contain 'LTAC'

    $sourceInput = 'New-EntitySyncPlan -SourceVendor '
    $sourceVendors = [System.Management.Automation.CommandCompletion]::CompleteInput($sourceInput, $sourceInput.Length, $null).CompletionMatches.CompletionText
    $sourceVendors | Should -Not -Contain 'LCAT'
    $sourceVendors | Should -Not -Contain 'LTAC'
  }

  It 'Completes only Customer as the LCAT/LTAC entity type (T013, US1)' {
    $getEntityInput = 'Get-EntitySyncEntity -Vendor LCAT -Type '
    $getEntityTypes = [System.Management.Automation.CommandCompletion]::CompleteInput($getEntityInput, $getEntityInput.Length, $null).CompletionMatches.CompletionText
    $getEntityTypes | Should -Be @('Customer')

    $getEntityLtacInput = 'Get-EntitySyncEntity -Vendor LTAC -Type '
    $getEntityLtacTypes = [System.Management.Automation.CommandCompletion]::CompleteInput($getEntityLtacInput, $getEntityLtacInput.Length, $null).CompletionMatches.CompletionText
    $getEntityLtacTypes | Should -Be @('Customer')

    $targetInput = 'New-EntitySyncPlan -TargetVendor LCAT -TargetEntityType '
    $targetTypes = [System.Management.Automation.CommandCompletion]::CompleteInput($targetInput, $targetInput.Length, $null).CompletionMatches.CompletionText
    $targetTypes | Should -Be @('Customer')

    $targetLtacInput = 'New-EntitySyncPlan -TargetVendor LTAC -TargetEntityType '
    $targetLtacTypes = [System.Management.Automation.CommandCompletion]::CompleteInput($targetLtacInput, $targetLtacInput.Length, $null).CompletionMatches.CompletionText
    $targetLtacTypes | Should -Be @('Customer')
  }

  It 'Returns an empty Customer read set for LCAT and LTAC when no LCAT read surface exists (T046, Polish)' {
    $connection = Connect-EntitySyncVendor -Vendor LCAT -LCATBaseUrl 'https://lcat.example.test' -LCATBearerToken 'token'
    $connection.Vendor | Should -Be 'LCAT'

    $lcatResults = Get-EntitySyncEntity -Vendor LCAT -Type Customer
    $ltacResults = Get-EntitySyncEntity -Vendor LTAC -Type Customer

    $lcatResults | Should -BeNullOrEmpty
    $ltacResults | Should -BeNullOrEmpty
  }

  It 'Tests registered LCAT connections through the adapter for LCAT and LTAC requests (T008 drift regression)' {
    foreach ($vendorName in @('LCAT', 'LTAC')) {
      $server = [EntitySyncTests.OneShotHttpServer]::new(204, 'No Content', '')
      $server.Start()
      $lcatAdapter = New-TestLCATAdapter -Options (New-TestLCATOptions -BaseUrl $server.BaseUrl -BearerToken "token-$vendorName")
      [LISSTech.EntitySync.Runtime.ConnectionRegistry]::Set($lcatAdapter)

      try {
        Test-EntitySyncConnection -Vendor $vendorName | Should -BeTrue
        $server.Wait()
        $server.RequestText | Should -Match '^GET / HTTP/1\.1'
      }
      finally {
        $server.Dispose()
        $lcatAdapter.Dispose()
      }
    }
  }

  It 'Maps NCentral Customer display name, N-central identifier, and a valid slug into LCAT Fields (T014, US1)' {
    $source = [LISSTech.EntitySync.Core.ExternalEntity]::new()
    $source.Vendor = 'NCentral'
    $source.EntityType = 'Customer'
    $source.Id = '111'
    $source.Name = 'Arista Air Conditioning Corp.'
    $source.ExternalIds['NCentralCustomerId'] = '111'

    $mapper = [LISSTech.EntitySync.Mapping.DefaultEntityMapper]::new()
    $request = $mapper.MapCreate($source, 'LCAT', 'Customer', [LISSTech.EntitySync.Core.MatchOptions]::new())

    $request.Vendor | Should -Be 'LCAT'
    $request.EntityType | Should -Be 'Customer'
    $request.Fields['display_name'] | Should -Be 'Arista Air Conditioning Corp.'
    $request.Fields['ncentral_customer_id'] | Should -Be '111'
    $request.Fields['slug'] | Should -Match '^[A-Za-z0-9][A-Za-z0-9_-]{0,62}[A-Za-z0-9]$'
    if ($request.Fields.ContainsKey('ncentral_parent_customer_id')) {
      $request.Fields['ncentral_parent_customer_id'] | Should -BeNullOrEmpty
    }
  }

  It 'Falls back to the N-central customer Id for ncentral_customer_id when NCentralCustomerId is absent (T014, US1)' {
    $source = [LISSTech.EntitySync.Core.ExternalEntity]::new()
    $source.Vendor = 'NCentral'
    $source.EntityType = 'Customer'
    $source.Id = '222'
    $source.Name = 'Fallback Metals LLC'

    $mapper = [LISSTech.EntitySync.Mapping.DefaultEntityMapper]::new()
    $request = $mapper.MapCreate($source, 'LCAT', 'Customer', [LISSTech.EntitySync.Core.MatchOptions]::new())

    $request.Fields['ncentral_customer_id'] | Should -Be '222'
  }

  It 'Derives a valid, deterministic LCAT slug from unsafe N-central customer names (T014, US1)' {
    $source = [LISSTech.EntitySync.Core.ExternalEntity]::new()
    $source.Vendor = 'NCentral'
    $source.EntityType = 'Customer'
    $source.Id = '333'
    $source.Name = 'A&W Networks, Inc. (East)'
    $source.ExternalIds['NCentralCustomerId'] = '333'

    $mapper = [LISSTech.EntitySync.Mapping.DefaultEntityMapper]::new()
    $requestA = $mapper.MapCreate($source, 'LCAT', 'Customer', [LISSTech.EntitySync.Core.MatchOptions]::new())
    $requestB = $mapper.MapCreate($source, 'LCAT', 'Customer', [LISSTech.EntitySync.Core.MatchOptions]::new())

    $requestA.Fields['slug'] | Should -Match '^[A-Za-z0-9][A-Za-z0-9_-]{0,62}[A-Za-z0-9]$'
    $requestA.Fields['slug'] | Should -Be $requestB.Fields['slug']
  }

  It 'Sanitizes the fallback source identifier when an LCAT slug cannot come from the display name' {
    $source = [LISSTech.EntitySync.Core.ExternalEntity]::new()
    $source.Vendor = 'NCentral'
    $source.EntityType = 'Customer'
    $source.Id = 'cust 12/34'
    $source.Name = '###'
    $source.ExternalIds['NCentralCustomerId'] = 'cust 12/34'

    $mapper = [LISSTech.EntitySync.Mapping.DefaultEntityMapper]::new()
    $request = $mapper.MapCreate($source, 'LCAT', 'Customer', [LISSTech.EntitySync.Core.MatchOptions]::new())

    $request.Fields['slug'] | Should -Be 'customer-cust-12-34'
    $request.Fields['slug'] | Should -Match '^[A-Za-z0-9][A-Za-z0-9_-]{0,62}[A-Za-z0-9]$'
  }

  It 'Maps NCentral Customer fields into LCAT on update, preserving the existing LCAT customer scope id (T014, US1)' {
    $source = [LISSTech.EntitySync.Core.ExternalEntity]::new()
    $source.Vendor = 'NCentral'
    $source.EntityType = 'Customer'
    $source.Id = '444'
    $source.Name = 'Northshore Plumbing'
    $source.ExternalIds['NCentralCustomerId'] = '444'

    $target = [LISSTech.EntitySync.Core.ExternalEntity]::new()
    $target.Vendor = 'LCAT'
    $target.EntityType = 'Customer'
    $target.Id = 'lcat-scope-99'
    $target.Name = 'Northshore Plumbing'

    $mapper = [LISSTech.EntitySync.Mapping.DefaultEntityMapper]::new()
    $request = $mapper.MapUpdate($source, $target, [LISSTech.EntitySync.Core.MatchOptions]::new())

    $request.Id | Should -Be 'lcat-scope-99'
    $request.Fields['display_name'] | Should -Be 'Northshore Plumbing'
    $request.Fields['ncentral_customer_id'] | Should -Be '444'
    $request.Fields['slug'] | Should -Match '^[A-Za-z0-9][A-Za-z0-9_-]{0,62}[A-Za-z0-9]$'
  }

  It 'Builds an LCAT batch sync request body matching the customer scope contract shape (T015, US1)' {
    $customerScope = [LISSTech.EntitySync.Adapters.LCAT.LCATCustomerScopeRequest]::new()
    $customerScope.Slug = 'Arista-Air-Conditioning'
    $customerScope.DisplayName = 'Arista Air Conditioning Corp.'
    $customerScope.NCentralCustomerId = '111'

    $siteScope = [LISSTech.EntitySync.Adapters.LCAT.LCATCustomerScopeRequest]::new()
    $siteScope.Slug = 'Arista-Air-Conditioning-Main-Office'
    $siteScope.DisplayName = 'Main Office'
    $siteScope.NCentralCustomerId = '9001'
    $siteScope.NCentralParentCustomerId = '111'

    $customers = [System.Collections.Generic.List[LISSTech.EntitySync.Adapters.LCAT.LCATCustomerScopeRequest]]::new()
    $customers.Add($customerScope)
    $customers.Add($siteScope)

    $method = [LISSTech.EntitySync.Adapters.LCAT.LCATEntityAdapter].GetMethod('BuildSyncRequestBody', [System.Reflection.BindingFlags]'NonPublic, Static')
    $bodyJson = $method.Invoke($null, @(, $customers))
    $body = $bodyJson | ConvertFrom-Json

    $body.customers.Count | Should -Be 2
    $body.customers[0].slug | Should -Be 'Arista-Air-Conditioning'
    $body.customers[0].display_name | Should -Be 'Arista Air Conditioning Corp.'
    $body.customers[0].ncentral_customer_id | Should -Be '111'
    $body.customers[0].ncentral_parent_customer_id | Should -BeNullOrEmpty
    $body.customers[1].slug | Should -Be 'Arista-Air-Conditioning-Main-Office'
    $body.customers[1].ncentral_customer_id | Should -Be '9001'
    $body.customers[1].ncentral_parent_customer_id | Should -Be '111'
    $body.reason | Should -Be 'EntitySync N-central to LCAT sync'
    $body.PSObject.Properties.Name | Should -Contain 'ticket'
    $body.ticket | Should -BeNullOrEmpty
  }

  It 'Parses inserted, updated, retired, and active counts and the audit event id from an LCAT batch sync response (T015, US1)' {
    $responseJson = '{"inserted_count":1,"updated_count":1,"retired_count":0,"active_count":2,"audit_event_id":"00000000-0000-0000-0000-000000000000"}'

    $method = [LISSTech.EntitySync.Adapters.LCAT.LCATEntityAdapter].GetMethod('ParseSyncResponse', [System.Reflection.BindingFlags]'NonPublic, Static')
    $result = $method.Invoke($null, @($responseJson))

    $result.InsertedCount | Should -Be 1
    $result.UpdatedCount | Should -Be 1
    $result.RetiredCount | Should -Be 0
    $result.ActiveCount | Should -Be 2
    $result.AuditEventId | Should -Be '00000000-0000-0000-0000-000000000000'
  }

  It 'Defaults LCAT batch sync response counts to zero and audit event id to null when absent (T015, US1)' {
    $responseJson = '{}'

    $method = [LISSTech.EntitySync.Adapters.LCAT.LCATEntityAdapter].GetMethod('ParseSyncResponse', [System.Reflection.BindingFlags]'NonPublic, Static')
    $result = $method.Invoke($null, @($responseJson))

    $result.InsertedCount | Should -Be 0
    $result.UpdatedCount | Should -Be 0
    $result.RetiredCount | Should -Be 0
    $result.ActiveCount | Should -Be 0
    $result.AuditEventId | Should -BeNullOrEmpty
  }

  It 'Creates an NCentral Customer to LCAT plan from pipeline sources without any LCAT vendor write (T016, US1)' {
    $ncOptions = [LISSTech.EntitySync.Adapters.NCentral.NCentralOptions]::new()
    $ncOptions.BaseUrl = 'https://ncentral.example.test/'
    $ncOptions.UserApiToken = 'token'
    $ncOptions.ServiceOrgId = '50'
    $ncAdapter = [LISSTech.EntitySync.Adapters.NCentral.NCentralEntityAdapter]::new($ncOptions)
    $lcatAdapter = New-TestLCATAdapter

    try {
      [LISSTech.EntitySync.Runtime.ConnectionRegistry]::Set($ncAdapter)
      [LISSTech.EntitySync.Runtime.ConnectionRegistry]::Set($lcatAdapter)

      $sourceOne = [LISSTech.EntitySync.Core.ExternalEntity]::new()
      $sourceOne.Vendor = 'NCentral'
      $sourceOne.EntityType = 'Customer'
      $sourceOne.Id = '111'
      $sourceOne.Name = 'Arista Air Conditioning Corp.'
      $sourceOne.ExternalIds['NCentralCustomerId'] = '111'

      $sourceTwo = [LISSTech.EntitySync.Core.ExternalEntity]::new()
      $sourceTwo.Vendor = 'NCentral'
      $sourceTwo.EntityType = 'Customer'
      $sourceTwo.Id = '222'
      $sourceTwo.Name = 'Fallback Metals LLC'
      $sourceTwo.ExternalIds['NCentralCustomerId'] = '222'

      $plan = @($sourceOne, $sourceTwo) | New-EntitySyncPlan -SourceVendor NCentral -TargetVendor LCAT -TargetEntityType Customer -CreateMissing

      $plan.SourceVendor | Should -Be 'NCentral'
      $plan.SourceEntityType | Should -Be 'Customer'
      $plan.TargetVendor | Should -Be 'LCAT'
      $plan.TargetEntityType | Should -Be 'Customer'
      $plan.TargetCandidates.Count | Should -Be 0
      $plan.Items.Count | Should -Be 2
      foreach ($item in $plan.Items) {
        $item.Action | Should -Be 'Create'
        $item.MatchType | Should -Be 'NoMatch'
        $item.Target | Should -BeNullOrEmpty
        $item.Reasons -join '; ' | Should -Match 'No target candidate found'
      }
    }
    finally {
      $ncAdapter.Dispose()
      $lcatAdapter.Dispose()
    }
  }

  It 'Normalizes the LTAC target alias to LCAT throughout a plan created from NCentral Customer sources (T016, US1)' {
    $ncOptions = [LISSTech.EntitySync.Adapters.NCentral.NCentralOptions]::new()
    $ncOptions.BaseUrl = 'https://ncentral.example.test/'
    $ncOptions.UserApiToken = 'token'
    $ncOptions.ServiceOrgId = '50'
    $ncAdapter = [LISSTech.EntitySync.Adapters.NCentral.NCentralEntityAdapter]::new($ncOptions)
    $lcatAdapter = New-TestLCATAdapter

    try {
      [LISSTech.EntitySync.Runtime.ConnectionRegistry]::Set($ncAdapter)
      [LISSTech.EntitySync.Runtime.ConnectionRegistry]::Set($lcatAdapter)

      $source = [LISSTech.EntitySync.Core.ExternalEntity]::new()
      $source.Vendor = 'NCentral'
      $source.EntityType = 'Customer'
      $source.Id = '333'
      $source.Name = 'Beacon Hill Facilities LLC'
      $source.ExternalIds['NCentralCustomerId'] = '333'

      $plan = @($source) | New-EntitySyncPlan -SourceVendor NCentral -TargetVendor LTAC -TargetEntityType Customer -CreateMissing

      $plan.TargetVendor | Should -Be 'LCAT'
      $plan.Items.Count | Should -Be 1
      $plan.Items[0].Action | Should -Be 'Create'
    }
    finally {
      $ncAdapter.Dispose()
      $lcatAdapter.Dispose()
    }
  }

  It 'Sends approved NCentral Customer to LCAT create items as one authoritative batch confirmation rather than one per item (T017, US1)' {
    $lcatAdapter = New-TestLCATAdapter
    [LISSTech.EntitySync.Runtime.ConnectionRegistry]::Set($lcatAdapter)

    $sourceOne = [LISSTech.EntitySync.Core.ExternalEntity]::new()
    $sourceOne.Vendor = 'NCentral'
    $sourceOne.EntityType = 'Customer'
    $sourceOne.Id = '111'
    $sourceOne.Name = 'Arista Air Conditioning Corp.'
    $sourceOne.ExternalIds['NCentralCustomerId'] = '111'

    $sourceTwo = [LISSTech.EntitySync.Core.ExternalEntity]::new()
    $sourceTwo.Vendor = 'NCentral'
    $sourceTwo.EntityType = 'Customer'
    $sourceTwo.Id = '222'
    $sourceTwo.Name = 'Fallback Metals LLC'
    $sourceTwo.ExternalIds['NCentralCustomerId'] = '222'

    $itemOne = [LISSTech.EntitySync.Core.EntitySyncPlanItem]::new()
    $itemOne.Action = 'Create'
    $itemOne.Source = $sourceOne
    $itemOne.MatchType = 'NoMatch'
    [void]$itemOne.Reasons.Add('No target candidate found')

    $itemTwo = [LISSTech.EntitySync.Core.EntitySyncPlanItem]::new()
    $itemTwo.Action = 'Create'
    $itemTwo.Source = $sourceTwo
    $itemTwo.MatchType = 'NoMatch'
    [void]$itemTwo.Reasons.Add('No target candidate found')

    $plan = [LISSTech.EntitySync.Core.EntitySyncPlan]::new()
    $plan.SourceVendor = 'NCentral'
    $plan.SourceEntityType = 'Customer'
    $plan.TargetVendor = 'LCAT'
    $plan.TargetEntityType = 'Customer'
    [void]$plan.Items.Add($itemOne)
    [void]$plan.Items.Add($itemTwo)

    $transcriptPath = Join-Path ([System.IO.Path]::GetTempPath()) ("entitysync-lcat-batch-whatif-{0}.txt" -f [guid]::NewGuid())
    try {
      Start-Transcript -Path $transcriptPath -Force | Out-Null
      $results = $null
      try {
        $results = Invoke-EntitySyncPlan -Plan $plan -Apply -WhatIf -PassThru
      }
      finally {
        Stop-Transcript | Out-Null
      }

      $confirmations = Get-Content -LiteralPath $transcriptPath | Where-Object { $_ -match 'What if:' }
      $confirmations.Count | Should -Be 1
      $confirmations | Should -Match 'LCAT'

      $results.Count | Should -Be 2
      $results.Success | Should -Not -Contain $false
      $results.Message | Should -Match 'LCAT batch sync preview'
      $results.Raw.NCentralCustomerId | Should -Contain '111'
      $results.Raw.NCentralCustomerId | Should -Contain '222'
    }
    finally {
      $lcatAdapter.Dispose()
      Remove-Item -LiteralPath $transcriptPath -Force -ErrorAction SilentlyContinue
    }
  }

  It 'Excludes a Review item from the LCAT batch confirmation while still reporting it separately (T017, US1)' {
    $lcatAdapter = New-TestLCATAdapter
    [LISSTech.EntitySync.Runtime.ConnectionRegistry]::Set($lcatAdapter)

    $sourceOne = [LISSTech.EntitySync.Core.ExternalEntity]::new()
    $sourceOne.Vendor = 'NCentral'
    $sourceOne.EntityType = 'Customer'
    $sourceOne.Id = '111'
    $sourceOne.Name = 'Arista Air Conditioning Corp.'
    $sourceOne.ExternalIds['NCentralCustomerId'] = '111'

    $sourceTwo = [LISSTech.EntitySync.Core.ExternalEntity]::new()
    $sourceTwo.Vendor = 'NCentral'
    $sourceTwo.EntityType = 'Customer'
    $sourceTwo.Id = '222'
    $sourceTwo.Name = 'Fallback Metals LLC'
    $sourceTwo.ExternalIds['NCentralCustomerId'] = '222'

    $sourceReview = [LISSTech.EntitySync.Core.ExternalEntity]::new()
    $sourceReview.Vendor = 'NCentral'
    $sourceReview.EntityType = 'Customer'
    $sourceReview.Id = '333'
    $sourceReview.Name = 'Ambiguous Match Co.'
    $sourceReview.ExternalIds['NCentralCustomerId'] = '333'

    $itemOne = [LISSTech.EntitySync.Core.EntitySyncPlanItem]::new()
    $itemOne.Action = 'Create'
    $itemOne.Source = $sourceOne
    $itemOne.MatchType = 'NoMatch'
    [void]$itemOne.Reasons.Add('No target candidate found')

    $itemTwo = [LISSTech.EntitySync.Core.EntitySyncPlanItem]::new()
    $itemTwo.Action = 'Create'
    $itemTwo.Source = $sourceTwo
    $itemTwo.MatchType = 'NoMatch'
    [void]$itemTwo.Reasons.Add('No target candidate found')

    $itemReview = [LISSTech.EntitySync.Core.EntitySyncPlanItem]::new()
    $itemReview.Action = 'Review'
    $itemReview.Source = $sourceReview
    $itemReview.MatchType = 'Fuzzy'
    [void]$itemReview.Reasons.Add('Multiple possible target candidates')

    $plan = [LISSTech.EntitySync.Core.EntitySyncPlan]::new()
    $plan.SourceVendor = 'NCentral'
    $plan.SourceEntityType = 'Customer'
    $plan.TargetVendor = 'LCAT'
    $plan.TargetEntityType = 'Customer'
    [void]$plan.Items.Add($itemOne)
    [void]$plan.Items.Add($itemTwo)
    [void]$plan.Items.Add($itemReview)

    $transcriptPath = Join-Path ([System.IO.Path]::GetTempPath()) ("entitysync-lcat-batch-review-{0}.txt" -f [guid]::NewGuid())
    try {
      Start-Transcript -Path $transcriptPath -Force | Out-Null
      $results = $null
      try {
        $results = Invoke-EntitySyncPlan -Plan $plan -Apply -WhatIf -PassThru
      }
      finally {
        Stop-Transcript | Out-Null
      }

      $confirmations = Get-Content -LiteralPath $transcriptPath | Where-Object { $_ -match 'What if:' }
      $confirmations.Count | Should -Be 1

      $reviewResults = @($results | Where-Object { $_.Action -eq 'Review' })
      $reviewResults.Count | Should -Be 1
      $reviewResults[0].Success | Should -BeFalse
    }
    finally {
      $lcatAdapter.Dispose()
      Remove-Item -LiteralPath $transcriptPath -Force -ErrorAction SilentlyContinue
    }
  }

  It 'Maps NCentral Site display name, site identifier, and parent N-central customer identifier into LCAT Fields (T026, US2)' {
    $source = [LISSTech.EntitySync.Core.ExternalEntity]::new()
    $source.Vendor = 'NCentral'
    $source.EntityType = 'Site'
    $source.Id = '555'
    $source.Name = 'Northshore Plumbing - Warehouse'
    $source.ExternalIds['NCentralSiteId'] = '555'
    $source.ExternalIds['NCentralCustomerId'] = '444'

    $mapper = [LISSTech.EntitySync.Mapping.DefaultEntityMapper]::new()
    $request = $mapper.MapCreate($source, 'LCAT', 'Customer', [LISSTech.EntitySync.Core.MatchOptions]::new())

    $request.Vendor | Should -Be 'LCAT'
    $request.EntityType | Should -Be 'Customer'
    $request.Fields['display_name'] | Should -Be 'Northshore Plumbing - Warehouse'
    $request.Fields['ncentral_customer_id'] | Should -Be '555'
    $request.Fields['ncentral_parent_customer_id'] | Should -Be '444'
    $request.Fields['slug'] | Should -Match '^[A-Za-z0-9][A-Za-z0-9_-]{0,62}[A-Za-z0-9]$'
  }

  It 'Falls back to the N-central site Id for ncentral_customer_id when NCentralSiteId is absent (T026, US2)' {
    $source = [LISSTech.EntitySync.Core.ExternalEntity]::new()
    $source.Vendor = 'NCentral'
    $source.EntityType = 'Site'
    $source.Id = '666'
    $source.Name = 'Fallback Metals LLC - Depot'
    $source.ExternalIds['NCentralCustomerId'] = '222'

    $mapper = [LISSTech.EntitySync.Mapping.DefaultEntityMapper]::new()
    $request = $mapper.MapCreate($source, 'LCAT', 'Customer', [LISSTech.EntitySync.Core.MatchOptions]::new())

    $request.Fields['ncentral_customer_id'] | Should -Be '666'
    $request.Fields['ncentral_parent_customer_id'] | Should -Be '222'
  }

  It 'Maps NCentral Site fields into LCAT on update, preserving the existing LCAT customer scope id and parent id (T026, US2)' {
    $source = [LISSTech.EntitySync.Core.ExternalEntity]::new()
    $source.Vendor = 'NCentral'
    $source.EntityType = 'Site'
    $source.Id = '777'
    $source.Name = 'Arista Air Conditioning Corp. - East'
    $source.ExternalIds['NCentralSiteId'] = '777'
    $source.ExternalIds['NCentralCustomerId'] = '111'

    $target = [LISSTech.EntitySync.Core.ExternalEntity]::new()
    $target.Vendor = 'LCAT'
    $target.EntityType = 'Customer'
    $target.Id = 'lcat-scope-42'
    $target.Name = 'Arista Air Conditioning Corp. - East'

    $mapper = [LISSTech.EntitySync.Mapping.DefaultEntityMapper]::new()
    $request = $mapper.MapUpdate($source, $target, [LISSTech.EntitySync.Core.MatchOptions]::new())

    $request.Id | Should -Be 'lcat-scope-42'
    $request.Fields['display_name'] | Should -Be 'Arista Air Conditioning Corp. - East'
    $request.Fields['ncentral_customer_id'] | Should -Be '777'
    $request.Fields['ncentral_parent_customer_id'] | Should -Be '111'
    $request.Fields['slug'] | Should -Match '^[A-Za-z0-9][A-Za-z0-9_-]{0,62}[A-Za-z0-9]$'
  }

  It 'Blocks an NCentral Site with no parent N-central customer identifier for review instead of creating an LCAT scope (T027, US2)' {
    $ncOptions = [LISSTech.EntitySync.Adapters.NCentral.NCentralOptions]::new()
    $ncOptions.BaseUrl = 'https://ncentral.example.test/'
    $ncOptions.UserApiToken = 'token'
    $ncOptions.ServiceOrgId = '50'
    $ncAdapter = [LISSTech.EntitySync.Adapters.NCentral.NCentralEntityAdapter]::new($ncOptions)
    $lcatAdapter = New-TestLCATAdapter

    try {
      [LISSTech.EntitySync.Runtime.ConnectionRegistry]::Set($ncAdapter)
      [LISSTech.EntitySync.Runtime.ConnectionRegistry]::Set($lcatAdapter)

      $orphanSite = [LISSTech.EntitySync.Core.ExternalEntity]::new()
      $orphanSite.Vendor = 'NCentral'
      $orphanSite.EntityType = 'Site'
      $orphanSite.Id = '888'
      $orphanSite.Name = 'Orphaned Site Without Parent'
      $orphanSite.ExternalIds['NCentralSiteId'] = '888'

      $plan = @($orphanSite) | New-EntitySyncPlan -SourceVendor NCentral -TargetVendor LCAT -TargetEntityType Customer -CreateMissing

      $plan.Items.Count | Should -Be 1
      $plan.Items[0].Action | Should -Be 'Review'
      $plan.Items[0].Target | Should -BeNullOrEmpty
      $plan.Items[0].Reasons -join '; ' | Should -Match 'parent N-central customer identifier'
    }
    finally {
      $ncAdapter.Dispose()
      $lcatAdapter.Dispose()
    }
  }

  It 'Still blocks an NCentral Site with no parent N-central customer identifier for review even without -CreateMissing (T027, US2)' {
    $ncOptions = [LISSTech.EntitySync.Adapters.NCentral.NCentralOptions]::new()
    $ncOptions.BaseUrl = 'https://ncentral.example.test/'
    $ncOptions.UserApiToken = 'token'
    $ncOptions.ServiceOrgId = '50'
    $ncAdapter = [LISSTech.EntitySync.Adapters.NCentral.NCentralEntityAdapter]::new($ncOptions)
    $lcatAdapter = New-TestLCATAdapter

    try {
      [LISSTech.EntitySync.Runtime.ConnectionRegistry]::Set($ncAdapter)
      [LISSTech.EntitySync.Runtime.ConnectionRegistry]::Set($lcatAdapter)

      $orphanSite = [LISSTech.EntitySync.Core.ExternalEntity]::new()
      $orphanSite.Vendor = 'NCentral'
      $orphanSite.EntityType = 'Site'
      $orphanSite.Id = '999'
      $orphanSite.Name = 'Another Orphaned Site'
      $orphanSite.ExternalIds['NCentralSiteId'] = '999'

      $plan = @($orphanSite) | New-EntitySyncPlan -SourceVendor NCentral -TargetVendor LCAT -TargetEntityType Customer

      $plan.Items.Count | Should -Be 1
      $plan.Items[0].Action | Should -Be 'Review'
      $plan.Items[0].Reasons -join '; ' | Should -Match 'parent N-central customer identifier'
    }
    finally {
      $ncAdapter.Dispose()
      $lcatAdapter.Dispose()
    }
  }

  It 'Derives different LCAT slugs for two N-central Sites with the same site name under different parent customers (T028, US2)' {
    $siteUnderParentOne = [LISSTech.EntitySync.Core.ExternalEntity]::new()
    $siteUnderParentOne.Vendor = 'NCentral'
    $siteUnderParentOne.EntityType = 'Site'
    $siteUnderParentOne.Id = '501'
    $siteUnderParentOne.Name = 'Main Office'
    $siteUnderParentOne.ExternalIds['NCentralSiteId'] = '501'
    $siteUnderParentOne.ExternalIds['NCentralCustomerId'] = '111'
    $siteUnderParentOne.CustomFields['NCentralCustomerName'] = 'Arista Air Conditioning'

    $siteUnderParentTwo = [LISSTech.EntitySync.Core.ExternalEntity]::new()
    $siteUnderParentTwo.Vendor = 'NCentral'
    $siteUnderParentTwo.EntityType = 'Site'
    $siteUnderParentTwo.Id = '502'
    $siteUnderParentTwo.Name = 'Main Office'
    $siteUnderParentTwo.ExternalIds['NCentralSiteId'] = '502'
    $siteUnderParentTwo.ExternalIds['NCentralCustomerId'] = '222'
    $siteUnderParentTwo.CustomFields['NCentralCustomerName'] = 'Fallback Metals LLC'

    $mapper = [LISSTech.EntitySync.Mapping.DefaultEntityMapper]::new()
    $requestOne = $mapper.MapCreate($siteUnderParentOne, 'LCAT', 'Customer', [LISSTech.EntitySync.Core.MatchOptions]::new())
    $requestTwo = $mapper.MapCreate($siteUnderParentTwo, 'LCAT', 'Customer', [LISSTech.EntitySync.Core.MatchOptions]::new())

    $requestOne.Fields['slug'] | Should -Match '^[A-Za-z0-9][A-Za-z0-9_-]{0,62}[A-Za-z0-9]$'
    $requestTwo.Fields['slug'] | Should -Match '^[A-Za-z0-9][A-Za-z0-9_-]{0,62}[A-Za-z0-9]$'
    $requestOne.Fields['slug'] | Should -Not -Be $requestTwo.Fields['slug']
  }

  It 'Derives the same LCAT slug each time for the same N-central Site source (T028, US2)' {
    $site = [LISSTech.EntitySync.Core.ExternalEntity]::new()
    $site.Vendor = 'NCentral'
    $site.EntityType = 'Site'
    $site.Id = '503'
    $site.Name = 'Warehouse'
    $site.ExternalIds['NCentralSiteId'] = '503'
    $site.ExternalIds['NCentralCustomerId'] = '333'
    $site.CustomFields['NCentralCustomerName'] = 'Northshore Plumbing'

    $mapper = [LISSTech.EntitySync.Mapping.DefaultEntityMapper]::new()
    $slugFirst = $mapper.MapCreate($site, 'LCAT', 'Customer', [LISSTech.EntitySync.Core.MatchOptions]::new()).Fields['slug']
    $slugSecond = $mapper.MapCreate($site, 'LCAT', 'Customer', [LISSTech.EntitySync.Core.MatchOptions]::new()).Fields['slug']

    $slugFirst | Should -Not -BeNullOrEmpty
    $slugFirst | Should -Be $slugSecond
  }

  It 'Changes the derived LCAT slug when only the parent customer name differs for an otherwise identical N-central Site (T028, US2)' {
    $siteWithParentName = [LISSTech.EntitySync.Core.ExternalEntity]::new()
    $siteWithParentName.Vendor = 'NCentral'
    $siteWithParentName.EntityType = 'Site'
    $siteWithParentName.Id = '504'
    $siteWithParentName.Name = 'Depot'
    $siteWithParentName.ExternalIds['NCentralSiteId'] = '504'
    $siteWithParentName.ExternalIds['NCentralCustomerId'] = '444'
    $siteWithParentName.CustomFields['NCentralCustomerName'] = 'Original Parent Name'

    $siteWithRenamedParent = [LISSTech.EntitySync.Core.ExternalEntity]::new()
    $siteWithRenamedParent.Vendor = 'NCentral'
    $siteWithRenamedParent.EntityType = 'Site'
    $siteWithRenamedParent.Id = '504'
    $siteWithRenamedParent.Name = 'Depot'
    $siteWithRenamedParent.ExternalIds['NCentralSiteId'] = '504'
    $siteWithRenamedParent.ExternalIds['NCentralCustomerId'] = '444'
    $siteWithRenamedParent.CustomFields['NCentralCustomerName'] = 'Renamed Parent Name'

    $mapper = [LISSTech.EntitySync.Mapping.DefaultEntityMapper]::new()
    $originalSlug = $mapper.MapCreate($siteWithParentName, 'LCAT', 'Customer', [LISSTech.EntitySync.Core.MatchOptions]::new()).Fields['slug']
    $renamedSlug = $mapper.MapCreate($siteWithRenamedParent, 'LCAT', 'Customer', [LISSTech.EntitySync.Core.MatchOptions]::new()).Fields['slug']

    $originalSlug | Should -Not -Be $renamedSlug
  }

  It 'Creates an NCentral Site to LCAT plan producing a Create action when the site has a valid parent (T029, US2)' {
    $ncOptions = [LISSTech.EntitySync.Adapters.NCentral.NCentralOptions]::new()
    $ncOptions.BaseUrl = 'https://ncentral.example.test/'
    $ncOptions.UserApiToken = 'token'
    $ncOptions.ServiceOrgId = '50'
    $ncAdapter = [LISSTech.EntitySync.Adapters.NCentral.NCentralEntityAdapter]::new($ncOptions)
    $lcatAdapter = New-TestLCATAdapter

    try {
      [LISSTech.EntitySync.Runtime.ConnectionRegistry]::Set($ncAdapter)
      [LISSTech.EntitySync.Runtime.ConnectionRegistry]::Set($lcatAdapter)

      $site = [LISSTech.EntitySync.Core.ExternalEntity]::new()
      $site.Vendor = 'NCentral'
      $site.EntityType = 'Site'
      $site.Id = '605'
      $site.Name = 'Northshore Plumbing - Warehouse'
      $site.ExternalIds['NCentralSiteId'] = '605'
      $site.ExternalIds['NCentralCustomerId'] = '444'

      $plan = @($site) | New-EntitySyncPlan -SourceVendor NCentral -TargetVendor LCAT -TargetEntityType Customer -CreateMissing

      $plan.TargetVendor | Should -Be 'LCAT'
      $plan.TargetCandidates.Count | Should -Be 0
      $plan.Items.Count | Should -Be 1
      $plan.Items[0].Action | Should -Be 'Create'
      $plan.Items[0].MatchType | Should -Be 'NoMatch'
      $plan.Items[0].Target | Should -BeNullOrEmpty
    }
    finally {
      $ncAdapter.Dispose()
      $lcatAdapter.Dispose()
    }
  }

  It 'Composes an LCAT batch payload carrying the site''s own id and parent customer id alongside a customer item (T029, US2)' {
    $customerSource = [LISSTech.EntitySync.Core.ExternalEntity]::new()
    $customerSource.Vendor = 'NCentral'
    $customerSource.EntityType = 'Customer'
    $customerSource.Id = '701'
    $customerSource.Name = 'Arista Air Conditioning Corp.'
    $customerSource.ExternalIds['NCentralCustomerId'] = '701'

    $siteSource = [LISSTech.EntitySync.Core.ExternalEntity]::new()
    $siteSource.Vendor = 'NCentral'
    $siteSource.EntityType = 'Site'
    $siteSource.Id = '702'
    $siteSource.Name = 'Arista Air Conditioning Corp. - Main Office'
    $siteSource.ExternalIds['NCentralSiteId'] = '702'
    $siteSource.ExternalIds['NCentralCustomerId'] = '701'

    $mapper = [LISSTech.EntitySync.Mapping.DefaultEntityMapper]::new()
    $customerRequest = $mapper.MapCreate($customerSource, 'LCAT', 'Customer', [LISSTech.EntitySync.Core.MatchOptions]::new())
    $siteRequest = $mapper.MapCreate($siteSource, 'LCAT', 'Customer', [LISSTech.EntitySync.Core.MatchOptions]::new())

    $toScopeMethod = [LISSTech.EntitySync.Commands.InvokeEntitySyncPlanCommand].GetMethod('ToLcatCustomerScopeRequest', [System.Reflection.BindingFlags]'NonPublic, Static')
    $customerScope = $toScopeMethod.Invoke($null, @($customerRequest))
    $siteScope = $toScopeMethod.Invoke($null, @($siteRequest))

    $customers = [System.Collections.Generic.List[LISSTech.EntitySync.Adapters.LCAT.LCATCustomerScopeRequest]]::new()
    $customers.Add($customerScope)
    $customers.Add($siteScope)

    $buildMethod = [LISSTech.EntitySync.Adapters.LCAT.LCATEntityAdapter].GetMethod('BuildSyncRequestBody', [System.Reflection.BindingFlags]'NonPublic, Static')
    $bodyJson = $buildMethod.Invoke($null, @(, $customers))
    $body = $bodyJson | ConvertFrom-Json

    $body.customers.Count | Should -Be 2
    $body.customers[0].ncentral_customer_id | Should -Be '701'
    $body.customers[0].ncentral_parent_customer_id | Should -BeNullOrEmpty
    $body.customers[1].ncentral_customer_id | Should -Be '702'
    $body.customers[1].ncentral_parent_customer_id | Should -Be '701'
  }

  It 'Rejects an LCAT batch sync request carrying duplicate ncentral_customer_id values across customer and site items (T033, US2)' {
    $lcatAdapter = New-TestLCATAdapter

    try {
      $customerScope = [LISSTech.EntitySync.Adapters.LCAT.LCATCustomerScopeRequest]::new()
      $customerScope.Slug = 'arista-air-conditioning'
      $customerScope.DisplayName = 'Arista Air Conditioning Corp.'
      $customerScope.NCentralCustomerId = '701'

      $siteScope = [LISSTech.EntitySync.Adapters.LCAT.LCATCustomerScopeRequest]::new()
      $siteScope.Slug = 'arista-air-conditioning-main-office'
      $siteScope.DisplayName = 'Main Office'
      $siteScope.NCentralCustomerId = '701'
      $siteScope.NCentralParentCustomerId = '701'

      $customers = [System.Collections.Generic.List[LISSTech.EntitySync.Adapters.LCAT.LCATCustomerScopeRequest]]::new()
      $customers.Add($customerScope)
      $customers.Add($siteScope)

      { $lcatAdapter.SyncCustomerScopesAsync($customers, [System.Threading.CancellationToken]::None).GetAwaiter().GetResult() } |
        Should -Throw '*duplicate ncentral_customer_id*'
    }
    finally {
      $lcatAdapter.Dispose()
    }
  }

  It 'Rejects case-only duplicate LCAT ncentral_customer_id values before HTTP send' {
    $lcatAdapter = New-TestLCATAdapter

    try {
      $customerScope = [LISSTech.EntitySync.Adapters.LCAT.LCATCustomerScopeRequest]::new()
      $customerScope.Slug = 'case-duplicate-customer'
      $customerScope.DisplayName = 'Case Duplicate Customer'
      $customerScope.NCentralCustomerId = 'ABC-701'

      $siteScope = [LISSTech.EntitySync.Adapters.LCAT.LCATCustomerScopeRequest]::new()
      $siteScope.Slug = 'case-duplicate-site'
      $siteScope.DisplayName = 'Case Duplicate Site'
      $siteScope.NCentralCustomerId = 'abc-701'
      $siteScope.NCentralParentCustomerId = '701'

      $customers = [System.Collections.Generic.List[LISSTech.EntitySync.Adapters.LCAT.LCATCustomerScopeRequest]]::new()
      $customers.Add($customerScope)
      $customers.Add($siteScope)

      { $lcatAdapter.SyncCustomerScopesAsync($customers, [System.Threading.CancellationToken]::None).GetAwaiter().GetResult() } |
        Should -Throw '*duplicate ncentral_customer_id*'
    }
    finally {
      $lcatAdapter.Dispose()
    }
  }

  It 'Rejects whitespace-hidden duplicate LCAT ncentral_customer_id values before HTTP send' {
    $lcatAdapter = New-TestLCATAdapter

    try {
      $customerScope = [LISSTech.EntitySync.Adapters.LCAT.LCATCustomerScopeRequest]::new()
      $customerScope.Slug = 'whitespace-duplicate-customer'
      $customerScope.DisplayName = 'Whitespace Duplicate Customer'
      $customerScope.NCentralCustomerId = '701'

      $siteScope = [LISSTech.EntitySync.Adapters.LCAT.LCATCustomerScopeRequest]::new()
      $siteScope.Slug = 'whitespace-duplicate-site'
      $siteScope.DisplayName = 'Whitespace Duplicate Site'
      $siteScope.NCentralCustomerId = ' 701 '
      $siteScope.NCentralParentCustomerId = '701'

      $customers = [System.Collections.Generic.List[LISSTech.EntitySync.Adapters.LCAT.LCATCustomerScopeRequest]]::new()
      $customers.Add($customerScope)
      $customers.Add($siteScope)

      { $lcatAdapter.SyncCustomerScopesAsync($customers, [System.Threading.CancellationToken]::None).GetAwaiter().GetResult() } |
        Should -Throw '*duplicate ncentral_customer_id*'
    }
    finally {
      $lcatAdapter.Dispose()
    }
  }

  It 'Trims LCAT customer-scope values before serializing the batch request' {
    $customerScope = [LISSTech.EntitySync.Adapters.LCAT.LCATCustomerScopeRequest]::new()
    $customerScope.Slug = ' arista-air-conditioning '
    $customerScope.DisplayName = ' Arista Air Conditioning Corp. '
    $customerScope.NCentralCustomerId = ' 701 '
    $customerScope.NCentralParentCustomerId = ' '

    $siteScope = [LISSTech.EntitySync.Adapters.LCAT.LCATCustomerScopeRequest]::new()
    $siteScope.Slug = ' arista-air-conditioning-main-office '
    $siteScope.DisplayName = ' Main Office '
    $siteScope.NCentralCustomerId = ' 702 '
    $siteScope.NCentralParentCustomerId = ' 701 '

    $customers = [System.Collections.Generic.List[LISSTech.EntitySync.Adapters.LCAT.LCATCustomerScopeRequest]]::new()
    $customers.Add($customerScope)
    $customers.Add($siteScope)

    $buildMethod = [LISSTech.EntitySync.Adapters.LCAT.LCATEntityAdapter].GetMethod('BuildSyncRequestBody', [System.Reflection.BindingFlags]'NonPublic, Static')
    $bodyJson = $buildMethod.Invoke($null, @(, $customers))
    $body = $bodyJson | ConvertFrom-Json

    $body.customers[0].slug | Should -Be 'arista-air-conditioning'
    $body.customers[0].display_name | Should -Be 'Arista Air Conditioning Corp.'
    $body.customers[0].ncentral_customer_id | Should -Be '701'
    $body.customers[0].ncentral_parent_customer_id | Should -BeNullOrEmpty
    $body.customers[1].slug | Should -Be 'arista-air-conditioning-main-office'
    $body.customers[1].display_name | Should -Be 'Main Office'
    $body.customers[1].ncentral_customer_id | Should -Be '702'
    $body.customers[1].ncentral_parent_customer_id | Should -Be '701'
  }

  It 'Rejects malformed LCAT customer-scope requests before sending the batch' {
    $lcatAdapter = New-TestLCATAdapter

    try {
      $customerScope = [LISSTech.EntitySync.Adapters.LCAT.LCATCustomerScopeRequest]::new()
      $customerScope.Slug = '###'
      $customerScope.DisplayName = ''
      $customerScope.NCentralCustomerId = ''

      $customers = [System.Collections.Generic.List[LISSTech.EntitySync.Adapters.LCAT.LCATCustomerScopeRequest]]::new()
      $customers.Add($customerScope)

      { $lcatAdapter.SyncCustomerScopesAsync($customers, [System.Threading.CancellationToken]::None).GetAwaiter().GetResult() } |
        Should -Throw '*customers`[0`].slug must match the LCAT customer-scope contract*display_name is required*ncentral_customer_id is required*'
    }
    finally {
      $lcatAdapter.Dispose()
    }
  }

  It 'Excludes LCATBearerToken from the Connect-EntitySyncVendor LCAT connection object (T035, US3)' {
    $secretToken = 'super-secret-lcat-bearer-9f8e7d6c'
    $connection = Connect-EntitySyncVendor -Vendor LCAT -LCATBaseUrl 'https://lcat.example.test' -LCATBearerToken $secretToken

    try {
      $connection.Vendor | Should -Be 'LCAT'
      $connection.PSObject.Properties.Name | Should -Not -Contain 'LCATBearerToken'
      $connection.PSObject.Properties.Name | Should -Not -Contain 'BearerToken'

      $renderedConnection = $connection | Format-List -Property * | Out-String
      $renderedConnection | Should -Not -Match ([regex]::Escape($secretToken))

      $renderedAdapter = $connection.Adapter | Format-List -Property * | Out-String
      $renderedAdapter | Should -Not -Match ([regex]::Escape($secretToken))
    }
    finally {
      ($connection.Adapter -as [IDisposable])?.Dispose()
    }
  }

  It 'Excludes LCATBearerToken from Connect-EntitySyncVendor error messages (T035, US3)' {
    $secretToken = 'super-secret-lcat-bearer-9f8e7d6c'
    $caught = $null

    try {
      Connect-EntitySyncVendor -Vendor LCAT -LCATBaseUrl 'http://lcat.example.test' -LCATBearerToken $secretToken -ErrorAction Stop
    }
    catch {
      $caught = $_
    }

    $caught | Should -Not -BeNullOrEmpty
    $caught.Exception.Message | Should -Not -Match ([regex]::Escape($secretToken))
    ($caught | Out-String) | Should -Not -Match ([regex]::Escape($secretToken))
  }

  It 'Excludes LCAT credential-bearing options from registered connection output (T041, US3)' {
    $secretToken = 'registered-lcat-bearer-a1b2c3d4'
    $connection = Connect-EntitySyncVendor -Vendor LCAT -LCATBaseUrl 'https://lcat.example.test' -LCATBearerToken $secretToken

    try {
      $registered = Get-EntitySyncConnection | Where-Object Vendor -eq 'LCAT' | Select-Object -First 1

      $registered | Should -Not -BeNullOrEmpty
      $registered.Vendor | Should -Be 'LCAT'
      $registered.PSObject.Properties.Name | Should -Not -Contain 'LCATOptions'
      $registered.PSObject.Properties.Name | Should -Not -Contain 'LCATBearerToken'
      $registered.PSObject.Properties.Name | Should -Not -Contain 'BearerToken'

      $renderedConnection = $registered | Format-List -Property * | Out-String
      $renderedConnection | Should -Not -Match 'LCATOptions'
      $renderedConnection | Should -Not -Match 'LCATBearerToken'
      $renderedConnection | Should -Not -Match 'BearerToken'
      $renderedConnection | Should -Not -Match ([regex]::Escape($secretToken))

      $serializedConnection = $registered | ConvertTo-Json -Depth 5
      $serializedConnection | Should -Not -Match 'LCATOptions'
      $serializedConnection | Should -Not -Match 'LCATBearerToken'
      $serializedConnection | Should -Not -Match 'BearerToken'
      $serializedConnection | Should -Not -Match ([regex]::Escape($secretToken))
    }
    finally {
      ($connection.Adapter -as [IDisposable])?.Dispose()
    }
  }

  It 'Excludes N-central registration tokens and other custom field metadata from LCAT customer mapping (T036, US3)' {
    $registrationToken = 'ncentral-reg-token-4f3e2d1c'

    $customerSource = [LISSTech.EntitySync.Core.ExternalEntity]::new()
    $customerSource.Vendor = 'NCentral'
    $customerSource.EntityType = 'Customer'
    $customerSource.Id = '701'
    $customerSource.Name = 'Arista Air Conditioning Corp.'
    $customerSource.ExternalIds['NCentralCustomerId'] = '701'
    $customerSource.CustomFields['NCentralRegistrationToken'] = $registrationToken
    $customerSource.CustomFields['NCentralOrgUnitType'] = 'customer'

    $mapper = [LISSTech.EntitySync.Mapping.DefaultEntityMapper]::new()
    $customerRequest = $mapper.MapCreate($customerSource, 'LCAT', 'Customer', [LISSTech.EntitySync.Core.MatchOptions]::new())

    $customerRequest.Fields.Keys | Should -Not -Contain 'NCentralRegistrationToken'
    $customerRequest.Fields.Keys | Should -Not -Contain 'registration_token'
    $customerRequest.CustomFields.Count | Should -Be 0
    ($customerRequest.Fields.Values -join '|') | Should -Not -Match ([regex]::Escape($registrationToken))
  }

  It 'Excludes N-central registration tokens from LCAT site-derived mapping and the serialized batch request (T036, US3)' {
    $registrationToken = 'ncentral-site-reg-token-9a8b7c6d'

    $siteSource = [LISSTech.EntitySync.Core.ExternalEntity]::new()
    $siteSource.Vendor = 'NCentral'
    $siteSource.EntityType = 'Site'
    $siteSource.Id = '702'
    $siteSource.Name = 'Main Office'
    $siteSource.ExternalIds['NCentralSiteId'] = '702'
    $siteSource.ExternalIds['NCentralCustomerId'] = '701'
    $siteSource.CustomFields['NCentralCustomerName'] = 'Arista Air Conditioning Corp.'
    $siteSource.CustomFields['NCentralRegistrationToken'] = $registrationToken

    $mapper = [LISSTech.EntitySync.Mapping.DefaultEntityMapper]::new()
    $siteRequest = $mapper.MapCreate($siteSource, 'LCAT', 'Customer', [LISSTech.EntitySync.Core.MatchOptions]::new())

    $siteRequest.Fields.Keys | Should -Not -Contain 'NCentralRegistrationToken'
    $siteRequest.CustomFields.Count | Should -Be 0
    ($siteRequest.Fields.Values -join '|') | Should -Not -Match ([regex]::Escape($registrationToken))

    $toScopeMethod = [LISSTech.EntitySync.Commands.InvokeEntitySyncPlanCommand].GetMethod('ToLcatCustomerScopeRequest', [System.Reflection.BindingFlags]'NonPublic, Static')
    $siteScope = $toScopeMethod.Invoke($null, @($siteRequest))

    $customers = [System.Collections.Generic.List[LISSTech.EntitySync.Adapters.LCAT.LCATCustomerScopeRequest]]::new()
    $customers.Add($siteScope)

    $buildMethod = [LISSTech.EntitySync.Adapters.LCAT.LCATEntityAdapter].GetMethod('BuildSyncRequestBody', [System.Reflection.BindingFlags]'NonPublic, Static')
    $bodyJson = $buildMethod.Invoke($null, @(, $customers))

    $bodyJson | Should -Not -Match ([regex]::Escape($registrationToken))
  }

  It 'Performs no LCAT batch sync when Invoke-EntitySyncPlan is called with -WhatIf, even with -Apply (T037, US3)' {
    $lcatAdapter = New-TestLCATAdapter
    # Dispose the adapter's HttpClient up front so any attempt to actually send the batch
    # request throws ObjectDisposedException, giving a deterministic, network-free proof that
    # -WhatIf blocked the write: if the ShouldProcess guard in ApplyLcatBatch were ever bypassed,
    # SyncCustomerScopesAsync's httpClient.PostAsync call would fail loudly instead of silently
    # succeeding against a real endpoint.
    $lcatAdapter.Dispose()
    [LISSTech.EntitySync.Runtime.ConnectionRegistry]::Set($lcatAdapter)

    $source = [LISSTech.EntitySync.Core.ExternalEntity]::new()
    $source.Vendor = 'NCentral'
    $source.EntityType = 'Customer'
    $source.Id = '801'
    $source.Name = 'Whatif Widgets Inc.'
    $source.ExternalIds['NCentralCustomerId'] = '801'

    $item = [LISSTech.EntitySync.Core.EntitySyncPlanItem]::new()
    $item.Action = 'Create'
    $item.Source = $source
    $item.MatchType = 'NoMatch'
    [void]$item.Reasons.Add('No target candidate found')

    $plan = [LISSTech.EntitySync.Core.EntitySyncPlan]::new()
    $plan.SourceVendor = 'NCentral'
    $plan.SourceEntityType = 'Customer'
    $plan.TargetVendor = 'LCAT'
    $plan.TargetEntityType = 'Customer'
    [void]$plan.Items.Add($item)

    $results = Invoke-EntitySyncPlan -Plan $plan -Apply -WhatIf -PassThru

    $results.Count | Should -Be 1
    $results[0].Vendor | Should -Be 'LCAT'
    $results[0].Action | Should -Be 'Create'
    $results[0].Success | Should -BeTrue
    $results[0].Message | Should -Match 'LCAT batch sync preview'
    $results[0].Message | Should -Match 'no write performed because -WhatIf was specified'
    $results[0].Raw.NCentralCustomerId | Should -Be '801'
  }

  It 'Skips Review, Reject, No Update, None, unsafe, duplicate, and incomplete LCAT plan items during apply (T038, US3)' {
    $lcatAdapter = New-TestLCATAdapter
    # A disposed adapter turns any accidental approved batch write into a deterministic failure.
    $lcatAdapter.Dispose()
    [LISSTech.EntitySync.Runtime.ConnectionRegistry]::Set($lcatAdapter)

    $plan = [LISSTech.EntitySync.Core.EntitySyncPlan]::new()
    $plan.SourceVendor = 'NCentral'
    $plan.SourceEntityType = 'Customer'
    $plan.TargetVendor = 'LCAT'
    $plan.TargetEntityType = 'Customer'

    $cases = @(
      [pscustomobject]@{ Id = '901'; Name = 'Ambiguous Match Co.'; Action = 'Review'; Status = 'Planned'; MatchType = 'NeedsReview'; Reason = 'Multiple possible target candidates' },
      [pscustomobject]@{ Id = '902'; Name = 'Rejected Customer'; Action = 'None'; Status = 'Rejected'; MatchType = 'Rejected'; Reason = 'Reviewer rejected item' },
      [pscustomobject]@{ Id = '903'; Name = 'No Update Customer'; Action = 'None'; Status = 'NoUpdate'; MatchType = 'NoUpdate'; Reason = 'Reviewer chose No Update' },
      [pscustomobject]@{ Id = '904'; Name = 'Plain None Customer'; Action = 'None'; Status = 'Planned'; MatchType = 'NoMatch'; Reason = 'No action planned' },
      [pscustomobject]@{ Id = '905'; Name = '###'; Action = 'Review'; Status = 'Planned'; MatchType = 'Invalid'; Reason = 'Unsafe LCAT slug' },
      [pscustomobject]@{ Id = '906'; Name = 'Duplicate Id Customer'; Action = 'Review'; Status = 'Planned'; MatchType = 'Duplicate'; Reason = 'Duplicate N-central source identifier' },
      [pscustomobject]@{ Id = ''; Name = 'Incomplete Customer'; Action = 'Review'; Status = 'Planned'; MatchType = 'Invalid'; Reason = 'Missing N-central source identifier' }
    )

    foreach ($case in $cases) {
      $source = [LISSTech.EntitySync.Core.ExternalEntity]::new()
      $source.Vendor = 'NCentral'
      $source.EntityType = 'Customer'
      $source.Id = $case.Id
      $source.Name = $case.Name
      if (-not [string]::IsNullOrWhiteSpace($case.Id)) {
        $source.ExternalIds['NCentralCustomerId'] = $case.Id
      }

      $item = [LISSTech.EntitySync.Core.EntitySyncPlanItem]::new()
      $item.Action = $case.Action
      $item.Status = $case.Status
      $item.MatchType = $case.MatchType
      $item.Source = $source
      [void]$item.Reasons.Add($case.Reason)
      [void]$plan.Items.Add($item)
    }

    $results = $null
    try {
      $results = Invoke-EntitySyncPlan -Plan $plan -Apply -PassThru -Confirm:$false
    }
    catch {
      throw
    }

    $reviewResults = @($results | Where-Object { $_.Action -eq 'Review' })
    $reviewResults.Count | Should -Be 4
    $reviewResults.Success | Should -Not -Contain $true
    $reviewResults.Message | Should -Contain 'Item requires review before apply.'
    @($results | Where-Object { $_.Action -ne 'Review' }).Count | Should -Be 0
  }

  It 'Reports a non-secret no-op result when an LCAT apply has no approved batch items' {
    $lcatAdapter = New-TestLCATAdapter
    # A disposed adapter proves no HTTP write is attempted for an all-no-op LCAT plan.
    $lcatAdapter.Dispose()
    [LISSTech.EntitySync.Runtime.ConnectionRegistry]::Set($lcatAdapter)

    $source = [LISSTech.EntitySync.Core.ExternalEntity]::new()
    $source.Vendor = 'NCentral'
    $source.EntityType = 'Customer'
    $source.Id = '1001'
    $source.Name = 'No Update Customer'
    $source.ExternalIds['NCentralCustomerId'] = '1001'

    $item = [LISSTech.EntitySync.Core.EntitySyncPlanItem]::new()
    $item.Action = 'None'
    $item.Status = 'NoUpdate'
    $item.MatchType = 'NoUpdate'
    $item.Source = $source
    [void]$item.Reasons.Add('Reviewer chose No Update')

    $plan = [LISSTech.EntitySync.Core.EntitySyncPlan]::new()
    $plan.SourceVendor = 'NCentral'
    $plan.SourceEntityType = 'Customer'
    $plan.TargetVendor = 'LCAT'
    $plan.TargetEntityType = 'Customer'
    [void]$plan.Items.Add($item)

    $results = Invoke-EntitySyncPlan -Plan $plan -Apply -PassThru -Confirm:$false

    $results.Count | Should -Be 1
    $results[0].Vendor | Should -Be 'LCAT'
    $results[0].Action | Should -Be 'None'
    $results[0].Success | Should -BeTrue
    $results[0].Message | Should -Be 'No approved LCAT customer-scope items were eligible for batch sync.'
    $results[0].Message | Should -Not -Match 'token|Authorization|Bearer'
  }

  It 'Filters invalid approved LCAT plan items before composing the batch request (T042, US3)' {
    $server = [EntitySyncTests.OneShotHttpServer]::new(200, 'OK', '{"inserted_count":1,"updated_count":0,"retired_count":0,"active_count":1}')
    $server.Start()

    $lcatAdapter = New-TestLCATAdapter -Options (New-TestLCATOptions -BaseUrl $server.BaseUrl)
    [LISSTech.EntitySync.Runtime.ConnectionRegistry]::Set($lcatAdapter)

    try {
      $plan = [LISSTech.EntitySync.Core.EntitySyncPlan]::new()
      $plan.SourceVendor = 'NCentral'
      $plan.SourceEntityType = 'Customer'
      $plan.TargetVendor = 'LCAT'
      $plan.TargetEntityType = 'Customer'

      foreach ($case in @(
        [pscustomobject]@{ Id = '1201'; Name = 'Valid Batch Customer' },
        [pscustomobject]@{ Id = ''; Name = 'Missing Identifier Customer' },
        [pscustomobject]@{ Id = 'duplicate-1203'; Name = 'Duplicate Customer A' },
        [pscustomobject]@{ Id = 'duplicate-1203'; Name = 'Duplicate Customer B' },
        [pscustomobject]@{ Id = '1204'; Name = 'Duplicate Slug Co' },
        [pscustomobject]@{ Id = '1205'; Name = 'Duplicate Slug Co' }
      )) {
        $source = [LISSTech.EntitySync.Core.ExternalEntity]::new()
        $source.Vendor = 'NCentral'
        $source.EntityType = 'Customer'
        $source.Id = $case.Id
        $source.Name = $case.Name
        if (-not [string]::IsNullOrWhiteSpace($case.Id)) {
          $source.ExternalIds['NCentralCustomerId'] = $case.Id
        }

        $item = [LISSTech.EntitySync.Core.EntitySyncPlanItem]::new()
        $item.Action = 'Create'
        $item.Source = $source
        $item.MatchType = 'NoMatch'
        [void]$item.Reasons.Add('No target candidate found')
        [void]$plan.Items.Add($item)
      }

      $results = Invoke-EntitySyncPlan -Plan $plan -Apply -PassThru -Confirm:$false
      $server.Wait()

      $successResults = @($results | Where-Object Success)
      $failedResults = @($results | Where-Object { -not $_.Success })
      $successResults.Count | Should -Be 1
      $failedResults.Count | Should -Be 5
      $failedResults.Message | Should -Contain 'LCAT item skipped before batch sync: ncentral_customer_id is required.'
      ($failedResults.Message -join "`n") | Should -Match "duplicate ncentral_customer_id 'duplicate-1203'"
      ($failedResults.Message -join "`n") | Should -Match "duplicate slug 'Duplicate-Slug-Co'"

      $requestBody = $server.RequestText.Substring($server.RequestText.IndexOf("`r`n`r`n") + 4) | ConvertFrom-Json
      @($requestBody.customers).Count | Should -Be 1
      $requestBody.customers[0].ncentral_customer_id | Should -Be '1201'
      $requestBody.customers[0].display_name | Should -Be 'Valid Batch Customer'
    }
    finally {
      $server.Dispose()
      $lcatAdapter.Dispose()
    }
  }

  It 'Filters case-only duplicate approved LCAT source identifiers before composing the batch request' {
    $server = [EntitySyncTests.OneShotHttpServer]::new(200, 'OK', '{"inserted_count":1,"updated_count":0,"retired_count":0,"active_count":1}')
    $server.Start()

    $lcatAdapter = New-TestLCATAdapter -Options (New-TestLCATOptions -BaseUrl $server.BaseUrl)
    [LISSTech.EntitySync.Runtime.ConnectionRegistry]::Set($lcatAdapter)

    try {
      $plan = [LISSTech.EntitySync.Core.EntitySyncPlan]::new()
      $plan.SourceVendor = 'NCentral'
      $plan.SourceEntityType = 'Customer'
      $plan.TargetVendor = 'LCAT'
      $plan.TargetEntityType = 'Customer'

      foreach ($case in @(
        [pscustomobject]@{ Id = '1401'; Name = 'Valid Case Batch Customer' },
        [pscustomobject]@{ Id = 'CASE-DUPLICATE-1402'; Name = 'Duplicate Case Customer A' },
        [pscustomobject]@{ Id = 'case-duplicate-1402'; Name = 'Duplicate Case Customer B' }
      )) {
        $source = [LISSTech.EntitySync.Core.ExternalEntity]::new()
        $source.Vendor = 'NCentral'
        $source.EntityType = 'Customer'
        $source.Id = $case.Id
        $source.Name = $case.Name
        $source.ExternalIds['NCentralCustomerId'] = $case.Id

        $item = [LISSTech.EntitySync.Core.EntitySyncPlanItem]::new()
        $item.Action = 'Create'
        $item.Source = $source
        $item.MatchType = 'NoMatch'
        [void]$item.Reasons.Add('No target candidate found')
        [void]$plan.Items.Add($item)
      }

      $results = Invoke-EntitySyncPlan -Plan $plan -Apply -PassThru -Confirm:$false
      $server.Wait()

      $successResults = @($results | Where-Object Success)
      $failedResults = @($results | Where-Object { -not $_.Success })
      $successResults.Count | Should -Be 1
      $failedResults.Count | Should -Be 2
      ($failedResults.Message -join "`n") | Should -Match "duplicate ncentral_customer_id 'CASE-DUPLICATE-1402'"
      ($failedResults.Message -join "`n") | Should -Match "duplicate ncentral_customer_id 'case-duplicate-1402'"

      $requestBody = $server.RequestText.Substring($server.RequestText.IndexOf("`r`n`r`n") + 4) | ConvertFrom-Json
      @($requestBody.customers).Count | Should -Be 1
      $requestBody.customers[0].ncentral_customer_id | Should -Be '1401'
    }
    finally {
      $server.Dispose()
      $lcatAdapter.Dispose()
    }
  }

  It 'Treats whitespace-hidden duplicate approved LCAT identifiers as duplicates before batch composition' {
    $item = [LISSTech.EntitySync.Core.EntitySyncPlanItem]::new()
    $item.Action = 'Create'
    $item.Source = [LISSTech.EntitySync.Core.ExternalEntity]::new()
    $item.Source.Vendor = 'NCentral'
    $item.Source.EntityType = 'Customer'
    $item.Source.Id = '1501'
    $item.Source.Name = 'Whitespace Duplicate Customer'

    $request = [LISSTech.EntitySync.Adapters.LCAT.LCATCustomerScopeRequest]::new()
    $request.Slug = 'Whitespace-Duplicate-Customer'
    $request.DisplayName = 'Whitespace Duplicate Customer'
    $request.NCentralCustomerId = ' duplicate-1501 '

    $duplicateIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    [void]$duplicateIds.Add('duplicate-1501')
    $duplicateSlugs = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

    $method = [LISSTech.EntitySync.Commands.InvokeEntitySyncPlanCommand].GetMethod('ValidateLcatCustomerScopeRequest', [System.Reflection.BindingFlags]'NonPublic, Static')
    $errors = $method.Invoke($null, @($item, $request, $duplicateIds, $duplicateSlugs))

    $errors | Should -Contain "duplicate ncentral_customer_id ' duplicate-1501 '"
  }

  It 'Rejects direct LCAT adapter batch calls with duplicate customer-scope slugs before HTTP send' {
    $lcatAdapter = New-TestLCATAdapter
    try {
      $customerOne = [LISSTech.EntitySync.Adapters.LCAT.LCATCustomerScopeRequest]::new()
      $customerOne.Slug = 'duplicate-scope'
      $customerOne.DisplayName = 'Duplicate Scope A'
      $customerOne.NCentralCustomerId = '1301'

      $customerTwo = [LISSTech.EntitySync.Adapters.LCAT.LCATCustomerScopeRequest]::new()
      $customerTwo.Slug = 'Duplicate-Scope'
      $customerTwo.DisplayName = 'Duplicate Scope B'
      $customerTwo.NCentralCustomerId = '1302'

      $customers = [System.Collections.Generic.List[LISSTech.EntitySync.Adapters.LCAT.LCATCustomerScopeRequest]]::new()
      $customers.Add($customerOne)
      $customers.Add($customerTwo)

      $caught = $null
      try {
        $lcatAdapter.SyncCustomerScopesAsync($customers, [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
      }
      catch {
        $caught = $_
      }

      $caught | Should -Not -BeNullOrEmpty
      $caught.Exception.Message | Should -Match 'duplicate slug'
      $caught.Exception.Message | Should -Match 'duplicate-scope'
    }
    finally {
      $lcatAdapter.Dispose()
    }
  }

  It 'Marks invalid LCAT source records for review during planning with non-secret safe-failure reasons (T043, US3)' {
    $ncOptions = [LISSTech.EntitySync.Adapters.NCentral.NCentralOptions]::new()
    $ncOptions.BaseUrl = 'https://ncentral.example.test/'
    $ncOptions.UserApiToken = 'token'
    $ncOptions.ServiceOrgId = '50'
    $ncAdapter = [LISSTech.EntitySync.Adapters.NCentral.NCentralEntityAdapter]::new($ncOptions)
    $lcatAdapter = New-TestLCATAdapter

    try {
      [LISSTech.EntitySync.Runtime.ConnectionRegistry]::Set($ncAdapter)
      [LISSTech.EntitySync.Runtime.ConnectionRegistry]::Set($lcatAdapter)

      $missingId = [LISSTech.EntitySync.Core.ExternalEntity]::new()
      $missingId.Vendor = 'NCentral'
      $missingId.EntityType = 'Customer'
      $missingId.Name = 'Missing Identifier Customer'

      $missingName = [LISSTech.EntitySync.Core.ExternalEntity]::new()
      $missingName.Vendor = 'NCentral'
      $missingName.EntityType = 'Customer'
      $missingName.Id = '1302'
      $missingName.ExternalIds['NCentralCustomerId'] = '1302'

      $unsafeSlug = [LISSTech.EntitySync.Core.ExternalEntity]::new()
      $unsafeSlug.Vendor = 'NCentral'
      $unsafeSlug.EntityType = 'Customer'
      $unsafeSlug.Id = '###'
      $unsafeSlug.Name = '###'
      $unsafeSlug.ExternalIds['NCentralCustomerId'] = '###'

      $duplicateOne = [LISSTech.EntitySync.Core.ExternalEntity]::new()
      $duplicateOne.Vendor = 'NCentral'
      $duplicateOne.EntityType = 'Customer'
      $duplicateOne.Id = '1304'
      $duplicateOne.Name = 'Duplicate Customer A'
      $duplicateOne.ExternalIds['NCentralCustomerId'] = 'duplicate-1304'

      $duplicateTwo = [LISSTech.EntitySync.Core.ExternalEntity]::new()
      $duplicateTwo.Vendor = 'NCentral'
      $duplicateTwo.EntityType = 'Customer'
      $duplicateTwo.Id = '1305'
      $duplicateTwo.Name = 'Duplicate Customer B'
      $duplicateTwo.ExternalIds['NCentralCustomerId'] = 'duplicate-1304'

      $duplicateSlugOne = [LISSTech.EntitySync.Core.ExternalEntity]::new()
      $duplicateSlugOne.Vendor = 'NCentral'
      $duplicateSlugOne.EntityType = 'Customer'
      $duplicateSlugOne.Id = '1306'
      $duplicateSlugOne.Name = 'Contoso!!!'
      $duplicateSlugOne.ExternalIds['NCentralCustomerId'] = '1306'

      $duplicateSlugTwo = [LISSTech.EntitySync.Core.ExternalEntity]::new()
      $duplicateSlugTwo.Vendor = 'NCentral'
      $duplicateSlugTwo.EntityType = 'Customer'
      $duplicateSlugTwo.Id = '1307'
      $duplicateSlugTwo.Name = 'Contoso???'
      $duplicateSlugTwo.ExternalIds['NCentralCustomerId'] = '1307'

      $plan = @($missingId, $missingName, $unsafeSlug, $duplicateOne, $duplicateTwo, $duplicateSlugOne, $duplicateSlugTwo) |
        New-EntitySyncPlan -SourceVendor NCentral -TargetVendor LCAT -TargetEntityType Customer -CreateMissing

      $plan.Items.Count | Should -Be 7
      $plan.Items.Action | Should -Not -Contain 'Create'
      $plan.Items.Action | Should -Not -Contain 'Update'
      $plan.Items.Action | Should -Not -Contain 'Link'
      $plan.Items.MatchType | Should -Contain 'LcatSourceInvalid'

      $reasons = $plan.Items.Reasons -join "`n"
      $reasons | Should -Match 'source identifier'
      $reasons | Should -Match 'display name'
      $reasons | Should -Match 'safe LCAT customer-scope slug'
      $reasons | Should -Match "Duplicate N-central source identifier 'duplicate-1304'"
      $reasons | Should -Match "Duplicate LCAT customer-scope slug 'Contoso'"
      $reasons | Should -Not -Match 'token'
    }
    finally {
      $ncAdapter.Dispose()
      $lcatAdapter.Dispose()
    }
  }

  It 'Blocks non-NCentral sources from LCAT customer-scope planning before apply' {
    $haloOptions = [LISSTech.EntitySync.Adapters.Halo.HaloOptions]::new()
    $haloOptions.BaseUrl = 'https://halo.example.test/'
    $haloOptions.AccessToken = 'token'
    $haloAdapter = [LISSTech.EntitySync.Adapters.Halo.HaloEntityAdapter]::new($haloOptions)
    $lcatAdapter = New-TestLCATAdapter

    try {
      [LISSTech.EntitySync.Runtime.ConnectionRegistry]::Set($haloAdapter)
      [LISSTech.EntitySync.Runtime.ConnectionRegistry]::Set($lcatAdapter)

      $source = [LISSTech.EntitySync.Core.ExternalEntity]::new()
      $source.Vendor = 'HaloPSA'
      $source.EntityType = 'Client'
      $source.Id = 'HALO-101'
      $source.Name = 'Contoso Operations'

      $plan = @($source) | New-EntitySyncPlan -SourceVendor HaloPSA -SourceEntityType Client -TargetVendor LCAT -TargetEntityType Customer -CreateMissing

      $plan.TargetVendor | Should -Be 'LCAT'
      $plan.Items.Count | Should -Be 1
      $plan.Items[0].Action | Should -Be 'Review'
      $plan.Items[0].MatchType | Should -Be 'LcatSourceInvalid'
      $plan.Items[0].Reasons -join "`n" | Should -Match 'only accepts N-central Customer or Site source records'
      $plan.Items[0].Reasons -join "`n" | Should -Match "source vendor 'HaloPSA' is not supported"
    }
    finally {
      $haloAdapter.Dispose()
      $lcatAdapter.Dispose()
    }
  }

  It 'Reports LCAT non-success responses without authorization headers or bearer credentials (T039, US3)' {
    $secretToken = 'lcat-error-secret-bearer-1a2b3c4d'
    $server = [EntitySyncTests.OneShotHttpServer]::new(403, 'Forbidden', '{"error":"do not echo this body"}')
    $server.Start()

    $options = New-TestLCATOptions -BaseUrl $server.BaseUrl -BearerToken $secretToken
    $lcatAdapter = New-TestLCATAdapter -Options $options

    try {
      $customerScope = [LISSTech.EntitySync.Adapters.LCAT.LCATCustomerScopeRequest]::new()
      $customerScope.Slug = 'arista-air-conditioning'
      $customerScope.DisplayName = 'Arista Air Conditioning Corp.'
      $customerScope.NCentralCustomerId = '701'

      $customers = [System.Collections.Generic.List[LISSTech.EntitySync.Adapters.LCAT.LCATCustomerScopeRequest]]::new()
      $customers.Add($customerScope)

      $caught = $null
      try {
        $lcatAdapter.SyncCustomerScopesAsync($customers, [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
      }
      catch {
        $caught = $_
      }
      finally {
        $server.Wait()
      }

      $server.RequestText | Should -Match 'POST /rpc/sync_ncentral_customers '
      $server.RequestText | Should -Match ([regex]::Escape("Authorization: Bearer $secretToken"))
      $caught | Should -Not -BeNullOrEmpty
      $caught.Exception.Message | Should -Match 'HTTP 403 Forbidden'
      $caught.Exception.Message | Should -Match 'Path: rpc/sync_ncentral_customers'
      $caught.Exception.Message | Should -Not -Match 'Authorization'
      $caught.Exception.Message | Should -Not -Match ([regex]::Escape($secretToken))
      $caught.Exception.Message | Should -Not -Match 'do not echo this body'
      ($caught | Out-String) | Should -Not -Match ([regex]::Escape($secretToken))
    }
    finally {
      $lcatAdapter.Dispose()
      $server.Dispose()
    }
  }

  It 'Reports malformed successful LCAT batch responses without raw body or bearer credentials' {
    $secretToken = 'malformed-lcat-bearer-123456'
    $server = [EntitySyncTests.OneShotHttpServer]::new(200, 'OK', 'not-json-secret-body')
    $server.Start()

    $options = New-TestLCATOptions -BaseUrl $server.BaseUrl -BearerToken $secretToken
    $lcatAdapter = New-TestLCATAdapter -Options $options

    try {
      $customerScope = [LISSTech.EntitySync.Adapters.LCAT.LCATCustomerScopeRequest]::new()
      $customerScope.Slug = 'arista-air-conditioning'
      $customerScope.DisplayName = 'Arista Air Conditioning Corp.'
      $customerScope.NCentralCustomerId = '701'

      $customers = [System.Collections.Generic.List[LISSTech.EntitySync.Adapters.LCAT.LCATCustomerScopeRequest]]::new()
      $customers.Add($customerScope)

      $caught = $null
      try {
        $lcatAdapter.SyncCustomerScopesAsync($customers, [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
      }
      catch {
        $caught = $_
      }
      finally {
        $server.Wait()
      }

      $caught | Should -Not -BeNullOrEmpty
      $caught.Exception.Message | Should -Match 'LCAT batch sync returned a malformed response'
      $caught.Exception.Message | Should -Match 'Path: rpc/sync_ncentral_customers'
      ($caught | Out-String) | Should -Not -Match ([regex]::Escape($secretToken))
      ($caught | Out-String) | Should -Not -Match 'not-json-secret-body'
    }
    finally {
      $lcatAdapter.Dispose()
      $server.Dispose()
    }
  }

  It 'Treats non-object successful LCAT batch responses as malformed without leaking secrets' {
    $secretToken = 'array-lcat-bearer-123456'
    $server = [EntitySyncTests.OneShotHttpServer]::new(200, 'OK', '["do not echo this array body"]')
    $server.Start()

    $options = New-TestLCATOptions -BaseUrl $server.BaseUrl -BearerToken $secretToken
    $lcatAdapter = New-TestLCATAdapter -Options $options

    try {
      $customerScope = [LISSTech.EntitySync.Adapters.LCAT.LCATCustomerScopeRequest]::new()
      $customerScope.Slug = 'arista-air-conditioning'
      $customerScope.DisplayName = 'Arista Air Conditioning Corp.'
      $customerScope.NCentralCustomerId = '701'

      $customers = [System.Collections.Generic.List[LISSTech.EntitySync.Adapters.LCAT.LCATCustomerScopeRequest]]::new()
      $customers.Add($customerScope)

      $caught = $null
      try {
        $lcatAdapter.SyncCustomerScopesAsync($customers, [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
      }
      catch {
        $caught = $_
      }
      finally {
        $server.Wait()
      }

      $caught | Should -Not -BeNullOrEmpty
      $caught.Exception.Message | Should -Match 'LCAT batch sync returned a malformed response'
      $caught.Exception.Message | Should -Match 'Path: rpc/sync_ncentral_customers'
      ($caught | Out-String) | Should -Not -Match ([regex]::Escape($secretToken))
      ($caught | Out-String) | Should -Not -Match 'do not echo this array body'
    }
    finally {
      $lcatAdapter.Dispose()
      $server.Dispose()
    }
  }

  It 'Treats non-numeric LCAT batch count fields as malformed without leaking secrets' {
    $secretToken = 'count-lcat-bearer-123456'
    $server = [EntitySyncTests.OneShotHttpServer]::new(200, 'OK', '{"inserted_count":"do not echo this count body","updated_count":0,"retired_count":0,"active_count":1}')
    $server.Start()

    $options = New-TestLCATOptions -BaseUrl $server.BaseUrl -BearerToken $secretToken
    $lcatAdapter = New-TestLCATAdapter -Options $options

    try {
      $customerScope = [LISSTech.EntitySync.Adapters.LCAT.LCATCustomerScopeRequest]::new()
      $customerScope.Slug = 'arista-air-conditioning'
      $customerScope.DisplayName = 'Arista Air Conditioning Corp.'
      $customerScope.NCentralCustomerId = '701'

      $customers = [System.Collections.Generic.List[LISSTech.EntitySync.Adapters.LCAT.LCATCustomerScopeRequest]]::new()
      $customers.Add($customerScope)

      $caught = $null
      try {
        $lcatAdapter.SyncCustomerScopesAsync($customers, [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
      }
      catch {
        $caught = $_
      }
      finally {
        $server.Wait()
      }

      $caught | Should -Not -BeNullOrEmpty
      $caught.Exception.Message | Should -Match 'LCAT batch sync returned a malformed response'
      $caught.Exception.Message | Should -Match 'Path: rpc/sync_ncentral_customers'
      ($caught | Out-String) | Should -Not -Match ([regex]::Escape($secretToken))
      ($caught | Out-String) | Should -Not -Match 'do not echo this count body'
    }
    finally {
      $lcatAdapter.Dispose()
      $server.Dispose()
    }
  }

  It 'Treats non-string LCAT audit event ids as malformed without leaking secrets' {
    $secretToken = 'audit-lcat-bearer-123456'
    $server = [EntitySyncTests.OneShotHttpServer]::new(200, 'OK', '{"inserted_count":0,"updated_count":0,"retired_count":0,"active_count":1,"audit_event_id":{"secret":"do not echo this audit body"}}')
    $server.Start()

    $options = New-TestLCATOptions -BaseUrl $server.BaseUrl -BearerToken $secretToken
    $lcatAdapter = New-TestLCATAdapter -Options $options

    try {
      $customerScope = [LISSTech.EntitySync.Adapters.LCAT.LCATCustomerScopeRequest]::new()
      $customerScope.Slug = 'arista-air-conditioning'
      $customerScope.DisplayName = 'Arista Air Conditioning Corp.'
      $customerScope.NCentralCustomerId = '701'

      $customers = [System.Collections.Generic.List[LISSTech.EntitySync.Adapters.LCAT.LCATCustomerScopeRequest]]::new()
      $customers.Add($customerScope)

      $caught = $null
      try {
        $lcatAdapter.SyncCustomerScopesAsync($customers, [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
      }
      catch {
        $caught = $_
      }
      finally {
        $server.Wait()
      }

      $caught | Should -Not -BeNullOrEmpty
      $caught.Exception.Message | Should -Match 'LCAT batch sync returned a malformed response'
      $caught.Exception.Message | Should -Match 'Path: rpc/sync_ncentral_customers'
      ($caught | Out-String) | Should -Not -Match ([regex]::Escape($secretToken))
      ($caught | Out-String) | Should -Not -Match 'do not echo this audit body'
    }
    finally {
      $lcatAdapter.Dispose()
      $server.Dispose()
    }
  }

  It 'Declares object output for Get-EntitySyncConnection' {
    (Get-Command Get-EntitySyncConnection).OutputType.Type.Name | Should -Contain 'EntitySyncConnection'
  }

  It 'Exposes ThrottleLimit on parallel read and plan commands' {
    (Get-Command Get-EntitySyncEntity).Parameters.Keys | Should -Contain 'ThrottleLimit'
    (Get-Command New-EntitySyncPlan).Parameters.Keys | Should -Contain 'ThrottleLimit'
  }

  It 'Exposes a gated chain sync command' {
    $command = Get-Command Invoke-EntitySyncChain
    $command.Parameters.Keys | Should -Contain 'RootVendor'
    $command.Parameters.Keys | Should -Contain 'HubVendor'
    $command.Parameters.Keys | Should -Contain 'LeafVendors'
    $command.Parameters.Keys | Should -Contain 'ReviewedPlanPath'
    $command.Parameters.Keys | Should -Contain 'Apply'
    $command.Parameters.Keys | Should -Contain 'WhatIf'
  }

  It 'Exposes a gated custom property setter' {
    $command = Get-Command Set-EntitySyncCustomProperty
    $command.Parameters.Keys | Should -Contain 'Vendor'
    $command.Parameters.Keys | Should -Contain 'EntityType'
    $command.Parameters.Keys | Should -Contain 'Id'
    $command.Parameters.Keys | Should -Contain 'Name'
    $command.Parameters.Keys | Should -Contain 'Value'
    $command.Parameters.Keys | Should -Contain 'Apply'
    $command.Parameters.Keys | Should -Contain 'WhatIf'
    $command.OutputType.Type.Name | Should -Contain 'EntityWriteResult'
  }

  It 'Sanitizes N-central names that contain ampersands' {
    [LISSTech.EntitySync.Adapters.NCentral.NCentralEntityAdapter]::SanitizeNCentralName('S.P. Cooper & Co. LLP') | Should -Be 'S.P. Cooper and Co. LLP'
  }

  It 'Normalizes N-central countries to ISO alpha-2 codes' -ForEach @(
    @{ Country = 'us'; Expected = 'US' }
    @{ Country = 'USA'; Expected = 'US' }
    @{ Country = 'U.S.A.'; Expected = 'US' }
    @{ Country = 'United States'; Expected = 'US' }
    @{ Country = 'United States of America'; Expected = 'US' }
  ) {
    [LISSTech.EntitySync.Adapters.NCentral.NCentralEntityAdapter]::NormalizeNCentralCountryCode($Country) | Should -Be $Expected
  }

  It 'Maps HaloPSA client ID into N-central externalId' {
    $source = [LISSTech.EntitySync.Core.ExternalEntity]::new()
    $source.Vendor = 'HaloPSA'
    $source.EntityType = 'Client'
    $source.Id = '684'
    $source.Name = 'GOAT USA Inc.'
    $source.ExternalIds['NetSuiteInternalId'] = '12345'
    $source.CustomFields['NetSuiteCustomerName'] = 'GOAT USA Inc. - NetSuite'

    $mapper = [LISSTech.EntitySync.Mapping.DefaultEntityMapper]::new()
    $request = $mapper.MapCreate($source, 'NCentral', 'Customer', [LISSTech.EntitySync.Core.MatchOptions]::new())

    $request.CustomFields['externalId'] | Should -Be '684'
    $request.CustomFields['HaloPsaId'] | Should -Be '684'
    $request.CustomFields['NetSuiteId'] | Should -Be '12345'
    $request.CustomFields['NetSuiteCustomerName'] | Should -Be 'GOAT USA Inc. - NetSuite'
  }

  It 'Does not fall back N-central NetSuite ID to HaloPSA client ID' {
    $source = [LISSTech.EntitySync.Core.ExternalEntity]::new()
    $source.Vendor = 'HaloPSA'
    $source.EntityType = 'Client'
    $source.Id = '13'
    $source.Name = 'Arista Air Conditioning Corp.'

    $mapper = [LISSTech.EntitySync.Mapping.DefaultEntityMapper]::new()
    $request = $mapper.MapCreate($source, 'NCentral', 'Customer', [LISSTech.EntitySync.Core.MatchOptions]::new())

    $request.CustomFields['externalId'] | Should -Be '13'
    $request.CustomFields['HaloPsaId'] | Should -Be '13'
    $request.CustomFields.ContainsKey('NetSuiteId') | Should -BeFalse
    $request.CustomFields.ContainsKey('CFNetSuiteCustomerID') | Should -BeFalse
  }

  It 'Maps HaloPSA client primary site address and communications into N-central customer fields' {
    $source = [LISSTech.EntitySync.Core.ExternalEntity]::new()
    $source.Vendor = 'HaloPSA'
    $source.EntityType = 'Client'
    $source.Id = '13'
    $source.Name = 'Arista Air Conditioning Corp.'
    $source.Email = 'service@arista.example'
    $source.Phone = '555-0100'
    $source.PrimaryAddress = [LISSTech.EntitySync.Core.EntityAddress]::new()
    $source.PrimaryAddress.Line1 = '123 Main St'
    $source.PrimaryAddress.Line2 = 'Suite 200'
    $source.PrimaryAddress.City = 'New York'
    $source.PrimaryAddress.State = 'NY'
    $source.PrimaryAddress.PostalCode = '10001'
    $source.PrimaryAddress.Country = 'US'

    $mapper = [LISSTech.EntitySync.Mapping.DefaultEntityMapper]::new()
    $request = $mapper.MapCreate($source, 'NCentral', 'Customer', [LISSTech.EntitySync.Core.MatchOptions]::new())

    $request.Fields['contactEmail'] | Should -Be 'service@arista.example'
    $request.Fields['phone'] | Should -Be '555-0100'
    $request.Fields['contactPhone'] | Should -Be '555-0100'
    $request.Fields.ContainsKey('contactFirstName') | Should -BeFalse
    $request.Fields.ContainsKey('contactLastName') | Should -BeFalse
    $request.Fields['address']['street1'] | Should -Be '123 Main St'
    $request.Fields['address']['street2'] | Should -Be 'Suite 200'
    $request.Fields['address']['city'] | Should -Be 'New York'
    $request.Fields['address']['stateProv'] | Should -Be 'NY'
    $request.Fields['address']['postalCode'] | Should -Be '10001'
    $request.Fields['address']['country'] | Should -Be 'US'
  }

  It 'Maps HaloPSA address line3 to city and line4 to state' {
    $json = '{"line1":"3116 Expressway Drive South","line3":"Bohemia","line4":"NY","postcode":"11749","country":"US"}'
    $document = [System.Text.Json.JsonDocument]::Parse($json)
    try {
      $method = [LISSTech.EntitySync.Adapters.Halo.HaloEntityAdapter].GetMethod('MapAddress', [System.Reflection.BindingFlags]'NonPublic, Static')
      $address = $method.Invoke($null, @($document.RootElement))

      $address.Line1 | Should -Be '3116 Expressway Drive South'
      $address.City | Should -Be 'Bohemia'
      $address.State | Should -Be 'NY'
      $address.PostalCode | Should -Be '11749'
      $address.Country | Should -Be 'US'
    }
    finally {
      $document.Dispose()
    }
  }

  It 'Leaves N-central contact fields empty when HaloPSA has no real contact data' {
    $source = [LISSTech.EntitySync.Core.ExternalEntity]::new()
    $source.Vendor = 'HaloPSA'
    $source.EntityType = 'Client'
    $source.Id = '13'
    $source.Name = 'Arista Air Conditioning Corp.'

    $mapper = [LISSTech.EntitySync.Mapping.DefaultEntityMapper]::new()
    $request = $mapper.MapCreate($source, 'NCentral', 'Customer', [LISSTech.EntitySync.Core.MatchOptions]::new())

    $request.Fields.ContainsKey('contactFirstName') | Should -BeFalse
    $request.Fields.ContainsKey('contactLastName') | Should -BeFalse
    $request.Fields.ContainsKey('contactEmail') | Should -BeFalse
    $request.Fields.ContainsKey('phone') | Should -BeFalse
  }

  It 'Maps NetSuite customer name into HaloPSA custom fields' {
    $source = [LISSTech.EntitySync.Core.ExternalEntity]::new()
    $source.Vendor = 'NetSuite'
    $source.EntityType = 'Customer'
    $source.Id = '12345'
    $source.Name = 'GOAT USA Inc. - NetSuite'

    $mapper = [LISSTech.EntitySync.Mapping.DefaultEntityMapper]::new()
    $request = $mapper.MapCreate($source, 'HaloPSA', 'Client', [LISSTech.EntitySync.Core.MatchOptions]::new())

    $request.CustomFields['CFNetSuiteCustomerName'] | Should -Be 'GOAT USA Inc. - NetSuite'
  }

  It 'Does not treat ordinary NetSuite external IDs as missing N-central integration targets' {
    $source = [LISSTech.EntitySync.Core.ExternalEntity]::new()
    $source.Vendor = 'NetSuite'
    $source.EntityType = 'Customer'
    $source.Id = '1658'
    $source.Name = 'Advanced Orthopedics and Joint Preservation'
    $source.ExternalIds['NetSuiteInternalId'] = '1658'

    $matcher = [LISSTech.EntitySync.Matching.WeightedEntityMatcher]::new()
    $targets = [System.Collections.Generic.List[LISSTech.EntitySync.Core.ExternalEntity]]::new()
    $options = [LISSTech.EntitySync.Core.MatchOptions]::new()
    $options.SourceExternalIdName = 'NetSuiteInternalId'
    $index = $matcher.CreateIndex($targets, $options)
    $method = [LISSTech.EntitySync.Commands.NewEntitySyncPlanCommand].GetMethod('CreatePlanItem', [System.Reflection.BindingFlags]'NonPublic, Static')
    $duplicateLcatSourceIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $duplicateLcatSlugs = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

    $item = $method.Invoke($null, @($source, $index, 90, 70, $false, 'NetSuiteInternalId', $false, $false, $duplicateLcatSourceIds, $duplicateLcatSlugs))

    $item.MatchType | Should -Be 'NoMatch'
    $item.Reasons -join '; ' | Should -Not -Match 'N-central integration'
  }

  It 'Flags missing authoritative targets only for N-central integration link plans' {
    $source = [LISSTech.EntitySync.Core.ExternalEntity]::new()
    $source.Vendor = 'HaloPSA'
    $source.EntityType = 'Client'
    $source.Id = '684'
    $source.Name = 'GOAT USA Inc.'
    $source.ExternalIds['NCentralCustomerId'] = '390'

    $matcher = [LISSTech.EntitySync.Matching.WeightedEntityMatcher]::new()
    $targets = [System.Collections.Generic.List[LISSTech.EntitySync.Core.ExternalEntity]]::new()
    $options = [LISSTech.EntitySync.Core.MatchOptions]::new()
    $options.SourceExternalIdName = 'NCentralCustomerId'
    $index = $matcher.CreateIndex($targets, $options)
    $method = [LISSTech.EntitySync.Commands.NewEntitySyncPlanCommand].GetMethod('CreatePlanItem', [System.Reflection.BindingFlags]'NonPublic, Static')
    $duplicateLcatSourceIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $duplicateLcatSlugs = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

    $item = $method.Invoke($null, @($source, $index, 90, 70, $false, 'NCentralCustomerId', $true, $false, $duplicateLcatSourceIds, $duplicateLcatSlugs))

    $item.MatchType | Should -Be 'IntegrationLinkTargetMissing'
    $item.Reasons -join '; ' | Should -Match 'N-central target 390'
  }

  It 'Labels linked external ID matches with the actual target external ID name' {
    $source = [LISSTech.EntitySync.Core.ExternalEntity]::new()
    $source.Vendor = 'HaloPSA'
    $source.EntityType = 'Site'
    $source.Id = '810'
    $source.Name = 'Chicago Shop'
    $source.ExternalIds['NCentralSiteId'] = '353'

    $target = [LISSTech.EntitySync.Core.ExternalEntity]::new()
    $target.Vendor = 'NCentral'
    $target.EntityType = 'Site'
    $target.Id = '353'
    $target.Name = 'Chicago Shop'
    $target.ExternalIds['NCentralSiteId'] = '353'

    $options = [LISSTech.EntitySync.Core.MatchOptions]::new()
    $options.SourceExternalIdName = 'NCentralSiteId'
    $options.TargetExternalIdName = 'NCentralSiteId'
    $matcher = [LISSTech.EntitySync.Matching.WeightedEntityMatcher]::new()
    $targets = [System.Collections.Generic.List[LISSTech.EntitySync.Core.ExternalEntity]]::new()
    $targets.Add($target)

    $matches = $matcher.FindMatches($source, $targets, $options)

    $matches[0].MatchType | Should -Be 'Linked'
    $matches[0].Reasons[0] | Should -Be 'External ID match: NCentralSiteId=353'
  }

  It 'Leaves low-confidence targets blank instead of preselecting weak suggestions' {
    $source = [LISSTech.EntitySync.Core.ExternalEntity]::new()
    $source.Vendor = 'NetSuite'
    $source.EntityType = 'Customer'
    $source.Id = '2851'
    $source.Name = 'Alex Apparel Group Inc.'

    $target = [LISSTech.EntitySync.Core.ExternalEntity]::new()
    $target.Vendor = 'HaloPSA'
    $target.EntityType = 'Client'
    $target.Id = '432'
    $target.Name = 'PPI Apparel Group Inc.'

    $matcher = [LISSTech.EntitySync.Matching.WeightedEntityMatcher]::new()
    $targets = [System.Collections.Generic.List[LISSTech.EntitySync.Core.ExternalEntity]]::new()
    $targets.Add($target)
    $options = [LISSTech.EntitySync.Core.MatchOptions]::new()
    $index = $matcher.CreateIndex($targets, $options)
    $method = [LISSTech.EntitySync.Commands.NewEntitySyncPlanCommand].GetMethod('CreatePlanItem', [System.Reflection.BindingFlags]'NonPublic, Static')
    $duplicateLcatSourceIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $duplicateLcatSlugs = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

    $item = $method.Invoke($null, @($source, $index, 90, 70, $false, 'NetSuiteInternalId', $false, $false, $duplicateLcatSourceIds, $duplicateLcatSlugs))

    $item.Action | Should -Be 'Review'
    $item.MatchType | Should -Be 'LowConfidence'
    $item.Score | Should -BeLessThan 70
    $item.Target | Should -BeNullOrEmpty
    $item.Reasons -join '; ' | Should -Match 'target left blank'
  }

  It 'Maps HaloPSA site parent customer into N-central site create metadata' {
    $source = [LISSTech.EntitySync.Core.ExternalEntity]::new()
    $source.Vendor = 'HaloPSA'
    $source.EntityType = 'Site'
    $source.Id = '810'
    $source.Name = 'Main Office'
    $source.ExternalIds['NCentralCustomerId'] = '390'

    $mapper = [LISSTech.EntitySync.Mapping.DefaultEntityMapper]::new()
    $request = $mapper.MapCreate($source, 'NCentral', 'Site', [LISSTech.EntitySync.Core.MatchOptions]::new())

    $request.CustomFields['externalId'] | Should -Be '810'
    $request.CustomFields['HaloPsaSiteId'] | Should -Be '810'
    $request.CustomFields['NCentralCustomerId'] | Should -Be '390'
  }

  It 'Requires SOAP credentials before creating N-central customers with custom properties' {
    $options = [LISSTech.EntitySync.Adapters.NCentral.NCentralOptions]::new()
    $options.BaseUrl = 'https://ncentral.example.test/'
    $options.UserApiToken = 'token'
    $options.ServiceOrgId = '50'
    $adapter = [LISSTech.EntitySync.Adapters.NCentral.NCentralEntityAdapter]::new($options)
    try {
      $request = [LISSTech.EntitySync.Core.EntityWriteRequest]::new()
      $request.EntityType = 'Customer'
      $request.Name = 'GOAT USA Inc.'
      $request.CustomFields['HaloPsaId'] = '684'

      { $adapter.CreateEntityAsync($request, [Threading.CancellationToken]::None).GetAwaiter().GetResult() } | Should -Throw '*organization custom properties require SOAP credentials*'
    }
    finally {
      $adapter.Dispose()
    }
  }

  It 'Requires SOAP credentials before updating N-central customers' {
    $options = [LISSTech.EntitySync.Adapters.NCentral.NCentralOptions]::new()
    $options.BaseUrl = 'https://ncentral.example.test/'
    $options.UserApiToken = 'token'
    $options.ServiceOrgId = '50'
    $adapter = [LISSTech.EntitySync.Adapters.NCentral.NCentralEntityAdapter]::new($options)
    try {
      $request = [LISSTech.EntitySync.Core.EntityWriteRequest]::new()
      $request.EntityType = 'Customer'
      $request.Id = '390'
      $request.Name = 'GOAT USA Inc.'

      { $adapter.UpdateEntityAsync($request, [Threading.CancellationToken]::None).GetAwaiter().GetResult() } | Should -Throw '*customer update requires SOAP credentials*'
    }
    finally {
      $adapter.Dispose()
    }
  }

  It 'Requires IDs before writing HaloPSA N-central client links' {
    $options = [LISSTech.EntitySync.Adapters.Halo.HaloOptions]::new()
    $options.BaseUrl = 'https://halo.example.test/'
    $options.AccessToken = 'token'
    $options.NCentralIntegrationId = 3
    $adapter = [LISSTech.EntitySync.Adapters.Halo.HaloEntityAdapter]::new($options)
    try {
      { $adapter.UpsertNCentralClientLinkAsync('', 'Source Co', '390', 'Target Co', [Threading.CancellationToken]::None).GetAwaiter().GetResult() } | Should -Throw '*requires a HaloPSA client ID*'
      { $adapter.UpsertNCentralClientLinkAsync('684', 'Source Co', '', 'Target Co', [Threading.CancellationToken]::None).GetAwaiter().GetResult() } | Should -Throw '*requires an N-central customer ID*'
    }
    finally {
      $adapter.Dispose()
    }
  }

  It 'Requires IDs before writing HaloPSA N-central site links' {
    $options = [LISSTech.EntitySync.Adapters.Halo.HaloOptions]::new()
    $options.BaseUrl = 'https://halo.example.test/'
    $options.AccessToken = 'token'
    $options.NCentralIntegrationId = 3
    $adapter = [LISSTech.EntitySync.Adapters.Halo.HaloEntityAdapter]::new($options)
    try {
      { $adapter.UpsertNCentralSiteLinkAsync('', 'Main Office', 'Source Co', '402', 'Target Site', '390', [Threading.CancellationToken]::None).GetAwaiter().GetResult() } | Should -Throw '*requires a HaloPSA site ID*'
      { $adapter.UpsertNCentralSiteLinkAsync('810', 'Main Office', 'Source Co', '', 'Target Site', '390', [Threading.CancellationToken]::None).GetAwaiter().GetResult() } | Should -Throw '*requires an N-central site ID*'
      { $adapter.UpsertNCentralSiteLinkAsync('810', 'Main Office', 'Source Co', '402', 'Target Site', '', [Threading.CancellationToken]::None).GetAwaiter().GetResult() } | Should -Throw '*requires an N-central customer ID*'
    }
    finally {
      $adapter.Dispose()
    }
  }

  It 'Requires a parent customer link before creating N-central sites' {
    $options = [LISSTech.EntitySync.Adapters.NCentral.NCentralOptions]::new()
    $options.BaseUrl = 'https://ncentral.example.test/'
    $options.UserApiToken = 'token'
    $adapter = [LISSTech.EntitySync.Adapters.NCentral.NCentralEntityAdapter]::new($options)
    try {
      $request = [LISSTech.EntitySync.Core.EntityWriteRequest]::new()
      $request.EntityType = 'Site'
      $request.Name = 'Main Office'

      { $adapter.CreateEntityAsync($request, [Threading.CancellationToken]::None).GetAwaiter().GetResult() } | Should -Throw '*requires NCentralCustomerId*'
    }
    finally {
      $adapter.Dispose()
    }
  }

  It 'Round-trips reviewed Excel plan decisions' {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $path = Join-Path ([System.IO.Path]::GetTempPath()) ("entitysync-review-{0}.xlsx" -f [guid]::NewGuid())
    try {
      $plan = [LISSTech.EntitySync.Core.EntitySyncPlan]::new()
      $plan.SourceVendor = 'NetSuite'
      $plan.SourceEntityType = 'Customer'
      $plan.TargetVendor = 'NCentral'
      $plan.TargetEntityType = 'Customer'
      $item = [LISSTech.EntitySync.Core.EntitySyncPlanItem]::new()
      $item.Action = 'Create'
      $item.Source.Name = 'Acme Inc'
      $item.Source.Id = '123'
      [void]$plan.Items.Add($item)

      $plan | Export-EntitySyncPlan -FilePath $path

      $zip = [System.IO.Compression.ZipFile]::Open($path, [System.IO.Compression.ZipArchiveMode]::Update)
      try {
        $entry = $zip.GetEntry('xl/worksheets/sheet1.xml')
        $reader = [System.IO.StreamReader]::new($entry.Open())
        $xml = $reader.ReadToEnd()
        $reader.Dispose()
        $entry.Delete()

        $document = [xml]$xml
        $namespace = [System.Xml.XmlNamespaceManager]::new($document.NameTable)
        $namespace.AddNamespace('x', 'http://schemas.openxmlformats.org/spreadsheetml/2006/main')
        $cell = $document.SelectSingleNode('//x:c[@r=''B2'']', $namespace)
        $cell.RemoveAll()
        [void]$cell.SetAttribute('r', 'B2')
        [void]$cell.SetAttribute('t', 'inlineStr')
        $is = $document.CreateElement('is', 'http://schemas.openxmlformats.org/spreadsheetml/2006/main')
        $text = $document.CreateElement('t', 'http://schemas.openxmlformats.org/spreadsheetml/2006/main')
        $text.InnerText = 'Reject'
        [void]$is.AppendChild($text)
        [void]$cell.AppendChild($is)

        $newEntry = $zip.CreateEntry('xl/worksheets/sheet1.xml')
        $writer = [System.IO.StreamWriter]::new($newEntry.Open(), [System.Text.UTF8Encoding]::new($false))
        $document.Save($writer)
        $writer.Dispose()
      }
      finally {
        $zip.Dispose()
      }

      $reviewed = Import-EntitySyncPlan $path
      $reviewed.Items[0].Action | Should -Be 'None'
      $reviewed.Items[0].Status | Should -Be 'Rejected'
    }
    finally {
      Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }
  }

  It 'Imports Excel workbooks rewritten with shared strings' {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $path = Join-Path ([System.IO.Path]::GetTempPath()) ("entitysync-shared-strings-{0}.xlsx" -f [guid]::NewGuid())
    try {
      $plan = [LISSTech.EntitySync.Core.EntitySyncPlan]::new()
      $plan.SourceVendor = 'HaloPSA'
      $plan.SourceEntityType = 'Client'
      $plan.TargetVendor = 'NCentral'
      $plan.TargetEntityType = 'Customer'
      $item = [LISSTech.EntitySync.Core.EntitySyncPlanItem]::new()
      $item.Action = 'Review'
      $item.Source.Name = 'Acme Inc'
      $item.Source.Id = '123'
      [void]$plan.Items.Add($item)

      $plan | Export-EntitySyncPlan -FilePath $path

      $zip = [System.IO.Compression.ZipFile]::Open($path, [System.IO.Compression.ZipArchiveMode]::Update)
      try {
        $strings = [System.Collections.Generic.List[string]]::new()
        foreach ($entryName in @('xl/worksheets/sheet1.xml', 'xl/worksheets/sheet2.xml')) {
          $entry = $zip.GetEntry($entryName)
          $reader = [System.IO.StreamReader]::new($entry.Open())
          $xml = $reader.ReadToEnd()
          $reader.Dispose()
          $entry.Delete()

          $document = [xml]$xml
          $namespace = [System.Xml.XmlNamespaceManager]::new($document.NameTable)
          $namespace.AddNamespace('x', 'http://schemas.openxmlformats.org/spreadsheetml/2006/main')
          foreach ($cell in $document.SelectNodes('//x:c[@t=''inlineStr'']', $namespace)) {
            $reference = $cell.GetAttribute('r')
            $text = $cell.InnerText
            $index = $strings.Count
            [void]$strings.Add($text)
            $cell.RemoveAll()
            [void]$cell.SetAttribute('r', $reference)
            [void]$cell.SetAttribute('t', 's')
            $value = $document.CreateElement('v', 'http://schemas.openxmlformats.org/spreadsheetml/2006/main')
            $value.InnerText = [string]$index
            [void]$cell.AppendChild($value)
          }

          $newEntry = $zip.CreateEntry($entryName)
          $writer = [System.IO.StreamWriter]::new($newEntry.Open(), [System.Text.UTF8Encoding]::new($false))
          $document.Save($writer)
          $writer.Dispose()
        }

        $existingSharedStrings = $zip.GetEntry('xl/sharedStrings.xml')
        if ($existingSharedStrings) { $existingSharedStrings.Delete() }
        $sharedStringsEntry = $zip.CreateEntry('xl/sharedStrings.xml')
        $sharedStrings = '<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" count="{0}" uniqueCount="{0}">{1}</sst>' -f $strings.Count, (($strings | ForEach-Object { '<si><t>{0}</t></si>' -f [System.Security.SecurityElement]::Escape($_) }) -join '')
        $writer = [System.IO.StreamWriter]::new($sharedStringsEntry.Open(), [System.Text.UTF8Encoding]::new($false))
        $writer.Write($sharedStrings)
        $writer.Dispose()
      }
      finally {
        $zip.Dispose()
      }

      $reviewed = Import-EntitySyncPlan $path
      $reviewed.SourceVendor | Should -Be 'HaloPSA'
      $reviewed.Items[0].Source.Name | Should -Be 'Acme Inc'
    }
    finally {
      Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }
  }

  It 'Round-trips LCAT plan reasons while excluding credential-bearing artifact fields (T044, US3)' {
    $secretToken = 'lcat-export-secret-4f3e2d1c'
    $xlsxPath = Join-Path ([System.IO.Path]::GetTempPath()) ("entitysync-lcat-redacted-{0}.xlsx" -f [guid]::NewGuid())
    $jsonPath = Join-Path ([System.IO.Path]::GetTempPath()) ("entitysync-lcat-redacted-{0}.json" -f [guid]::NewGuid())
    try {
      $plan = [LISSTech.EntitySync.Core.EntitySyncPlan]::new()
      $plan.SourceVendor = 'NCentral'
      $plan.SourceEntityType = 'Site'
      $plan.TargetVendor = 'LCAT'
      $plan.TargetEntityType = 'Customer'

      $item = [LISSTech.EntitySync.Core.EntitySyncPlanItem]::new()
      $item.Action = 'Review'
      $item.Status = 'Review'
      $item.MatchType = 'LcatSourceInvalid'
      $item.Score = 0
      $item.Source.Vendor = 'NCentral'
      $item.Source.EntityType = 'Site'
      $item.Source.Id = 'SITE-901'
      $item.Source.Name = '###'
      $item.Source.ExternalIds['NCentralSiteId'] = 'SITE-901'
      $item.Source.ExternalIds['NCentralCustomerId'] = 'CUST-390'
      $item.Source.ExternalIds['LCATBearerToken'] = $secretToken
      $item.Source.CustomFields['NCentralCustomerName'] = 'GOAT USA Inc.'
      $item.Source.CustomFields['Authorization'] = "Bearer $secretToken"
      $item.Source.CustomFields['LCATBearerToken'] = $secretToken
      [void]$item.Reasons.Add("N-central Site SITE-901 cannot produce a safe LCAT customer-scope slug.")
      [void]$item.Reasons.Add("Duplicate N-central source identifier 'SITE-901' cannot be synced to LCAT customer scopes.")
      [void]$item.Reasons.Add("LCATBearerToken=$secretToken")
      [void]$plan.Items.Add($item)

      $plan | Export-EntitySyncPlan -FilePath $xlsxPath
      $plan | Export-EntitySyncPlan -FilePath $jsonPath

      $reviewed = Import-EntitySyncPlan $xlsxPath
      $reviewedItem = $reviewed.Items[0]
      $reviewedItem.MatchType | Should -Be 'LcatSourceInvalid'
      $reviewedItem.Reasons | Should -Contain "N-central Site SITE-901 cannot produce a safe LCAT customer-scope slug."
      $reviewedItem.Reasons | Should -Contain "Duplicate N-central source identifier 'SITE-901' cannot be synced to LCAT customer scopes."
      $reviewedItem.Reasons | Should -Contain '[credential redacted]'
      $reviewedItem.Source.ExternalIds['NCentralSiteId'] | Should -Be 'SITE-901'
      $reviewedItem.Source.ExternalIds['NCentralCustomerId'] | Should -Be 'CUST-390'
      $reviewedItem.Source.CustomFields['NCentralCustomerName'] | Should -Be 'GOAT USA Inc.'
      $reviewedItem.Source.ExternalIds.Keys | Should -Not -Contain 'LCATBearerToken'
      $reviewedItem.Source.CustomFields.Keys | Should -Not -Contain 'Authorization'
      $reviewedItem.Source.CustomFields.Keys | Should -Not -Contain 'LCATBearerToken'

      $json = Get-Content -Raw $jsonPath
      $json | Should -Not -Match ([regex]::Escape($secretToken))
      $json | Should -Not -Match 'LCATBearerToken'
      $json | Should -Not -Match 'Authorization'

      $plan.Items[0].Source.CustomFields['LCATBearerToken'] | Should -Be $secretToken
    }
    finally {
      Remove-Item -LiteralPath $xlsxPath -Force -ErrorAction SilentlyContinue
      Remove-Item -LiteralPath $jsonPath -Force -ErrorAction SilentlyContinue
    }
  }

  It 'Generates an Excel plan filename when exporting to a directory' {
    $plan = [LISSTech.EntitySync.Core.EntitySyncPlan]::new()
    $plan.SourceVendor = 'NetSuite'
    $plan.SourceEntityType = 'Customer'
    $plan.TargetVendor = 'HaloPSA'
    $plan.TargetEntityType = 'Client'
    $item = [LISSTech.EntitySync.Core.EntitySyncPlanItem]::new()
    $item.Action = 'Review'
    $item.Source.Name = 'Acme Inc'
    [void]$plan.Items.Add($item)

    $file = $plan | Export-EntitySyncPlan -Path ([System.IO.Path]::GetTempPath()) -PassThru
    try {
      $file.Name | Should -Match '^EntitySync-NetSuite-Customer-to-HaloPSA-Client-\d{8}-\d{6}\.xlsx$'
      $file.Exists | Should -BeTrue
    }
    finally {
      Remove-Item -LiteralPath $file.FullName -Force -ErrorAction SilentlyContinue
    }
  }

  It 'Uses context-specific Excel headers without site-only columns for NetSuite to HaloPSA' {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $path = Join-Path ([System.IO.Path]::GetTempPath()) ("entitysync-context-headers-{0}.xlsx" -f [guid]::NewGuid())
    try {
      $plan = [LISSTech.EntitySync.Core.EntitySyncPlan]::new()
      $plan.SourceVendor = 'NetSuite'
      $plan.SourceEntityType = 'Customer'
      $plan.TargetVendor = 'HaloPSA'
      $plan.TargetEntityType = 'Client'
      $source = [LISSTech.EntitySync.Core.ExternalEntity]::new()
      $source.Id = 'S1'
      $source.Name = 'Source Co'
      $target = [LISSTech.EntitySync.Core.ExternalEntity]::new()
      $target.Id = 'T1'
      $target.Name = 'Target Co'
      $item = [LISSTech.EntitySync.Core.EntitySyncPlanItem]::new()
      $item.Action = 'Review'
      $item.Source = $source
      $item.Target = $target
      [void]$plan.Items.Add($item)

      $plan | Export-EntitySyncPlan -FilePath $path

      $zip = [System.IO.Compression.ZipFile]::OpenRead($path)
      try {
        $sheetReader = [System.IO.StreamReader]::new($zip.GetEntry('xl/worksheets/sheet1.xml').Open())
        $tableReader = [System.IO.StreamReader]::new($zip.GetEntry('xl/tables/table1.xml').Open())
        try {
          [xml]$sheet = $sheetReader.ReadToEnd()
          [xml]$table = $tableReader.ReadToEnd()
        }
        finally {
          $sheetReader.Dispose()
          $tableReader.Dispose()
        }

        $ns = [System.Xml.XmlNamespaceManager]::new($sheet.NameTable)
        $ns.AddNamespace('x', 'http://schemas.openxmlformats.org/spreadsheetml/2006/main')
        $sheet.SelectSingleNode('//x:c[@r=''E1'']/x:is/x:t[text()=''NetSuiteCustomerName'']', $ns) | Should -Not -BeNullOrEmpty
        $sheet.SelectSingleNode('//x:c[@r=''F1'']/x:is/x:t[text()=''HaloClientName'']', $ns) | Should -Not -BeNullOrEmpty
        $sheet.SelectSingleNode('//x:c[@r=''G1'']/x:is/x:t[text()=''Score'']', $ns) | Should -Not -BeNullOrEmpty
        $sheet.SelectSingleNode('//x:c[@r=''G1'']/x:is/x:t[text()=''SourceClientName'']', $ns) | Should -BeNullOrEmpty

        $tableNs = [System.Xml.XmlNamespaceManager]::new($table.NameTable)
        $tableNs.AddNamespace('x', 'http://schemas.openxmlformats.org/spreadsheetml/2006/main')
        $table.SelectSingleNode('//x:table[@ref=''A1:N2'']', $tableNs) | Should -Not -BeNullOrEmpty
      }
      finally {
        $zip.Dispose()
      }
    }
    finally {
      Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }
  }

  It 'Adds conditional formatting for source and target name mismatches' {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $path = Join-Path ([System.IO.Path]::GetTempPath()) ("entitysync-conditional-formatting-{0}.xlsx" -f [guid]::NewGuid())
    try {
      $plan = [LISSTech.EntitySync.Core.EntitySyncPlan]::new()
      $plan.SourceVendor = 'HaloPSA'
      $plan.SourceEntityType = 'Client'
      $plan.TargetVendor = 'NCentral'
      $plan.TargetEntityType = 'Customer'
      $source = [LISSTech.EntitySync.Core.ExternalEntity]::new()
      $source.Id = 'S1'
      $source.Name = 'Source Co'
      $target = [LISSTech.EntitySync.Core.ExternalEntity]::new()
      $target.Id = 'T1'
      $target.Name = 'Different Target Co'
      $item = [LISSTech.EntitySync.Core.EntitySyncPlanItem]::new()
      $item.Action = 'Review'
      $item.Source = $source
      $item.Target = $target
      [void]$plan.Items.Add($item)

      $plan | Export-EntitySyncPlan -FilePath $path

      $zip = [System.IO.Compression.ZipFile]::OpenRead($path)
      try {
        $sheetReader = [System.IO.StreamReader]::new($zip.GetEntry('xl/worksheets/sheet1.xml').Open())
        $styleReader = [System.IO.StreamReader]::new($zip.GetEntry('xl/styles.xml').Open())
        $tableReader = [System.IO.StreamReader]::new($zip.GetEntry('xl/tables/table1.xml').Open())
        $relationshipReader = [System.IO.StreamReader]::new($zip.GetEntry('xl/worksheets/_rels/sheet1.xml.rels').Open())
        $workbookReader = [System.IO.StreamReader]::new($zip.GetEntry('xl/workbook.xml').Open())
        $themeReader = [System.IO.StreamReader]::new($zip.GetEntry('xl/theme/theme1.xml').Open())
        $legendReader = [System.IO.StreamReader]::new($zip.GetEntry('xl/worksheets/sheet5.xml').Open())
        try {
          [xml]$sheet = $sheetReader.ReadToEnd()
          [xml]$styles = $styleReader.ReadToEnd()
          [xml]$table = $tableReader.ReadToEnd()
          [xml]$relationships = $relationshipReader.ReadToEnd()
          [xml]$workbook = $workbookReader.ReadToEnd()
          [xml]$theme = $themeReader.ReadToEnd()
          [xml]$legend = $legendReader.ReadToEnd()
        }
        finally {
          $sheetReader.Dispose()
          $styleReader.Dispose()
          $tableReader.Dispose()
          $relationshipReader.Dispose()
          $workbookReader.Dispose()
          $themeReader.Dispose()
          $legendReader.Dispose()
        }

        $sheetNs = [System.Xml.XmlNamespaceManager]::new($sheet.NameTable)
        $sheetNs.AddNamespace('x', 'http://schemas.openxmlformats.org/spreadsheetml/2006/main')
        $rule = $sheet.SelectSingleNode('//x:conditionalFormatting[@sqref=''A2:N1048576'']/x:cfRule[@type=''expression'' and @dxfId=''0'']', $sheetNs)
        $rule | Should -Not -BeNullOrEmpty
        $rule.formula | Should -Be 'AND($F2<>"",$E2<>$F2)'
        $sheet.SelectSingleNode('//x:sheetFormatPr[@defaultRowHeight=''17'' and @customHeight=''1'']', $sheetNs) | Should -Not -BeNullOrEmpty
        $sheet.SelectSingleNode('//x:row[@r=''1'' and @ht=''17'' and @customHeight=''1'']', $sheetNs) | Should -Not -BeNullOrEmpty
        $sheet.SelectSingleNode('//x:c[@r=''A1'' and @s=''1'']', $sheetNs) | Should -Not -BeNullOrEmpty
        $sheet.SelectSingleNode('//x:c[@r=''A2'' and @s=''0'']', $sheetNs) | Should -Not -BeNullOrEmpty

        $styleNs = [System.Xml.XmlNamespaceManager]::new($styles.NameTable)
        $styleNs.AddNamespace('x', 'http://schemas.openxmlformats.org/spreadsheetml/2006/main')
        $styles.SelectSingleNode('//x:fonts[@count=''2'']/x:font[x:name[@val=''Aptos Narrow''] and x:sz[@val=''11'']]', $styleNs) | Should -Not -BeNullOrEmpty
        $styles.SelectSingleNode('//x:fonts[@count=''2'']/x:font[x:name[@val=''Aptos Display''] and x:sz[@val=''11''] and x:b]', $styleNs) | Should -Not -BeNullOrEmpty
        $styles.SelectSingleNode('//x:cellXfs[@count=''2'']/x:xf[@fontId=''0'']/x:alignment[@vertical=''center'' and @indent=''1'']', $styleNs) | Should -Not -BeNullOrEmpty
        $styles.SelectSingleNode('//x:cellXfs[@count=''2'']/x:xf[@fontId=''1'']/x:alignment[@vertical=''center'' and @indent=''1'']', $styleNs) | Should -Not -BeNullOrEmpty
        $styles.SelectSingleNode('//x:dxfs[@count=''1'']/x:dxf/x:font/x:color[@rgb=''FF9C5700'']', $styleNs) | Should -Not -BeNullOrEmpty
        $styles.SelectSingleNode('//x:dxfs[@count=''1'']/x:dxf/x:fill/x:patternFill[@patternType=''solid'']/x:fgColor[@rgb=''FFFFEB9C'']', $styleNs) | Should -Not -BeNullOrEmpty
        $styles.SelectSingleNode('//x:dxfs[@count=''1'']/x:dxf/x:fill/x:patternFill[@patternType=''solid'']/x:bgColor[@rgb=''FFFFEB9C'']', $styleNs) | Should -Not -BeNullOrEmpty

        $tableNs = [System.Xml.XmlNamespaceManager]::new($table.NameTable)
        $tableNs.AddNamespace('x', 'http://schemas.openxmlformats.org/spreadsheetml/2006/main')
        $table.SelectSingleNode('//x:tableStyleInfo[@name=''TableStyleMedium12'']', $tableNs) | Should -Not -BeNullOrEmpty
        $table.SelectSingleNode('//x:table[@ref=''A1:N2'']', $tableNs) | Should -Not -BeNullOrEmpty

        $relationshipNs = [System.Xml.XmlNamespaceManager]::new($relationships.NameTable)
        $relationshipNs.AddNamespace('r', 'http://schemas.openxmlformats.org/package/2006/relationships')
        $relationships.SelectSingleNode('//r:Relationship[@Type=''http://schemas.openxmlformats.org/officeDocument/2006/relationships/table'' and @Target=''../tables/table1.xml'']', $relationshipNs) | Should -Not -BeNullOrEmpty

        $workbookNs = [System.Xml.XmlNamespaceManager]::new($workbook.NameTable)
        $workbookNs.AddNamespace('x', 'http://schemas.openxmlformats.org/spreadsheetml/2006/main')
        $workbookNs.AddNamespace('r', 'http://schemas.openxmlformats.org/officeDocument/2006/relationships')
        $workbook.SelectSingleNode('//x:sheet[@name=''Legend'' and @r:id=''rId5'']', $workbookNs) | Should -Not -BeNullOrEmpty

        $themeNs = [System.Xml.XmlNamespaceManager]::new($theme.NameTable)
        $themeNs.AddNamespace('a', 'http://schemas.openxmlformats.org/drawingml/2006/main')
        $theme.SelectSingleNode('/a:theme[@name=''Integral'']/a:themeElements/a:fontScheme[@name=''Integral'']', $themeNs) | Should -Not -BeNullOrEmpty

        $legendNs = [System.Xml.XmlNamespaceManager]::new($legend.NameTable)
        $legendNs.AddNamespace('x', 'http://schemas.openxmlformats.org/spreadsheetml/2006/main')
        $legend.SelectSingleNode('//x:c[@r=''A1'' and @s=''1'']/x:is/x:t[text()=''Term'']', $legendNs) | Should -Not -BeNullOrEmpty
        $legend.SelectSingleNode('//x:c[@r=''A2'']/x:is/x:t[text()=''Score'']', $legendNs) | Should -Not -BeNullOrEmpty
      }
      finally {
        $zip.Dispose()
      }
    }
    finally {
      Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }
  }

  It 'Includes site parent client context in Excel review workbooks' {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $path = Join-Path ([System.IO.Path]::GetTempPath()) ("entitysync-site-context-{0}.xlsx" -f [guid]::NewGuid())
    try {
      $plan = [LISSTech.EntitySync.Core.EntitySyncPlan]::new()
      $plan.SourceVendor = 'HaloPSA'
      $plan.SourceEntityType = 'Site'
      $plan.TargetVendor = 'NCentral'
      $plan.TargetEntityType = 'Site'
      $source = [LISSTech.EntitySync.Core.ExternalEntity]::new()
      $source.Id = '810'
      $source.Name = 'Main Office'
      $source.ExternalIds['HaloPsaClientId'] = '684'
      $source.CustomFields['HaloPsaClientName'] = 'GOAT USA Inc.'
      $target = [LISSTech.EntitySync.Core.ExternalEntity]::new()
      $target.Id = '402'
      $target.Name = 'Main Office'
      $target.ExternalIds['NCentralCustomerId'] = '390'
      $target.CustomFields['NCentralCustomerName'] = 'GOAT USA N-central'
      $item = [LISSTech.EntitySync.Core.EntitySyncPlanItem]::new()
      $item.Action = 'Update'
      $item.Source = $source
      $item.Target = $target
      [void]$plan.Items.Add($item)

      $plan | Export-EntitySyncPlan -FilePath $path

      $zip = [System.IO.Compression.ZipFile]::OpenRead($path)
      try {
        $reader = [System.IO.StreamReader]::new($zip.GetEntry('xl/worksheets/sheet1.xml').Open())
        try { [xml]$sheet = $reader.ReadToEnd() }
        finally { $reader.Dispose() }

        $ns = [System.Xml.XmlNamespaceManager]::new($sheet.NameTable)
        $ns.AddNamespace('x', 'http://schemas.openxmlformats.org/spreadsheetml/2006/main')
        $sheet.SelectSingleNode('//x:c[@r=''E1'']/x:is/x:t[text()=''HaloSiteName'']', $ns) | Should -Not -BeNullOrEmpty
        $sheet.SelectSingleNode('//x:c[@r=''F1'']/x:is/x:t[text()=''NCentralSiteName'']', $ns) | Should -Not -BeNullOrEmpty
        $sheet.SelectSingleNode('//x:c[@r=''G1'']/x:is/x:t[text()=''HaloClientName'']', $ns) | Should -Not -BeNullOrEmpty
        $sheet.SelectSingleNode('//x:c[@r=''H1'']/x:is/x:t[text()=''NCentralCustomerName'']', $ns) | Should -Not -BeNullOrEmpty
        $sheet.SelectSingleNode('//x:c[@r=''I1'']/x:is/x:t[text()=''HaloClientId'']', $ns) | Should -Not -BeNullOrEmpty
        $sheet.SelectSingleNode('//x:c[@r=''J1'']/x:is/x:t[text()=''NCentralCustomerId'']', $ns) | Should -Not -BeNullOrEmpty
        $sheet.SelectSingleNode('//x:c[@r=''G2'']/x:is/x:t[text()=''GOAT USA Inc.'']', $ns) | Should -Not -BeNullOrEmpty
        $sheet.SelectSingleNode('//x:c[@r=''H2'']/x:is/x:t[text()=''GOAT USA N-central'']', $ns) | Should -Not -BeNullOrEmpty
        $sheet.SelectSingleNode('//x:c[@r=''I2'']/x:is/x:t[text()=''684'']', $ns) | Should -Not -BeNullOrEmpty
        $sheet.SelectSingleNode('//x:c[@r=''J2'']/x:is/x:t[text()=''390'']', $ns) | Should -Not -BeNullOrEmpty
      }
      finally {
        $zip.Dispose()
      }
    }
    finally {
      Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }
  }

  It 'Imports Excel override target selections' {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $path = Join-Path ([System.IO.Path]::GetTempPath()) ("entitysync-override-{0}.xlsx" -f [guid]::NewGuid())
    try {
      $plan = [LISSTech.EntitySync.Core.EntitySyncPlan]::new()
      $plan.SourceVendor = 'NetSuite'
      $plan.SourceEntityType = 'Customer'
      $plan.TargetVendor = 'HaloPSA'
      $plan.TargetEntityType = 'Client'
      $source = [LISSTech.EntitySync.Core.ExternalEntity]::new()
      $source.Id = 'S1'
      $source.Name = 'Source Co'
      $wrongTarget = [LISSTech.EntitySync.Core.ExternalEntity]::new()
      $wrongTarget.Id = 'T1'
      $wrongTarget.Name = 'Wrong Target'
      $rightTarget = [LISSTech.EntitySync.Core.ExternalEntity]::new()
      $rightTarget.Id = 'T2'
      $rightTarget.Name = 'Right Target'
      $item = [LISSTech.EntitySync.Core.EntitySyncPlanItem]::new()
      $item.Action = 'Review'
      $item.Source = $source
      $item.Target = $wrongTarget
      [void]$plan.Items.Add($item)
      [void]$plan.TargetCandidates.Add($wrongTarget)
      [void]$plan.TargetCandidates.Add($rightTarget)

      $plan | Export-EntitySyncPlan -FilePath $path

      $zip = [System.IO.Compression.ZipFile]::Open($path, [System.IO.Compression.ZipArchiveMode]::Update)
      try {
        $entry = $zip.GetEntry('xl/worksheets/sheet1.xml')
        $reader = [System.IO.StreamReader]::new($entry.Open())
        $xml = $reader.ReadToEnd()
        $reader.Dispose()
        $entry.Delete()

        $document = [xml]$xml
        $namespace = [System.Xml.XmlNamespaceManager]::new($document.NameTable)
        $namespace.AddNamespace('x', 'http://schemas.openxmlformats.org/spreadsheetml/2006/main')
        $row = $document.SelectSingleNode('//x:row[@r=''2'']', $namespace)
        $cell = $document.CreateElement('c', 'http://schemas.openxmlformats.org/spreadsheetml/2006/main')
        [void]$cell.SetAttribute('r', 'F2')
        [void]$cell.SetAttribute('t', 'inlineStr')
        $is = $document.CreateElement('is', 'http://schemas.openxmlformats.org/spreadsheetml/2006/main')
        $text = $document.CreateElement('t', 'http://schemas.openxmlformats.org/spreadsheetml/2006/main')
        $text.InnerText = 'Right Target'
        [void]$is.AppendChild($text)
        [void]$cell.AppendChild($is)
        [void]$row.AppendChild($cell)

        $newEntry = $zip.CreateEntry('xl/worksheets/sheet1.xml')
        $writer = [System.IO.StreamWriter]::new($newEntry.Open(), [System.Text.UTF8Encoding]::new($false))
        $document.Save($writer)
        $writer.Dispose()
      }
      finally {
        $zip.Dispose()
      }

      $reviewed = Import-EntitySyncPlan $path
      $reviewed.Items[0].Target.Id | Should -Be 'T2'
      $reviewed.Items[0].Action | Should -Be 'Link'
      $reviewed.Items[0].MatchType | Should -Be 'ReviewerOverride'
    }
    finally {
      Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }
  }

  It 'Lets Create decisions ignore stale target cells' {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $path = Join-Path ([System.IO.Path]::GetTempPath()) ("entitysync-create-stale-target-{0}.xlsx" -f [guid]::NewGuid())
    try {
      $plan = [LISSTech.EntitySync.Core.EntitySyncPlan]::new()
      $plan.SourceVendor = 'HaloPSA'
      $plan.SourceEntityType = 'Client'
      $plan.TargetVendor = 'NCentral'
      $plan.TargetEntityType = 'Customer'
      $source = [LISSTech.EntitySync.Core.ExternalEntity]::new()
      $source.Id = 'S1'
      $source.Name = 'Source Co'
      $target = [LISSTech.EntitySync.Core.ExternalEntity]::new()
      $target.Id = 'T1'
      $target.Name = 'Existing Target'
      $item = [LISSTech.EntitySync.Core.EntitySyncPlanItem]::new()
      $item.Action = 'Review'
      $item.Source = $source
      $item.Target = $target
      [void]$plan.Items.Add($item)
      [void]$plan.TargetCandidates.Add($target)

      $plan | Export-EntitySyncPlan -FilePath $path

      $zip = [System.IO.Compression.ZipFile]::Open($path, [System.IO.Compression.ZipArchiveMode]::Update)
      try {
        $entry = $zip.GetEntry('xl/worksheets/sheet1.xml')
        $reader = [System.IO.StreamReader]::new($entry.Open())
        $xml = $reader.ReadToEnd()
        $reader.Dispose()
        $entry.Delete()

        $document = [xml]$xml
        $namespace = [System.Xml.XmlNamespaceManager]::new($document.NameTable)
        $namespace.AddNamespace('x', 'http://schemas.openxmlformats.org/spreadsheetml/2006/main')
        $cell = $document.SelectSingleNode('//x:c[@r=''B2'']', $namespace)
        $cell.RemoveAll()
        [void]$cell.SetAttribute('r', 'B2')
        [void]$cell.SetAttribute('t', 'inlineStr')
        $is = $document.CreateElement('is', 'http://schemas.openxmlformats.org/spreadsheetml/2006/main')
        $text = $document.CreateElement('t', 'http://schemas.openxmlformats.org/spreadsheetml/2006/main')
        $text.InnerText = 'Create'
        [void]$is.AppendChild($text)
        [void]$cell.AppendChild($is)

        $newEntry = $zip.CreateEntry('xl/worksheets/sheet1.xml')
        $writer = [System.IO.StreamWriter]::new($newEntry.Open(), [System.Text.UTF8Encoding]::new($false))
        $document.Save($writer)
        $writer.Dispose()
      }
      finally {
        $zip.Dispose()
      }

      $reviewed = Import-EntitySyncPlan $path
      $reviewed.Items[0].Action | Should -Be 'Create'
      $reviewed.Items[0].Target | Should -BeNullOrEmpty
    }
    finally {
      Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }
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
