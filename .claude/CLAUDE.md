# Cohesion

Code-first, multi-service application framework in C# — comparable to .NET Aspire, but designed for both in-process and out-of-process hosting. Everything targets `net10.0` (`LangVersion=Preview`, `EnablePreviewFeatures=true`); NativeAOT compatibility is a standing requirement. .NET SDK `10.0.300`+ is pinned in `global.json`. Layering model: L1 = foundation libraries + SDK/tooling, L2 = application runtime and composition, L3 = service platforms (see `docs/DELIVERY_ROADMAP.md`).

## Coding standards

The rules in `.claude/rules/` are the canonical coding standard for this repo. They load automatically as matching files are touched — do not re-derive conventions from the code. Before modifying a library, read its `docs/DESIGN.md` and the area `README.md`; area context often determines which rule variant applies. If asked to do something that contradicts a rule, follow the exception protocol in `.claude/rules/deviations.md`. (The rules lived in a root `AGENTS.md` until 2026-07; historical `Deviates from AGENTS.md` markers in code refer to these same rules.)

## Repository structure

- `libraries/` — shared libraries, infrastructure, runtime, and cross-service foundations
- `resources/` — service and resource implementations. Every folder under `resources/` has a corresponding `Sdk.<Name>` and `App.<Name>` framework family
- `frameworks/` — shared-framework producer projects (one Refs + one Runtime project per family) plus the authoritative manifests `Assimalign.Cohesion.App.props` / `.targets`
- `build/` — custom MSBuild logic, centralized targets, package-version management. `build/Targets/Build.Version.props` is the single source of truth for `$(CohesionVersion)`
- `sdks/` — Cohesion SDK projects; `Sdk` is the base and `Sdk.<Domain>` chain to it
- `analyzers/` — Roslyn analyzers/codefixes/generators; target `netstandard2.0` with `IsAotCompatible=false` — the one sanctioned exception to the repo-wide TFM/AOT defaults
- `assets/` — shared repo assets such as the `cohesion.config` JSON schemas
- `installer/` — WiX MSI source plus dev scripts (`Install-Local.ps1`, `Get-CohesionVersion.ps1`, `New-CohesionDomainScaffold.ps1`; the publish helper lives at `.github/scripts/Publish-Nupkg.ps1`)
- `extensions/` and `tooling/` — developer tooling and integration surfaces
- `docs/` — repository-level documentation

## Build & test

```powershell
dotnet build [path-to-csproj]               # build repo or a single project
dotnet test <project>/tests/                # run a project's tests
pwsh installer/scripts/Install-Local.ps1    # dev loop: pack all SDKs + frameworks into _out/packages/
```

Outputs land in `_out/packages/` and `_out/dotnet/sdk/`. In a **fresh worktree**, build `build/Tasks` first — per-project builds fail with MSB4062 until the build tasks exist.
