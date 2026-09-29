# Cohesion development scripts

`Assimalign.Cohesion.DevScripts` is the repository maintenance PowerShell module.
Its generated manifest exports exactly one cmdlet:

| Cmdlet | Purpose |
| --- | --- |
| `New-CohesionDotnetSolution` | Writes an XML `.slnx` solution from a directory tree, optionally including resolved Cohesion project references. |

Use `-SolutionPath` and `-SolutionName` (or a complete `.slnx` path), with optional
`-SolutionRootFolder`, `-IncludeReferences`, `-ReferenceFolderName`, `-IgnorePaths`,
`-Grouping` and `-Force`. Existing solutions are preserved unless `-Force` is supplied.
The result is a `CohesionDotnetSolution` containing the generated `FileInfo`.

`New-CohesionDotnetLibrary` was an unexported, no-output placeholder and has been deleted.
Application scaffolding is supplied by the shipped [dotnet new templates](../templates/README.md)
and the [cohesion CLI](../README.md), not a `cohesion-dev` command table.
The unexported setup placeholder is not an available module command.
The Database area's hard-coded one-off `solution.ps1` is retired; this cmdlet remains the generator.

The legacy project sets `OutDir` to the operator's Documents/PowerShell module directory.
Do not build it as part of a CLI verification run. If compiling it separately is necessary,
override `OutDir` with an absolute path inside this repository's `_out/` directory so the
assembly and generated manifest stay in the workspace. Deleting an unexported placeholder
requires no module build or install.
