# Assimalign.Cohesion.FileSystem.Physical Design

## Design Intent

This package is the production-oriented backend for the file-system abstraction. It adapts OS files, directories, and metadata into the common Cohesion file-system model.

## Architecture

- PhysicalFileSystem is the public adapter over the operating system file system.
- PhysicalFileSystemOptions shape runtime behavior without polluting the shared file-system contracts.
- Factory registration extensions keep physical storage easy to compose with other backends.

## Layout Example

```text
Assimalign.Cohesion.FileSystem.Physical/
  src/
    Assimalign.Cohesion.FileSystem.Physical.csproj
    Extensions/
    Internal/
  tests/
  docs/
    OVERVIEW.md
    DESIGN.md
```

## Example 1: Register the physical backend through the factory builder

```csharp
var factory = new FileSystemFactoryBuilder()
    .AddPhysicalFileSystem()
    .Build();

IFileSystem fileSystem = factory.Create<PhysicalFileSystem>();
```

## Example 2: Construct the physical backend directly

```csharp
var fileSystem = new PhysicalFileSystem(PhysicalFileSystemOptions.Default);
```
