# Assimalign.Cohesion.Configuration.FileSystem Design

## Design Intent

The goal of this package is to decouple configuration loading from any single storage implementation. A configuration provider can target IFileSystem and work with physical, in-memory, or isolated backends.

## Architecture

- FileSystemConfigurationOptions captures the file-system, target path, and reload behavior.
- FileSystemConfigurationProvider centralizes watch and reload behavior over IFileSystemFile.
- Format-specific providers can inherit from this layer instead of rewriting file access concerns.

## Layout Example

```text
Assimalign.Cohesion.Configuration.FileSystem/
  src/
    Assimalign.Cohesion.Configuration.FileSystem.csproj
    Extensions/
    Internal/
  tests/
  docs/
    OVERVIEW.md
    DESIGN.md
```

## Example 1: Model the common file-backed provider options

```csharp
var options = new FileSystemConfigurationOptions
{
    FileSystem = fileSystem,
    Path = default,
    Optional = true,
    ReloadOnChange = true
};

FileSystemConfigurationProvider provider = new MyFileSystemConfigurationProvider(options);
```

## Example 2: Intended reload flow

```text
IFileSystem
  -> IFileSystemFile
     -> Watch()
        -> FileSystemConfigurationProvider.ReloadAsync()
           -> configuration reload token
```
