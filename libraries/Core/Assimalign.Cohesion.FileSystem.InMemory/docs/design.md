# Assimalign.Cohesion.FileSystem.InMemory Design

## Design Intent

This package is the test-friendly and ephemeral-storage implementation of the file-system stack. It keeps the same public contract as physical storage while moving all state into process memory.

## Architecture

- InMemoryFileSystem is the public backend type and is configured through InMemoryFileSystemOptions.
- Internal node and stream types keep storage mechanics out of the public surface.
- Factory builder extensions make the backend easy to register alongside other file systems.

## Layout Example

```text
Assimalign.Cohesion.FileSystem.InMemory/
  src/
    Assimalign.Cohesion.FileSystem.InMemory.csproj
    Extensions/
    Internal/
  tests/
  docs/
    OVERVIEW.md
    DESIGN.md
```

## Example 1: Register the in-memory backend through the factory builder

```csharp
var factory = new FileSystemFactoryBuilder()
    .AddInMemoryFileSystem()
    .Build();

IFileSystem fileSystem = factory.Create<InMemoryFileSystem>();
```

## Example 2: Configure the in-memory backend directly

```csharp
var options = new InMemoryFileSystemOptions();
var fileSystem = new InMemoryFileSystem(options);
```
