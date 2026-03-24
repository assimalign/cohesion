# Assimalign.Cohesion.FileSystem.Isolated Design

## Design Intent

The implementation aims to adapt isolated storage to the shared file-system contract, which would let callers swap persistence models without rewriting consumers. The package is still partial today.

## Implementation Note

Examples below document the intended public shape; some members still throw NotImplementedException today.

## Architecture

- IsolatedFileSystem is the single public backend type for the package.
- The design should mirror the same operations exposed by the core IFileSystem contract.
- Several members are unfinished, so the library currently describes a target direction more than a finished adapter.

## Layout Example

```text
Assimalign.Cohesion.FileSystem.Isolated/
  src/
    Assimalign.Cohesion.FileSystem.Isolated.csproj
    Internal/
  tests/
  docs/
    OVERVIEW.md
    DESIGN.md
```

## Example 1: Current public shape

```csharp
IFileSystem fileSystem = new IsolatedFileSystem();
```

## Example 2: Intended responsibility

```text
IsolatedFileSystem
  -> should adapt isolated storage to the shared IFileSystem contract
  -> while preserving the same caller-facing operations as other backends
```
