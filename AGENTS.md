# LISSTech.EntitySync

PowerShell 7 binary module for safe vendor entity synchronization.

## Build

- `just build` compiles `src/LISSTech.EntitySync.csproj` to `Module/`.
- `just test-load` imports `Module/LISSTech.EntitySync.psd1` in PowerShell 7 and lists exported commands.
- `just test` runs Pester tests in `Tests/`.

## Scope

The module owns canonical entity models, explainable fuzzy matching, sync plans, and safe application. Vendor API behavior belongs in adapters under `src/Adapters`.
