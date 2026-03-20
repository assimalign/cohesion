# Assimalign.Cohesion.Configuration Design

## Design Intent

This package is the hub for the configuration stack. Providers are composed through builders, loaded through a common context, and surfaced as a hierarchical key and value model.

## Implementation Note

Examples below document the intended public shape; some members still throw NotImplementedException today.

## Architecture

- ConfigurationBuilder gathers provider factories and builds a configuration snapshot.
- ConfigurationManager extends that flow for longer-lived provider orchestration.
- Keys, paths, sections, values, and provider abstractions keep the rest of the configuration family consistent.

## Layout Example

```text
Assimalign.Cohesion.Configuration/
  src/
    Assimalign.Cohesion.Configuration.csproj
    Abstractions/
    Decorators/
    Exceptions/
    Extensions/
    Internal/
    Properties/
    ValueObjects/
  tests/
  docs/
    OVERVIEW.md
    DESIGN.md
```

## Example 1: Build from custom providers

```csharp
var builder = ConfigurationBuilder.Create(options =>
{
    options.LoadTimeout = TimeSpan.FromSeconds(5);
});

builder.AddProvider(context => new MyConfigurationProvider());

Configuration configuration = builder.Build();
```

## Example 2: Async build path

```csharp
var builder = new ConfigurationBuilder();

builder.AddProvider(context => Task.FromResult<IConfigurationProvider>(new MyConfigurationProvider()));

Configuration configuration = await builder.BuildAsync(cancellationToken);
```
