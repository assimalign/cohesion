# Cohesion Build Tasks

This project provides the tasks behind Cohesion's shared MSBuild targets, including
`CohesionValueType` generation. It depends on `Microsoft.Build.Utilities.Core` and loads under
both command-line and Visual Studio MSBuild through its `netstandard2.0` target.

Build it first in a fresh worktree:

```powershell
dotnet build build/Tasks -c Debug
```

Declare value objects with `CohesionValueType` items in the consumer project. Set
`ObjectHasIsValidMethod="true"` to supply the validation rule in a handwritten partial. For a
secret value, additionally set `ObjectHasCustomToString="true"` and provide a redacted
`ToString()` and `DebuggerDisplay` in that partial. Explicit value access and JSON serialization
still use the underlying value.

See the [metadata reference](../../../docs/build/MSBUILD_COHESION_PROPS.md#value-types) and
[design](DESIGN.md). Conformance tests live in
`libraries/Core/Assimalign.Cohesion.Core/tests/Assimalign.Cohesion.Core.Tests.csproj`.
