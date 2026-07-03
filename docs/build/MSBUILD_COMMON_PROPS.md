# Common MSBuild Properties

This document describes where common MSBuild properties are defined in the Cohesion build and which values individual projects inherit. The rule from `AGENTS.md` applies throughout: **shared build logic lives in `.props`/`.targets` files, never duplicated per project.** If a project genuinely needs to deviate, override the property locally with a comment explaining why — otherwise project files stay free of build configuration.

## Import chain

Every project in the repository picks up the shared configuration automatically:

```
Directory.Build.props (repo root)
└── build/Build.props
    ├── build/Targets/Build.Global.props           ← nullable, repo directories, _out/ output paths
    ├── build/Targets/Build.TargetFramework.props  ← TargetFrameworkLatest + per-area TFM properties
    ├── build/Targets/Build.Branding.props         ← package and assembly metadata
    ├── build/Targets/Build.Version.props          ← $(CohesionVersion) single source of truth
    ├── build/Targets/Build.Constants.props        ← OS define constants (IS_WINDOWS, IS_LINUX, IS_MAC)
    └── build/Targets/Build.CodeGeneration.props   ← code-generation defaults

Directory.Build.targets (repo root)
└── build/Build.targets
    ├── build/Targets/Build.CodeGeneration.targets
    ├── build/Targets/Build.References.Packages.targets   ← central package versions (CohesionPackageReference)
    ├── build/Targets/Build.References.Projects.targets   ← name-only project resolution (CohesionProjectReference)
    ├── build/Targets/Build.References.Analyzers.targets  ← analyzer references
    └── build/Targets/Build.SharedFiles.targets
```

`Assimalign.Cohesion.Build.Tasks` skips most of these imports (see the conditions in `build/Build.props`) because the build-tasks assembly must compile before everything that depends on it.

## Key properties

| Property | Defined in | Notes |
| --- | --- | --- |
| `TargetFrameworkLatest` | `Build.TargetFramework.props` | Currently `net10.0`. Drives `TargetFrameworkForLibraries`, `...ForResources`, `...ForFrameworks`, `...ForSdks`, and `...ForTooling`. Analyzers use `TargetFrameworkForAnalyzers` (`netstandard2.0`). |
| `LangVersion` / `EnablePreviewFeatures` | `Build.TargetFramework.props` | `Preview` + `true` repo-wide. Source generators and build tasks that must target the stable surface opt out locally. |
| `Nullable` | `Build.Global.props` | `enable` repo-wide. |
| `CohesionVersion` (+ `Prefix`/`Suffix`) | `Build.Version.props` | The single source of truth for every package version; the major version derives from the TFM. Never set `<Version>` per project. |
| `CohesionOutputPath*` | `Build.Global.props` | Output roots under `_out/` (`packages`, `dotnet/sdk`, `dotnet/shared`, `dotnet/packs`, ...). |
| `DefineConstants` | `Build.Constants.props` | Adds `IS_WINDOWS` / `IS_LINUX` / `IS_MAC` for the build OS. |

## Related docs

- [Cohesion Custom MSBuild Items](./MSBUILD_COHESION_PROPS.md)
- [Common MSBuild Targets](./MSBUILD_COMMON_TARGETS.md)
- [MSBuild Common Project Properties (Microsoft)](https://learn.microsoft.com/en-us/visualstudio/msbuild/common-msbuild-project-properties?view=vs-2022)
