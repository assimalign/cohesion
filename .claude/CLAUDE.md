# Cohesion

Code-first, multi-service application framework in C# — comparable to .NET Aspire, but designed for both in-process and out-of-process hosting. Everything targets `net10.0` (`LangVersion=Preview`, `EnablePreviewFeatures=true`); NativeAOT compatibility is a standing requirement. .NET SDK `10.0.300`+ is pinned in `global.json`. Layering model: L1 = foundation libraries + SDK/tooling, L2 = application runtime and composition, L3 = service platforms (see `docs/programs/DELIVERY_ROADMAP.md`).

## Coding standards

The rules in `.claude/rules/` are the canonical coding standard for this repo. They load automatically as matching files are touched — do not re-derive conventions from the code. Before modifying a library, read its `docs/DESIGN.md` and the area `README.md`; area context often determines which rule variant applies. If asked to do something that contradicts a rule, follow the exception protocol in `.claude/rules/deviations.md`. (The rules lived in a root `AGENTS.md` until 2026-07; historical `Deviates from AGENTS.md` markers in code refer to these same rules.)

## Repository structure

- `libraries/` — shared libraries, infrastructure, runtime, and cross-service foundations
- `resources/` — service and resource implementations. Every folder under `resources/` has a corresponding `Sdk.<Name>` and `App.<Name>` framework family
- `frameworks/` — shared-framework producer projects (one Refs + one Runtime project per family) plus the authoritative manifests `Assimalign.Cohesion.App.props` / `.targets`
- `build/` — custom MSBuild logic, centralized targets, package-version management. `build/Targets/Build.Version.props` is the single source of truth for `$(CohesionVersion)`
- `sdks/` — Cohesion SDK projects; `Sdk` is the base and `Sdk.<Domain>` chain to it
- `analyzers/` — Roslyn analyzers/codefixes/generators; target `netstandard2.0` with `IsAotCompatible=false` — the one sanctioned exception to the repo-wide TFM/AOT defaults
- `assets/` — shared repo assets: the `cohesion.config` JSON schemas and `branding/` (NuGet package icon, imported from the branding repo)
- `installer/` — WiX MSI source plus dev and release scripts (`Install-Local.ps1`, `Get-CohesionVersion.ps1`, `New-CohesionDomainScaffold.ps1`, `Pack-Release.ps1`, `Get-ReleaseMatrix.ps1`, and `modules/CohesionPackaging.psm1` — the authoritative release inventory)
- `extensions/` and `tooling/` — developer tooling and integration surfaces
- `samples/` — the package-only SDK-consumer smoke tree, moving to the `cohesion-examples` companion repo. Executable acceptance fixtures are **not** here: each lives in a `fixtures/` folder inside the project whose tests drive it. AOT guard projects stay in their library's own `samples/`. See `.claude/rules/workflow.md` and `resource-areas.md`
- `docs/` — all repository-, program-, and **area**-level documentation. `docs/programs/` holds dated plans and roadmaps; `docs/resources/<Area>/` and `docs/libraries/<Area>/` mirror the repo root and hold each area's architecture record; `docs/DEPENDENCIES.md` is the generated reference graph. Area `README.md` files stay in the area; per-project `docs/` stay beside `src/`. Layout and rationale: `.claude/rules/documentation.md`

## Build & test

```powershell
dotnet build [path-to-csproj]                          # build repo or a single project
dotnet test <project>/tests/                           # run a project's tests
pwsh installer/scripts/Install-Local.ps1               # dev loop: pack all SDKs + frameworks into _out/packages/
pwsh installer/scripts/Pack-Release.ps1 -Version <v>   # strict release pack into _out/release/packages/
```

Outputs land in `_out/packages/`, `_out/release/packages/`, and `_out/dotnet/sdk/`. In a **fresh worktree**, build `build/Tasks` first — per-project builds fail with MSB4062 until the build tasks exist.

Publishing happens **only** from `.github/workflows/release.yml`, triggered by a published GitHub Release tagged `v$(CohesionVersion)`. What ships is defined once, in `installer/scripts/modules/CohesionPackaging.psm1`; a package ships only if a per-area CI workflow already builds and tests it, enforced both ways by `Assert-CohesionReleaseInventory`. See `.claude/rules/build-system.md`.
