# Assimalign.Cohesion.Configuration.Ini Design

## Design Intent

The package keeps format concerns narrow: extension methods register sources, and stream providers focus on translating INI sections and keys into the shared configuration shape.

## Architecture

- Builder extensions provide the familiar AddIniFile and AddIniStream entry points.
- The stream provider owns the parsing behavior for section and key handling.
- The package composes with the base configuration model rather than inventing a new data shape.

## Layout Example

```text
Assimalign.Cohesion.Configuration.Ini/
  src/
    Assimalign.Cohesion.Configuration.Ini.csproj
  tests/
  docs/
    OVERVIEW.md
    DESIGN.md
```

## Example 1: Register a file-based provider

```csharp
var configuration = new ConfigurationBuilder()
    .AddIniFile("appsettings.ini")
    .Build();
```

## Example 2: Register a stream-based provider

```csharp
using var stream = File.OpenRead("appsettings.ini");

var configuration = new ConfigurationBuilder()
    .AddIniStream(stream)
    .Build();
```
