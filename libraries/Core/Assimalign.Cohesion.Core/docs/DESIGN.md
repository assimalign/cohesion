# Assimalign.Cohesion.Core Design

## Design Intent

This package is deliberately broad but lightweight. It collects reusable system-level building blocks so the higher-level packages can share a common vocabulary without depending on each other.

## Implementation Note

Examples below document the intended public shape; some members still throw NotImplementedException today.

## Architecture

- Core value types such as Size and Glob capture reusable concepts that show up across multiple libraries.
- Exception and system extension helpers centralize small cross-cutting behaviors.
- The library is dependency-light by design so other Core packages can reference it safely.

## Layout Example

```text
Assimalign.Cohesion.Core/
  src/
    Assimalign.Cohesion.Core.csproj
    Exceptions/
    Internal/
    Properties/
    Shared/
    System/
    Utilities/
  tests/
  docs/
    OVERVIEW.md
    DESIGN.md
```

## Example 1: Work with reusable size primitives

```csharp
Size payload = Size.FromKilobytes(512);

Console.WriteLine(payload.ToString("ki"));
Console.WriteLine(payload.Megabytes);
```

## Example 2: Use shared environment and glob helpers

```csharp
Glob pattern = Glob.Parse("**/*.json");
bool isMatch = pattern.IsMatch("settings/appsettings.json");

string? environmentName = AppEnvironment.GetEnvironmentName();
```
