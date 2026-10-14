# Core tests

Build the shared MSBuild tasks before running this suite in a fresh worktree:

```powershell
dotnet build build/Tasks -c Debug
dotnet test libraries/Core/Assimalign.Cohesion.Core/tests/Assimalign.Cohesion.Core.Tests.csproj
```

`GeneratedValueTypeTests` exercises the `CohesionValueType` items in this test project across
every supported runtime type, validation, class and struct kinds, parsing, formatting, and JSON.
`GeneratedValueTypeRegressionTests` keeps the runtime regressions on the earlier generated
surface so the same tests can run against the baseline generator.

`GeneratedValueTypeJsonContext` is an ordinary source-generated context outside the value-object
partials. The generated internal converter factory connects that same-assembly context to each
private converter without reflection-based serialization. The factory stays internal; a context
in another assembly still needs its own accessible converter.

The conformance classes share a non-parallel collection because they temporarily change the
current culture and count validation calls. Every culture change is restored in `finally`.
