# Assimalign.Cohesion.FileSystem.Aggregate Design

## Design Intent

The design goal is compelling: unify multiple backends without changing the caller contract. The current implementation is still mostly a stub, so the docs describe the intended composite role more than the runtime behavior.

## Implementation Note

Examples below document the intended public shape; some members still throw NotImplementedException today.

## Architecture

- AggregateFileSystem is intended to remain an IFileSystem so callers do not need a second API surface.
- A completed implementation would need routing rules for lookups, enumeration, and change notifications.
- Today the class mostly marks the intended seam for future composition work.

## Layout Example

```text
Assimalign.Cohesion.FileSystem.Aggregate/
  src/
    Assimalign.Cohesion.FileSystem.Aggregate.csproj
  tests/
  docs/
    OVERVIEW.md
    DESIGN.md
```

## Example 1: Current public shape

```csharp
IFileSystem fileSystem = new AggregateFileSystem();
```

## Example 2: Intended responsibility

```text
AggregateFileSystem
  -> should compose multiple file-system backends behind one IFileSystem facade
  -> while preserving the IFileSystem contract for callers
```
