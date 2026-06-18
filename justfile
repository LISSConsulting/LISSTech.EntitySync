set shell := ["pwsh", "-NoProfile", "-Command"]
set dotenv-load

project_root    := justfile_directory()
source_project  := project_root / "src" / "LISSTech.EntitySync.csproj"
module_dir      := project_root / "Module"
module_manifest := module_dir / "LISSTech.EntitySync.psd1"
release_dir     := project_root / "Release"
configuration   := "Release"
module_name     := "LISSTech.EntitySync"

[private]
default:
    @just --list

# Build

# Prepare build directories
[group('build')]
[script('pwsh', '-NoProfile')]
[extension('.ps1')]
init:
    $ErrorActionPreference = 'Stop'
    function Use-Glyphs { -not [Console]::IsOutputRedirected -and ($env:WT_SESSION -or $env:TERM_PROGRAM -or $env:TERM -match 'xterm|screen|tmux|cygwin|ansi' -or $Host.Name -match 'Visual Studio Code') }
    function Icon($Glyph, $Text) { if (Use-Glyphs) { $Glyph } else { $Text } }
    function Say($Glyph, $Text, $Message, $Color = 'Cyan') { Write-Host ('[{0}] {1} {2}' -f (Get-Date -Format 'HH:mm:ss'), (Icon $Glyph $Text), $Message) -ForegroundColor $Color }

    Say '🧰' '[init]' 'Preparing build directories'
    foreach ($dir in @('{{ module_dir }}', '{{ release_dir }}')) {
        if (!(Test-Path $dir)) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
            Say '✨' '[new]' $dir 'DarkGray'
        } else {
            Say '✓' '[ok]' $dir 'DarkGray'
        }
    }

# Compile binary module into Module/
[group('build')]
[script('pwsh', '-NoProfile')]
[extension('.ps1')]
build: init
    $ErrorActionPreference = 'Stop'
    function Use-Glyphs { -not [Console]::IsOutputRedirected -and ($env:WT_SESSION -or $env:TERM_PROGRAM -or $env:TERM -match 'xterm|screen|tmux|cygwin|ansi' -or $Host.Name -match 'Visual Studio Code') }
    function Icon($Glyph, $Text) { if (Use-Glyphs) { $Glyph } else { $Text } }
    function Say($Glyph, $Text, $Message, $Color = 'Cyan') { Write-Host ('[{0}] {1} {2}' -f (Get-Date -Format 'HH:mm:ss'), (Icon $Glyph $Text), $Message) -ForegroundColor $Color }

    Say '🔨' '[build]' 'Compiling {{ module_name }} ({{ configuration }})'
    dotnet build '{{ source_project }}' --configuration {{ configuration }} --verbosity minimal
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $dll = Join-Path '{{ module_dir }}' '{{ module_name }}.dll'
    if (Test-Path $dll) {
        $size = (Get-Item $dll).Length / 1KB
        Say '✅' '[ok]' ('Built {0} ({1:N0} KB)' -f $dll, $size) 'Green'
    }

# Remove generated output
[group('build')]
[script('pwsh', '-NoProfile')]
[extension('.ps1')]
clean:
    $ErrorActionPreference = 'Stop'
    function Use-Glyphs { -not [Console]::IsOutputRedirected -and ($env:WT_SESSION -or $env:TERM_PROGRAM -or $env:TERM -match 'xterm|screen|tmux|cygwin|ansi' -or $Host.Name -match 'Visual Studio Code') }
    function Icon($Glyph, $Text) { if (Use-Glyphs) { $Glyph } else { $Text } }
    function Say($Glyph, $Text, $Message, $Color = 'Cyan') { Write-Host ('[{0}] {1} {2}' -f (Get-Date -Format 'HH:mm:ss'), (Icon $Glyph $Text), $Message) -ForegroundColor $Color }

    Say '🧹' '[clean]' 'Removing generated output'
    Remove-Module {{ module_name }} -Force -ErrorAction SilentlyContinue
    $paths = @(
        '{{ module_dir }}\{{ module_name }}.dll',
        '{{ module_dir }}\{{ module_name }}.pdb',
        '{{ module_dir }}\{{ module_name }}.deps.json',
        '{{ module_dir }}\en-US',
        '{{ project_root }}\src\obj',
        '{{ release_dir }}'
    )
    foreach ($path in $paths) {
        if (Test-Path $path) {
            Remove-Item $path -Recurse -Force -ErrorAction Stop
            Say '🗑️' '[rm]' $path 'DarkGray'
        }
    }
    Say '✅' '[ok]' 'Workspace scrubbed' 'Green'

# Import module and list exported commands
[group('test')]
[script('pwsh', '-NoProfile')]
[extension('.ps1')]
test-load: build
    $ErrorActionPreference = 'Stop'
    function Use-Glyphs { -not [Console]::IsOutputRedirected -and ($env:WT_SESSION -or $env:TERM_PROGRAM -or $env:TERM -match 'xterm|screen|tmux|cygwin|ansi' -or $Host.Name -match 'Visual Studio Code') }
    function Icon($Glyph, $Text) { if (Use-Glyphs) { $Glyph } else { $Text } }
    function Say($Glyph, $Text, $Message, $Color = 'Cyan') { Write-Host ('[{0}] {1} {2}' -f (Get-Date -Format 'HH:mm:ss'), (Icon $Glyph $Text), $Message) -ForegroundColor $Color }

    Say '🚪' '[load]' 'Importing {{ module_name }} in a clean PowerShell process'
    pwsh -NoProfile -NonInteractive -Command "Import-Module '{{ module_manifest }}' -Force; Get-Command -Module {{ module_name }} | Select-Object -ExpandProperty Name"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Say '✅' '[ok]' 'Module import succeeded' 'Green'

# Run Pester test suite
[group('test')]
[script('pwsh', '-NoProfile')]
[extension('.ps1')]
test: build
    $ErrorActionPreference = 'Stop'
    function Use-Glyphs { -not [Console]::IsOutputRedirected -and ($env:WT_SESSION -or $env:TERM_PROGRAM -or $env:TERM -match 'xterm|screen|tmux|cygwin|ansi' -or $Host.Name -match 'Visual Studio Code') }
    function Icon($Glyph, $Text) { if (Use-Glyphs) { $Glyph } else { $Text } }
    function Say($Glyph, $Text, $Message, $Color = 'Cyan') { Write-Host ('[{0}] {1} {2}' -f (Get-Date -Format 'HH:mm:ss'), (Icon $Glyph $Text), $Message) -ForegroundColor $Color }

    Say '🧪' '[test]' 'Running Pester suite'
    if (-not (Get-Module -ListAvailable Pester)) {
        Say '❌' '[fail]' 'Pester is not installed. Install-Module Pester -Scope CurrentUser' 'Red'
        exit 1
    }
    $result = Invoke-Pester -Path '{{ project_root }}\Tests' -Output Detailed -PassThru
    if ($result.FailedCount -gt 0) {
        Say '❌' '[fail]' "$($result.FailedCount) test(s) failed" 'Red'
        exit 1
    }
    Say '✅' '[ok]' "$($result.PassedCount) test(s) passed" 'Green'

# Generate external help from docs/
[group('docs')]
[script('pwsh', '-NoProfile')]
[extension('.ps1')]
external-help:
    $ErrorActionPreference = 'Stop'
    function Use-Glyphs { -not [Console]::IsOutputRedirected -and ($env:WT_SESSION -or $env:TERM_PROGRAM -or $env:TERM -match 'xterm|screen|tmux|cygwin|ansi' -or $Host.Name -match 'Visual Studio Code') }
    function Icon($Glyph, $Text) { if (Use-Glyphs) { $Glyph } else { $Text } }
    function Say($Glyph, $Text, $Message, $Color = 'Cyan') { Write-Host ('[{0}] {1} {2}' -f (Get-Date -Format 'HH:mm:ss'), (Icon $Glyph $Text), $Message) -ForegroundColor $Color }

    Say '📚' '[docs]' 'Generating external help'
    if (!(Get-Module -ListAvailable platyPS)) {
        Say '❌' '[fail]' 'platyPS is not installed. Install-Module platyPS -Scope CurrentUser' 'Red'
        exit 1
    }
    New-ExternalHelp -Path '{{ project_root }}\docs' -OutputPath '{{ project_root }}\en-US' -Force
    Say '✅' '[ok]' 'External help generated' 'Green'
