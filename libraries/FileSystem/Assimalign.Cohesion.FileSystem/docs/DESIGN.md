# Assimalign.Cohesion.FileSystem Design

## Design Intent

This is the abstraction layer that keeps the rest of the ecosystem storage-agnostic. Callers depend on IFileSystem and related interfaces, while concrete backends live in sibling packages.

## Architecture

- IFileSystem, IFileSystemFile, IFileSystemDirectory, and related interfaces define the common contract.
- FileSystemFactoryBuilder handles named and typed backend registration.
- Events, glob-aware watching, and helper extensions are part of the base abstraction rather than optional add-ons.

## Layout Example

```text
Assimalign.Cohesion.FileSystem/
  src/
    Assimalign.Cohesion.FileSystem.csproj
    Abstractions/
    Exceptions/
    Extensions/
    Internal/
    Properties/
  tests/
  docs/
    OVERVIEW.md
    DESIGN.md
```

## Example 1: Register a named file system

```csharp
var builder = new FileSystemFactoryBuilder();

builder.AddFileSystem("primary", myFileSystem);

IFileSystemFactory factory = builder.Build();
IFileSystem fileSystem = factory.Create("primary");
```

## Example 2: Register and resolve by type

```csharp
var builder = new FileSystemFactoryBuilder();

builder.AddFileSystem(() => new MyFileSystem());

IFileSystem fileSystem = builder.Build().Create<MyFileSystem>();
```
