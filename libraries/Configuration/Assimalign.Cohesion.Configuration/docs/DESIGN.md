# Assimalign.Cohesion.Configuration Design

## Design Intent

This package is the hub for the configuration stack. Providers are composed through builders, loaded through a common context, and surfaced as a hierarchical key and value model.

## Implementation Note

Examples below document the intended public shape; some members still throw NotImplementedException today.

## Architecture

- ConfigurationBuilder gathers provider factories and builds a configuration snapshot.
- ConfigurationManager extends that flow for longer-lived provider orchestration.
- Keys, paths, sections, values, and provider abstractions keep the rest of the configuration family consistent.

## Key Identity

`Key` compares its character span, so `default(Key)`, an empty string, a null string passed to
the string constructor, and an empty span all represent the same empty key. Ordinal hashing
uses that same span, including its empty representation. `KeyComparer` delegates key hashing
to the selected comparison and hashes alternate spans with the matching string comparison,
so dictionary lookups agree for constructed keys, default keys, and alternate spans.

This is a behavioral change from `10.0.0-preview.1`: the ordinal hash of `default(Key)` is now
the empty-string hash instead of zero. Dictionaries and sets consequently treat default and
constructed-empty keys as one key, as equality already requires. Constructors, validation,
and the public surface are unchanged.

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
