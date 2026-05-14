# Assimalign.Cohesion.FileSystem.IsolatedStorage Design

## Design Intent

The implementation aims to adapt isolated storage to the shared file-system contract, which would let callers swap persistence models without rewriting consumers. The package is still partial today.

## Implementation Note

Examples below document the intended public shape; some members still throw NotImplementedException today.

## Architecture

- IsolatedStorageFileSystem is the single public backend type for the package.
- The design should mirror the same operations exposed by the core IFileSystem contract.
- Several members are unfinished, so the library currently describes a target direction more than a finished adapter.

## Layout Example

```text
Assimalign.Cohesion.FileSystem.IsolatedStorage/
  src/
    Assimalign.Cohesion.FileSystem.IsolatedStorage.csproj
    Internal/
  tests/
  docs/
    OVERVIEW.md
    DESIGN.md
```

## Example 1: Current public shape

```csharp
IFileSystem fileSystem = new IsolatedStorageFileSystem();
```

## Example 2: Intended responsibility

```text
IsolatedStorageFileSystem
  -> should adapt isolated storage to the shared IFileSystem contract
  -> while preserving the same caller-facing operations as other backends
```
