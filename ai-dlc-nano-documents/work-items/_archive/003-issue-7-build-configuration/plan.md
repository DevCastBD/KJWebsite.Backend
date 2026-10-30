<!-- phase: DONE | branch: 7-feature-b0-3-add-editorconfig-directorybuildprops-and-directorypackagesprops | tasks: 5/5
     base: fbe8885 | updated: 2026-09-20
     next: complete -->
# Plan: B0-3 shared build configuration
## Tasks
- [x] Add a root `.editorconfig` with consistent C# formatting rules.
- [x] Add root build properties for the active solution: net10.0, nullable, warnings-as-errors, and .NET analyzers; protect legacy snapshots.
- [x] Centralize active-solution package versions, update compatible dependencies to eliminate NU1903 warnings, and fix surfaced analyzer diagnostics.
- [x] Remove duplicated target, nullable, and package-version settings from the six solution project files.
- [x] Update the codebase map/stack notes and verify repository configuration.
## Tests
- Standard rigor: `dotnet restore`, `dotnet build KJWebsite.Backend.slnx`, `dotnet test KJWebsite.Backend.slnx`, and `dotnet format --verify-no-changes`; inspect resolved packages and restore/build diagnostics for warnings or suppressions.
## Files
- `.editorconfig`, `Directory.Build.props`, `Directory.Packages.props`, six active `.csproj` files, `src/Services/ContentService/Program.cs`
- `ai-dlc-nano-documents/tech-stack.md`, `ai-dlc-nano-documents/codebase-map.md`
