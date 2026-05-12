# Assimalign.Cohesion.Configuration.Json Design

## Design Intent

This package is the JSON-specialized member of the configuration family. It contains builder extensions, stream handling, and both legacy and newer provider code paths, which makes it a transitional package today.

## Implementation Note

Examples below document the intended public shape; some members still throw NotImplementedException today.

## Architecture

- Builder extensions provide AddJsonFile and AddJsonStream so callers stay close to the core configuration experience.
- Stream-based parsing is the natural center of the implementation, regardless of the backing file source.
- The coexistence of older and newer provider code shows that the package is evolving and still being consolidated.

## Layout Example

```text
Assimalign.Cohesion.Configuration.Json/
  src/
    Assimalign.Cohesion.Configuration.Json.csproj
    Extensions/
  tests/
  docs/
    OVERVIEW.md
    DESIGN.md
```

## Example 1: Register a file-based provider

```csharp
var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")
    .Build();
```

## Example 2: Register a stream-based provider

```csharp
using var stream = File.OpenRead("appsettings.json");

var configuration = new ConfigurationBuilder()
    .AddJsonStream(stream)
    .Build();
```
