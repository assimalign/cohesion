# Cohesion

Code-first, multi-service application framework in C# — comparable to .NET Aspire, but designed for both in-process and out-of-process hosting. Everything targets `net10.0` (`LangVersion=Preview`, `EnablePreviewFeatures=true`); NativeAOT compatibility is a standing requirement. .NET SDK `10.0.300`+ is pinned in `global.json`. Layering model: L1 = foundation libraries + SDK/tooling, L2 = application runtime and composition, L3 = service platforms (see `docs/programs/DELIVERY_ROADMAP.md`).

## Coding standards

The rules in `.claude/rules/` are the canonical coding standard for this repo. They load automatically as matching files are touched — do not re-derive conventions from the code. Before modifying a library, read its `docs/DESIGN.md` and the area `README.md`; area context often determines which rule variant applies. If asked to do something that contradicts a rule, follow the exception protocol in `.claude/rules/deviations.md`. (The rules lived in a root `AGENTS.md` until 2026-07; historical `Deviates from AGENTS.md` markers in code refer to these same rules.)

## Repository structure

- `libraries/` — shared libraries, infrastructure, runtime, and cross-service foundations. `libraries/App/` holds the root `Assimalign.Cohesion.App` framework producers (`Assimalign.Cohesion.App.Refs` / `.Runtime`) plus `Assimalign.Cohesion.App.props` (the App hosting kernel's roots and the defaults every framework producer shares) and `Assimalign.Cohesion.App.targets` (App's closure derivation and every framework's pack logic)
- `resources/` — service and resource implementations. Every folder under `resources/` has a corresponding `Sdk.<Name>` and `App.<Name>` framework family, and produces its framework itself: `Assimalign.Cohesion.<Name>.Refs` / `.Runtime` sit beside the area's libraries while their assembly and package names keep the `App` segment (naming convention: `.claude/rules/build-system.md`). Each area's framework members are listed by hand in its Runtime producer's `Directory.Build.props`, which the Refs producer's `Directory.Build.props` imports
- `build/` — custom MSBuild logic, centralized targets, package-version management. `build/Targets/Build.Version.props` is the single source of truth for `$(CohesionVersion)`
- `sdks/` — Cohesion SDK projects; `Sdk` is the base and `Sdk.<Domain>` chain to it
- `analyzers/` — Roslyn analyzers/codefixes/generators; target `netstandard2.0` with `IsAotCompatible=false` — the one sanctioned exception to the repo-wide TFM/AOT defaults; placement and naming follow [analyzers.md](rules/analyzers.md)
- `assets/` — shared repo assets: the `cohesion.config` JSON schemas and `branding/` (NuGet package icon, imported from the branding repo)
- `scripts/` — all repository PowerShell entry points. Area scripts mirror their owner (`resources/Database/`, `resources/Web/`, `build/`, `sdks/`); cross-repository commands stay at the scripts root. Reusable modules live in `modules/`, Pester tests in `tests/`. `modules/CohesionPackaging.psm1` is the authoritative release inventory. See `scripts/README.md`
- `extensions/` and `tooling/` — developer tooling and integration surfaces
- `fixtures/` — repository-wide IntegrationCheck, ResourceGenerationCheck, and the isolated SDK-consumer smoke tree. Project-owned acceptance hosts stay in their project's `fixtures/`; AOT guards stay in their library's `samples/`. See `fixtures/README.md`
- `docs/` — all repository-, program-, and **area**-level documentation. `docs/programs/` holds dated plans and roadmaps; `docs/resources/<Area>/` and `docs/libraries/<Area>/` mirror the repo root and hold each area's architecture record; `docs/DEPENDENCIES.md` is the generated reference graph. Area `README.md` files stay in the area; per-project `docs/` stay beside `src/`. Layout and rationale: `.claude/rules/documentation.md`
- Public API reference pages belong in the `cohesion-docs` companion repository. Keep XML documentation in source and project `OVERVIEW.md` / `DESIGN.md` here.

## Build & test

```powershell
dotnet build [path-to-csproj]                          # build repo or a single project
dotnet test <project>/tests/                           # run a project's tests
pwsh scripts/Install-Local.ps1               # dev loop: pack all SDKs + frameworks into _out/packages/
pwsh scripts/Pack-Release.ps1 -Version <v>   # strict release pack into _out/release/packages/
```

Outputs land in `_out/packages/`, `_out/release/packages/`, and `_out/dotnet/sdk/`. In a **fresh worktree**, build `build/Tasks` first — per-project builds fail with MSB4062 until the build tasks exist.

Release versions publish **only** from `.github/workflows/release.yml`, triggered by a published GitHub Release tagged `v$(CohesionVersion)`. The only other publisher, `.github/workflows/ci-packages.yml`, stages a uniquely versioned CI build of every `main` push and same-repository pull request commit in GitHub Packages, never nuget.org. What ships is defined once, in `scripts/modules/CohesionPackaging.psm1`; a package ships only if a per-area CI workflow already builds and tests it, enforced both ways by `Assert-CohesionReleaseInventory`. See `.claude/rules/build-system.md`.
