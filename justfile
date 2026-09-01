set shell := ["pwsh", "-NoProfile", "-Command"]
set dotenv-load

project_root      := justfile_directory()
source_project    := project_root / "src" / "LISSTech.EntitySync.csproj"
module_source_dir := project_root / "Module"
module_manifest   := module_source_dir / "LISSTech.EntitySync.psd1"
build_dir         := project_root / "Build"
build_module_dir  := build_dir / "Module"
build_manifest    := build_module_dir / "LISSTech.EntitySync.psd1"
release_dir       := project_root / "Release"
package_dir       := release_dir / "Packages"
style_script      := project_root / "scripts" / "just-style.ps1"
configuration     := "Release"
module_name       := "LISSTech.EntitySync"
mcp_project       := project_root / "mcp" / "LISSTech.EntitySync.Mcp.csproj"
mcp_publish_dir   := project_root / "Build" / "Mcp"
scheduler_project       := project_root / "scheduler" / "LISSTech.EntitySync.Scheduler.csproj"
scheduler_publish_dir   := project_root / "Build" / "Scheduler"
nswag_config      := project_root / "nswag.json"
generated_client  := project_root / "src" / "Adapters" / "LTAC" / "Generated" / "AgentControllerClient.g.cs"
platform_tests     := project_root / "Tests" / "LISSTech.EntitySync.Platform.Tests" / "LISSTech.EntitySync.Platform.Tests.csproj"
signing_cert      := env("CODE_SIGNING_CERTIFICATE_THUMBPRINT", "")
timestamp_url     := env("TIMESTAMP_URL", "http://timestamp.digicert.com")
psgallery_key     := env("PSGALLERY_API_KEY", "")

[private]
default:
    @just --list

# Show current module version
[group('version')]
[script('pwsh', '-NoProfile')]
[extension('.ps1')]
version:
    . '{{ style_script }}'
    $manifest = Get-Content '{{ module_manifest }}' -Raw
    if ($manifest -match "ModuleVersion\s*=\s*'([^']+)'") {
        Write-JustStep -Icon '📦' -Fallback '[version]' -Text "{{ module_name }} $($Matches[1])"
    } else {
        Write-Error 'Could not extract ModuleVersion.'
        exit 1
    }

# Prepare Build/ and Release/ directories
[group('build')]
[script('pwsh', '-NoProfile')]
[extension('.ps1')]
init:
    . '{{ style_script }}'
    $ErrorActionPreference = 'Stop'
    Invoke-JustTimed -Icon '🧰' -Fallback '[init]' -Text 'Preparing artifact directories' -Script {
        foreach ($dir in @('{{ build_dir }}', '{{ build_module_dir }}', '{{ release_dir }}', '{{ package_dir }}')) {
            if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        }
    }

# Restore .NET and local tool dependencies
[group('build')]
[script('pwsh', '-NoProfile')]
[extension('.ps1')]
restore:
    . '{{ style_script }}'
    $ErrorActionPreference = 'Stop'
    Invoke-JustTimed -Icon '📥' -Fallback '[restore]' -Text 'Restoring dependencies' -Script {
        dotnet restore '{{ source_project }}' --verbosity minimal
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        dotnet restore '{{ mcp_project }}' --verbosity minimal
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        dotnet restore '{{ platform_tests }}' --verbosity minimal
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        dotnet tool restore
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

# Compile binary module into Build/Module; never writes DLLs into Module/
[group('build')]
[script('pwsh', '-NoProfile')]
[extension('.ps1')]
build: init restore
    . '{{ style_script }}'
    $ErrorActionPreference = 'Stop'
    Invoke-JustTimed -Icon '🔨' -Fallback '[build]' -Text 'Building {{ module_name }} into Build\Module' -Script {
        Get-ChildItem '{{ build_module_dir }}' -Filter 'LISSTech.EntitySync*.dll' -File -ErrorAction SilentlyContinue | Remove-Item -Force
        Get-ChildItem '{{ build_module_dir }}' -Filter 'LISSTech.EntitySync*.pdb' -File -ErrorAction SilentlyContinue | Remove-Item -Force
        dotnet build '{{ source_project }}' --configuration '{{ configuration }}' --no-restore --verbosity minimal
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        $manifest = '{{ build_manifest }}'
        if (-not (Test-Path -LiteralPath $manifest)) { throw "Build manifest missing: $manifest" }
        $dll = Get-Item -LiteralPath (Join-Path '{{ build_module_dir }}' '{{ module_name }}.dll')
        Write-JustStep -Icon '📏' -Fallback '[size]' -Text ('{0} ({1:N0} KB)' -f $dll.Name, ($dll.Length / 1KB)) -ForegroundColor DarkGray
    }

# Build with analyzers and warnings as errors
[group('quality')]
[script('pwsh', '-NoProfile')]
[extension('.ps1')]
analyze: restore
    . '{{ style_script }}'
    $ErrorActionPreference = 'Stop'
    Invoke-JustTimed -Icon '🔬' -Fallback '[analyze]' -Text 'Running C# analyzers' -Script {
        dotnet build '{{ source_project }}' --configuration '{{ configuration }}' --no-restore --verbosity minimal -p:RunAnalyzers=true -p:TreatWarningsAsErrors=true
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        dotnet build '{{ mcp_project }}' --configuration '{{ configuration }}' --no-restore --verbosity minimal -p:RunAnalyzers=true -p:TreatWarningsAsErrors=true
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

# Verify C# formatting, module manifest, and PowerShell files when PSScriptAnalyzer is installed
[group('quality')]
[script('pwsh', '-NoProfile')]
[extension('.ps1')]
lint: build
    . '{{ style_script }}'
    $ErrorActionPreference = 'Stop'
    Invoke-JustTimed -Icon '🧼' -Fallback '[lint]' -Text 'Linting and formatting checks' -Script {
        dotnet format '{{ source_project }}' --verify-no-changes --verbosity minimal
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

        Test-ModuleManifest '{{ build_manifest }}' | Out-Null

        if (Get-Command Invoke-ScriptAnalyzer -ErrorAction SilentlyContinue) {
            $psRoots = @('{{ module_source_dir }}', '{{ project_root }}\scripts', '{{ project_root }}\Tests') |
                Where-Object { Test-Path -LiteralPath $_ }
            $psFiles = foreach ($psRoot in $psRoots) {
                Get-ChildItem -LiteralPath $psRoot -Recurse -Include *.ps1,*.psm1,*.psd1 -File
            }
            $excludedRules = @(
                'PSAvoidUsingWriteHost',
                'PSUseBOMForUnicodeEncodedFile',
                'PSUseShouldProcessForStateChangingFunctions',
                'PSUseSingularNouns'
            )
            $findings = foreach ($psFile in $psFiles) {
                Invoke-ScriptAnalyzer -Path $psFile.FullName -Severity Error,Warning -ExcludeRule $excludedRules
            }
            if ($findings) {
                $findings | Format-Table -AutoSize
                throw 'PSScriptAnalyzer reported findings.'
            }
        } else {
            Write-JustStep -Icon '⚠️' -Fallback '[warn]' -Text 'PSScriptAnalyzer not installed; skipping PowerShell lint' -ForegroundColor Yellow
        }
    }

# Apply C# formatting fixes
[group('quality')]
[script('pwsh', '-NoProfile')]
[extension('.ps1')]
format: restore
    . '{{ style_script }}'
    $ErrorActionPreference = 'Stop'
    Invoke-JustTimed -Icon '✨' -Fallback '[format]' -Text 'Formatting C# source' -Script {
        dotnet format '{{ source_project }}' --verbosity minimal
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

# Generate the typed AgentController client from the pinned OpenAPI contract
[group('generate')]
[script('pwsh', '-NoProfile')]
[extension('.ps1')]
generate-agentcontroller-client: restore
    . '{{ style_script }}'
    $ErrorActionPreference = 'Stop'
    Invoke-JustTimed -Icon '🤖' -Fallback '[generate]' -Text 'Generating AgentController client' -Script {
        dotnet tool run nswag run '{{ nswag_config }}'
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

# Regenerate and fail when the checked-in AgentController client is stale
[group('generate')]
[script('pwsh', '-NoProfile')]
[extension('.ps1')]
check-agentcontroller-client: generate-agentcontroller-client
    . '{{ style_script }}'
    $ErrorActionPreference = 'Stop'
    Invoke-JustTimed -Icon '🧬' -Fallback '[generated]' -Text 'Checking generated client freshness' -Script {
        git diff --exit-code -- '{{ generated_client }}'
        if ($LASTEXITCODE -ne 0) { throw 'Generated AgentController client is stale. Run just generate-agentcontroller-client and commit the result.' }
    }

# Import Build/Module in a clean process and list exported commands
[group('quality')]
[script('pwsh', '-NoProfile')]
[extension('.ps1')]
test-load: build
    . '{{ style_script }}'
    $ErrorActionPreference = 'Stop'
    Invoke-JustTimed -Icon '🚪' -Fallback '[load]' -Text 'Testing module import from Build\Module' -Script {
        pwsh -NoProfile -NonInteractive -Command "Import-Module '{{ build_manifest }}' -Force; Get-Command -Module {{ module_name }} | Select-Object -ExpandProperty Name"
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

# Run Pester against Build/Module to avoid locking Module/
[group('quality')]
[script('pwsh', '-NoProfile')]
[extension('.ps1')]
test: build
    . '{{ style_script }}'
    $ErrorActionPreference = 'Stop'
    Invoke-JustTimed -Icon '🧪' -Fallback '[test]' -Text 'Running Pester suite against Build\Module' -Script {
        if (-not (Get-Command Invoke-Pester -ErrorAction SilentlyContinue)) { throw 'Pester is not installed. Install-Module Pester -Scope CurrentUser.' }
        $old = $env:LISSTECH_ENTITYSYNC_TEST_MODULE_PATH
        $oldDatabaseUrl = $env:DATABASE_URL
        try {
            $env:DATABASE_URL = $null
            $env:LISSTECH_ENTITYSYNC_TEST_MODULE_PATH = '{{ build_manifest }}'
            $result = Invoke-Pester -Path '{{ project_root }}\Tests' -Output Detailed -PassThru
            if ($result.FailedCount -gt 0) { throw "$($result.FailedCount) test(s) failed." }
            Write-JustStep -Icon '🧾' -Fallback '[tests]' -Text "$($result.PassedCount) test(s) passed" -ForegroundColor Green
        } finally {
            $env:LISSTECH_ENTITYSYNC_TEST_MODULE_PATH = $old
            $env:DATABASE_URL = $oldDatabaseUrl
        }
        dotnet test '{{ platform_tests }}' --configuration '{{ configuration }}' --no-restore --verbosity minimal
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

# Full local quality gate
[group('quality')]
check: check-agentcontroller-client lint analyze test-load test mcp-build mcp-compose-config

# Generate external help from docs/ into source en-US; build copies it into Build/Module
[group('docs')]
[script('pwsh', '-NoProfile')]
[extension('.ps1')]
external-help:
    . '{{ style_script }}'
    $ErrorActionPreference = 'Stop'
    Invoke-JustTimed -Icon '📚' -Fallback '[docs]' -Text 'Generating external help' -Script {
        if (-not (Get-Module -ListAvailable platyPS)) { throw 'platyPS is not installed. Install-Module platyPS -Scope CurrentUser.' }
        New-ExternalHelp -Path '{{ project_root }}\docs' -OutputPath '{{ project_root }}\en-US' -Force
    }

# Sign Build/Module files when CODE_SIGNING_CERTIFICATE_THUMBPRINT is set
[group('release')]
[script('pwsh', '-NoProfile')]
[extension('.ps1')]
sign: build
    . '{{ style_script }}'
    $ErrorActionPreference = 'Stop'
    Invoke-JustTimed -Icon '🔏' -Fallback '[sign]' -Text 'Signing Build\Module artifacts' -Script {
        $thumbprint = '{{ signing_cert }}'
        if (-not $thumbprint) {
            Write-JustStep -Icon '⚠️' -Fallback '[warn]' -Text 'CODE_SIGNING_CERTIFICATE_THUMBPRINT not set; skipping signing' -ForegroundColor Yellow
            return
        }

        $cert = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -CodeSigningCert -ErrorAction SilentlyContinue |
            Where-Object Thumbprint -eq $thumbprint |
            Select-Object -First 1
        if (-not $cert) { throw "Code signing certificate '$thumbprint' not found." }

        $files = @('{{ build_manifest }}') + @(Get-ChildItem '{{ build_module_dir }}' -Filter *.dll | Select-Object -ExpandProperty FullName)
        foreach ($file in $files) {
            $sig = Set-AuthenticodeSignature -FilePath $file -Certificate $cert -TimestampServer '{{ timestamp_url }}' -HashAlgorithm SHA256
            if ($sig.Status -ne 'Valid') { throw "Signing failed for $file`: $($sig.StatusMessage)" }
            Write-JustStep -Icon '🔏' -Fallback '[signed]' -Text ([System.IO.Path]::GetFileName($file)) -ForegroundColor Green
        }
    }

# Require configured signing for publish-grade release tasks
[group('release')]
[script('pwsh', '-NoProfile')]
[extension('.ps1')]
require-signing:
    if (-not '{{ signing_cert }}') { Write-Error 'CODE_SIGNING_CERTIFICATE_THUMBPRINT is required for publish-grade release tasks.'; exit 1 }

# Create final Release artifact zip from Build/Module
[group('release')]
[script('pwsh', '-NoProfile')]
[extension('.ps1')]
package: check sign
    . '{{ style_script }}'
    $ErrorActionPreference = 'Stop'
    Invoke-JustTimed -Icon '📦' -Fallback '[package]' -Text 'Packaging final Release artifact' -Script {
        $manifest = Import-PowerShellDataFile -Path '{{ build_manifest }}'
        $version = $manifest.ModuleVersion.ToString()
        if (-not (Test-Path -LiteralPath '{{ package_dir }}')) { New-Item -ItemType Directory -Path '{{ package_dir }}' -Force | Out-Null }
        $zip = Join-Path '{{ package_dir }}' "{{ module_name }}.$version.zip"
        if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
        $stageRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("{{ module_name }}-package-" + [guid]::NewGuid().ToString('N'))
        $stageModule = Join-Path $stageRoot '{{ module_name }}'
        try {
            New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null
            Copy-Item -Path '{{ build_module_dir }}' -Destination $stageModule -Recurse
            Compress-Archive -Path $stageModule -DestinationPath $zip -Force
        } finally {
            Remove-Item -LiteralPath $stageRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
        Write-JustStep -Icon '📦' -Fallback '[zip]' -Text $zip -ForegroundColor Green
    }

# Build, check, sign when configured, and create Release artifacts
[group('release')]
release: clean package

# Publish Build/Module to PSGallery. Requires signing cert and PSGALLERY_API_KEY.
[group('release')]
[script('pwsh', '-NoProfile')]
[extension('.ps1')]
publish: require-signing check sign
    . '{{ style_script }}'
    $ErrorActionPreference = 'Stop'
    Invoke-JustTimed -Icon '🚀' -Fallback '[publish]' -Text 'Publishing to PSGallery' -Script {
        $apiKey = '{{ psgallery_key }}'
        if (-not $apiKey) { throw 'PSGALLERY_API_KEY is not set.' }
        Test-ModuleManifest '{{ build_manifest }}' | Out-Null
        Publish-Module -Path '{{ build_module_dir }}' -Repository PSGallery -NuGetApiKey $apiKey -Force
    }

# Remove Build/ and Release/ artifacts
[group('build')]
[script('pwsh', '-NoProfile')]
[extension('.ps1')]
clean:
    . '{{ style_script }}'
    $ErrorActionPreference = 'Stop'
    Invoke-JustTimed -Icon '🧹' -Fallback '[clean]' -Text 'Removing Build and Release artifacts' -Script {
        foreach ($path in @('{{ build_dir }}', '{{ release_dir }}')) {
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Stop }
        }
        Get-ChildItem '{{ project_root }}\src', '{{ project_root }}\mcp', '{{ project_root }}\tests' -Directory -Recurse -ErrorAction SilentlyContinue |
            Where-Object Name -In @('bin', 'obj') |
            Sort-Object FullName -Descending |
            Remove-Item -Recurse -Force -ErrorAction Stop
    }

# Build the EntitySync MCP server as a self-contained single-file binary
[group('mcp')]
[script('pwsh', '-NoProfile')]
[extension('.ps1')]
mcp-build:
    . '{{ style_script }}'
    $ErrorActionPreference = 'Stop'
    $rid = if ($IsWindows) { 'win-x64' } elseif ($IsMacOS) { 'osx-arm64' } else { 'linux-x64' }
    Invoke-JustTimed -Icon '🔧' -Fallback '[mcp-build]' -Text "Building MCP server ($rid)" -Script {
        dotnet publish '{{ mcp_project }}' -c Release -r $rid -o '{{ mcp_publish_dir }}' -p:PublishSingleFile=true -p:SelfContained=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true --verbosity minimal
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        $binary = Get-ChildItem '{{ mcp_publish_dir }}' -Filter 'lisstech-entitysync-mcp*' -File | Select-Object -First 1
        Write-JustStep -Icon '📦' -Fallback '[mcp]' -Text ('{0} ({1:N0} KB)' -f $binary.Name, ($binary.Length / 1KB)) -ForegroundColor Green
    }

# Run the MCP server locally via stdio
[group('mcp')]
[script('pwsh', '-NoProfile')]
[extension('.ps1')]
mcp-run: mcp-build
    . '{{ style_script }}'
    $binary = Get-ChildItem '{{ mcp_publish_dir }}' -Filter 'lisstech-entitysync-mcp*' -File | Select-Object -First 1
    & $binary.FullName

# Build the EntitySync scheduler as a self-contained single-file binary
[group('scheduler')]
[script('pwsh', '-NoProfile')]
[extension('.ps1')]
scheduler-build:
    . '{{ style_script }}'
    $ErrorActionPreference = 'Stop'
    $rid = if ($IsWindows) { 'win-x64' } elseif ($IsMacOS) { 'osx-arm64' } else { 'linux-x64' }
    Invoke-JustTimed -Icon '🔧' -Fallback '[scheduler-build]' -Text "Building scheduler ($rid)" -Script {
        dotnet publish '{{ scheduler_project }}' -c Release -r $rid -o '{{ scheduler_publish_dir }}' -p:PublishSingleFile=true -p:SelfContained=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true --verbosity minimal
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        $binary = Get-ChildItem '{{ scheduler_publish_dir }}' -Filter 'lisstech-entitysync-scheduler*' -File | Select-Object -First 1
        Write-JustStep -Icon '📦' -Fallback '[scheduler]' -Text ('{0} ({1:N0} KB)' -f $binary.Name, ($binary.Length / 1KB)) -ForegroundColor Green
    }

# Run the scheduler locally
[group('scheduler')]
[script('pwsh', '-NoProfile')]
[extension('.ps1')]
scheduler-run: scheduler-build
    . '{{ style_script }}'
    $binary = Get-ChildItem '{{ scheduler_publish_dir }}' -Filter 'lisstech-entitysync-scheduler*' -File | Select-Object -First 1
    & $binary.FullName

# Build the production scheduler container
[group('scheduler')]
scheduler-docker-build:
    docker compose --file '{{ project_root }}/docker-compose.yaml' build entitysync-scheduler

# Build both production application containers used by docker-compose.yaml and Coolify
[group('mcp')]
mcp-docker-build:
    docker compose --file '{{ project_root }}/docker-compose.yaml' build entitysync-mcp entitysync-scheduler

# Validate the Coolify Compose model without starting it
[group('mcp')]
[script('pwsh', '-NoProfile')]
[extension('.ps1')]
mcp-compose-config:
    $env:MCP_OAUTH_AUTHORITY = 'https://auth.example.com'
    $env:MCP_OAUTH_RESOURCE = 'https://mcp.example.com/mcp'
    $env:MCP_OAUTH_AUDIENCE = 'https://mcp.example.com/mcp'
    $env:POSTGRES_PASSWORD = 'compose-validation-only'
    $env:DATABASE_URL = 'Host=entitysync-db;Database=entitysync;Username=entitysync;Password=compose-validation-only'
    $env:OTEL_EXPORTER_OTLP_LOGS_ENDPOINT = 'https://logfire-us.pydantic.dev/v1/logs'
    $env:OTEL_EXPORTER_OTLP_HEADERS = 'Authorization=compose-validation-only'
    $env:HALO_BASE_URL = 'https://halo.example.com'
    $env:HALO_CLIENT_ID = 'compose-validation-only'
    $env:HALO_CLIENT_SECRET = 'compose-validation-only'
    $env:NETSUITE_ACCOUNT_ID = 'compose-validation-only'
    $env:NETSUITE_CONSUMER_KEY = 'compose-validation-only'
    $env:NETSUITE_CONSUMER_SECRET = 'compose-validation-only'
    $env:NETSUITE_TOKEN_ID = 'compose-validation-only'
    $env:NETSUITE_TOKEN_SECRET = 'compose-validation-only'
    docker compose --file '{{ project_root }}/docker-compose.yaml' config --quiet
