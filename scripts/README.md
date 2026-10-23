# Repository scripts

All repository PowerShell entry points live here. Run them with `pwsh`; their default repository
and source paths resolve from the script location, so they also work from another directory.
Reusable modules live in `modules/`, and Pester tests live in `tests/`.

Area-specific scripts mirror their owner in the repository: `resources/Database/`,
`resources/Web/`, `build/`, and `sdks/`. Keep commands that operate across the repository at
the scripts root. A script for one area belongs under that area's path, even when it is called
from CI or an agent skill.

| Script | Purpose |
| --- | --- |
| `Install-Local.ps1` | Build and pack the local SDK, framework, and library feed. |
| `Pack-Release.ps1` | Strictly build and validate the release package set. |
| `Get-CohesionVersion.ps1` | Read the canonical version from the shared MSBuild props. |
| `Get-ReleaseMatrix.ps1` | Emit the release validation matrix from the release inventory. |
| `Update-CohesionSolutions.ps1` | Update solution project entries and dependency closures; replaces root `setup.ps1` and the compiled DevScripts module. Preserves existing folder groupings. Supports `-Check`, `-WhatIf`, and `-SolutionPath`. |
| `Update-CohesionDependencyGraph.ps1` | Regenerate `docs/DEPENDENCIES.md`; `-Check` detects drift. |
| `New-CohesionDomainScaffold.ps1` | Scaffold an SDK, framework producers, and ApplicationModel project. |
| `New-CohesionWorkItem.ps1` | Search, audit, or create work items using the cohesion-work-items skill's contract. |
| `build/Test-CohesionSharedSource.ps1` | Exercise shared-source module selection and rejection cases. |
| `resources/Database/Generate-CollationUnicodeData.ps1` | Regenerate collation tables from checksum-pinned Unicode inputs. |
| `resources/Database/Get-CohesionRoadmapData.ps1` | Read the Database work board and generate roadmap data and HTML. |
| `resources/Database/Prepare-Q8PhaseInstrumentation.ps1` | Prepare SQL phase instrumentation in an extracted archive beneath `_out/`. |
| `resources/Web/Measure-OutputCacheCounters.ps1` | Run the output-cache counter experiment; temporarily edits source and restores it in `finally`. |
| `sdks/Invoke-SdkConsumerSmoke.ps1` | Build, publish, and run isolated package-only SDK consumers. |

```powershell
pwsh scripts/Install-Local.ps1
pwsh scripts/Update-CohesionSolutions.ps1 -WhatIf
pwsh scripts/Update-CohesionDependencyGraph.ps1
Invoke-Pester ./scripts/tests -CI
```

`modules/CohesionPackaging.psm1` remains the authoritative release inventory. SDKs, frameworks,
libraries, CLI, and templates ship as NuGet packages. Repository checks live in `fixtures/`. Roadmap assets and work-item
instructions stay in `.claude/skills/`, with their executable entry points here.
