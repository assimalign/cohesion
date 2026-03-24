# Assimalign.Cohesion.FileSystem.Globbing Design

## Design Intent

The package isolates wildcard and pattern behavior from the file-system core so matching rules can evolve without inflating the base IFileSystem contracts.

## Architecture

- GlobMatcherBuilder separates include and exclude pattern composition.
- IGlobMatcher operates over FileSystemPath, IFileSystemFile, and IFileSystemDirectory so it can be reused across backends.
- GlobMatchResults gives callers a focused result object instead of pushing match bookkeeping into each consumer.

## Layout Example

```text
Assimalign.Cohesion.FileSystem.Globbing/
  src/
    Assimalign.Cohesion.FileSystem.Globbing.csproj
    Abstractions/
    Internal/
    Properties/
  tests/
  docs/
    OVERVIEW.md
    DESIGN.md
```

## Example 1: Compose include and exclude patterns

```csharp
IGlobMatcher matcher = new GlobMatcherBuilder()
    .AddInclude(Glob.Parse("**/*.json"))
    .AddExclude(Glob.Parse("bin/**"))
    .Build();
```

## Example 2: Match against a directory abstraction

```csharp
GlobMatchResults results = matcher.Match(rootDirectory);
```
