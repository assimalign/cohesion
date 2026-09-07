# Assimalign.Cohesion.Core Design

## Design Intent

This package is deliberately broad but lightweight. It collects reusable system-level building blocks so the higher-level packages can share a common vocabulary without depending on each other.

## Implementation Note

Examples below document the intended public shape; some members still throw NotImplementedException today.

## Architecture

- Core value types such as Size and Glob capture reusable concepts that show up across multiple libraries.
- Exception and system extension helpers centralize small cross-cutting behaviors.
- The library is dependency-light by design so other Core packages can reference it safely.

## Frozen runtime contract

`ResourceEnvironment` is the canonical .NET expression of the version 1 gateway-to-resource
contract documented in `docs/RUNTIME_CONTRACT.md`. Its fixed names and parameterized patterns are
`public const string` values. Constants are inlined into consuming assemblies, so changing any
value is a breaking wire-contract change rather than an ordinary implementation change.

Endpoint, resource, and mount components use one ordinal ASCII normalization rule: lowercase
ASCII is uppercased, ASCII letters and digits are retained, and each other character becomes one
underscore. Typed endpoint readers return `System.Uri`: the BCL type already owns scheme, host,
port, path, parsing, and equality, so Core does not duplicate that state in a framework-specific
address type. A declared endpoint's HOST/PORT/SCHEME values describe its bind address; its
separately advertised PUBLIC_URL remains available through the URI reader. Dependency URLs may
carry an optional path.

`UriExtensions` is a single C# 14 extension block in the `System` namespace for the endpoint
behavior the BCL does not supply directly: `CreateEndpoint`, `TryCreateEndpoint`,
`TryParseEndpoint`, `ThrowIfNotEndpoint`, `IsEndpoint`, `EndpointPath`, and
`ToEndpointString()`. An endpoint is absolute, has a host and an explicit port from 1 through
65535, and has no user information, query, or fragment. `ToEndpointString()` is the sole writer
for runtime-contract URL values: `scheme://host:port[/path]`, with an escaped path and no trailing
slash for the root. Code crossing into socket APIs uses `Uri.IdnHost` by convention so IPv6
literals are unbracketed and internationalized hosts use their ASCII-compatible form.

`AppEnvironment` owns the single environment-name rule:
`COHESION_ENVIRONMENT ?? DOTNET_ENVIRONMENT ?? "Production"`. ApplicationModel delegates to this
Core rule rather than parsing process variables itself. Both process-environment and dictionary
readers live in Core so the ambient `Assimalign.Cohesion.Hosting.Resources.ResourceContext` can
carry the same keys without duplicating their parsing rules.

`ResourceContext` and `ResourceRuntime` live in `Assimalign.Cohesion.Hosting.Resources`, not
Core; they are the in-process carrier delivered by runtime-contract item 12.

## AOT posture

Runtime-contract parsing uses direct string, integer, and URI operations. It performs no
reflection, assembly scanning, dynamic code generation, or runtime serialization.

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

string environmentName = AppEnvironment.GetEnvironmentName();
```
