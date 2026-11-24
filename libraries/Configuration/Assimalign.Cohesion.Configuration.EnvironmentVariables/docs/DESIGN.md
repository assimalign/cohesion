# Assimalign.Cohesion.Configuration.EnvironmentVariables Design

## Design Intent

This package keeps environment loading as a small plug-in over the core configuration system, with optional prefix filtering to bound the imported key space.

## Architecture

- The options object only captures the filtering values needed for provider creation.
- The provider is responsible for enumerating environment variables and mapping them into configuration keys.
- The builder extension keeps the caller API consistent with the rest of the configuration provider packages.
- A program that runs in an `AppEnvironmentScope` (Core) loads variables through
  `AppEnvironment.GetValues()`. The scope supplies the runtime-contract (`COHESION_`) variables,
  so a scoped `COHESION_CONFIG__` setting reaches only that program (2026-10-29). Outside a
  scope the provider reads `Environment.GetEnvironmentVariables()`.

## Layout Example

```text
Assimalign.Cohesion.Configuration.EnvironmentVariables/
  src/
    Assimalign.Cohesion.Configuration.EnvironmentVariables.csproj
    Extensions/
  tests/
  docs/
    OVERVIEW.md
    DESIGN.md
```

## Example 1: Read all visible environment variables

```csharp
var configuration = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .Build();
```

## Example 2: Filter environment variables by prefix

```csharp
var configuration = new ConfigurationBuilder()
    .AddEnvironmentVariables(source =>
    {
        source.Prefix = "COHESION_";
    })
    .Build();
```
