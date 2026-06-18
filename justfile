set shell := ["pwsh", "-NoProfile", "-Command"]

project_root    := justfile_directory()
source_project  := project_root / "src" / "LISSTech.EntitySync.csproj"
module_dir      := project_root / "Module"
module_manifest := module_dir / "LISSTech.EntitySync.psd1"
release_dir     := project_root / "Release"
configuration   := "Release"

[private]
default:
    @just --list

[group('build')]
init:
    if (!(Test-Path '{{ module_dir }}')) { New-Item -ItemType Directory -Path '{{ module_dir }}' -Force | Out-Null }
    if (!(Test-Path '{{ release_dir }}')) { New-Item -ItemType Directory -Path '{{ release_dir }}' -Force | Out-Null }

[group('build')]
build: init
    dotnet build '{{ source_project }}' --configuration {{ configuration }} --verbosity minimal

[group('build')]
clean:
    Remove-Module LISSTech.EntitySync -Force -ErrorAction SilentlyContinue
    if (Test-Path '{{ module_dir }}\LISSTech.EntitySync.dll') { Remove-Item '{{ module_dir }}\*.dll','{{ module_dir }}\*.pdb' -Force -ErrorAction SilentlyContinue }
    if (Test-Path '{{ project_root }}\src\obj') { Remove-Item '{{ project_root }}\src\obj' -Recurse -Force }

[group('test')]
test-load: build
    pwsh -NoProfile -NonInteractive -Command "Import-Module '{{ module_manifest }}' -Force; Get-Command -Module LISSTech.EntitySync | Select-Object -ExpandProperty Name"

[group('test')]
test: build
    pwsh -NoProfile -NonInteractive -Command "Invoke-Pester -Path '{{ project_root }}\Tests'"

[group('docs')]
external-help:
    pwsh -NoProfile -Command "if (!(Get-Module -ListAvailable platyPS)) { throw 'Install platyPS first.' }; New-ExternalHelp -Path '{{ project_root }}\docs' -OutputPath '{{ project_root }}\en-US' -Force"
