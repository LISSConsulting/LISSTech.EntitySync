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
nswag_config      := project_root / "nswag.json"
generated_client  := project_root / "src" / "Adapters" / "LTAC" / "Generated" / "AgentControllerClient.g.cs"
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
        try {
            $env:LISSTECH_ENTITYSYNC_TEST_MODULE_PATH = '{{ build_manifest }}'
            $result = Invoke-Pester -Path '{{ project_root }}\Tests' -Output Detailed -PassThru
            if ($result.FailedCount -gt 0) { throw "$($result.FailedCount) test(s) failed." }
            Write-JustStep -Icon '🧾' -Fallback '[tests]' -Text "$($result.PassedCount) test(s) passed" -ForegroundColor Green
        } finally {
            $env:LISSTECH_ENTITYSYNC_TEST_MODULE_PATH = $old
        }
    }

# Full local quality gate
[group('quality')]
check: check-agentcontroller-client lint analyze test-load test

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
        foreach ($path in @('{{ build_dir }}', '{{ release_dir }}', '{{ project_root }}\src\obj')) {
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Stop }
        }
    }
