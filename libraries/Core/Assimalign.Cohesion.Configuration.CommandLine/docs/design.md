# Assimalign.Cohesion.Configuration.CommandLine Design

## Design Intent

This package keeps command-line parsing as a focused provider over the core configuration system, with an options object that carries raw arguments and switch mappings.

## Architecture

- ConfigurationBuilder extension methods are the user-facing entry point.
- ConfigurationCommandLineOptions captures raw arguments and optional switch mappings.
- ConfigurationCommandLineProvider turns normalized arguments into configuration keys and values.

## Layout Example

```text
Assimalign.Cohesion.Configuration.CommandLine/
  src/
    Assimalign.Cohesion.Configuration.CommandLine.csproj
    Extensions/
  tests/
  docs/
    OVERVIEW.md
    DESIGN.md
```

## Example 1: Add raw command-line arguments

```csharp
var configuration = new ConfigurationBuilder()
    .AddCommandLine(args)
    .Build();
```

## Example 2: Add command-line arguments with switch mappings

```csharp
var mappings = new Dictionary<string, string>
{
    ["-p"] = "Port",
    ["--env"] = "Environment"
};

var configuration = new ConfigurationBuilder()
    .AddCommandLine(args, mappings)
    .Build();
```
