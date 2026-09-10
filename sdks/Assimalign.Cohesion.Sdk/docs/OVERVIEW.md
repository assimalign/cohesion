# Assimalign.Cohesion.Sdk

`Assimalign.Cohesion.Sdk` is the common MSBuild SDK for Cohesion applications and
the base imported by every resource-area SDK. It chains to `Microsoft.NET.Sdk`,
adds the Cohesion application framework reference, and owns common source
generation and validation.

## Strongly typed settings

Settings generation is opt-in. Set the generated root type name in the consumer
project:

```xml
<PropertyGroup>
    <CohesionAppSettingsClass>CatalogSettings</CohesionAppSettingsClass>
</PropertyGroup>
```

The namespace defaults to `RootNamespace`; set
`CohesionAppSettingsNamespace` to override it. When
`CohesionAppSettingsClass` is unset, the SDK generates no settings source and
adds no generated settings file to `Compile`.

At build time the SDK merges the shapes of `appsettings*.json` and generates a
public root class, public nested classes, and an instance
`Bind(IConfiguration)` method. Objects, arrays, strings, Boolean values, integer
values, and floating-point values are supported. `Bind` reads each known leaf by
its full Cohesion configuration path through `IConfiguration.GetEntry`; it does
not use reflection or the reflection-based `ConfigurationBinder`. A missing
configuration leaf leaves the corresponding property unchanged.

## SDK pin validation

During restore and build, the base SDK walks upward from the consumer project
directory to find the nearest `global.json`. If one is found, COHSDK002 requires
every present, recognized `Assimalign.Cohesion.Sdk*` pin to use exactly the same
version string and requires the pinned .NET SDK version to be `10.0.300` or
newer. Exact string agreement deliberately accepts local identities such as
`10.0.1-preview.3.local` when every Cohesion SDK pin uses that identity.

A consumer with no `global.json` is unchanged. Tooling that must inspect a
temporarily inconsistent tree can set `CohesionSkipSdkPinCheck=true`; normal
builds should not set the escape property.

## Diagnostics

| Code | Severity | Meaning |
| --- | --- | --- |
| COHSDK001 | Error | A referenced resource project has `CohesionApplicationModel` disabled. |
| COHSDK002 | Error | Cohesion SDK pins disagree, the pinned .NET SDK is invalid or below `10.0.300`, or the nearest `global.json` cannot be read. |
| COHSDK004 | Warning or error | A packed resource lacks a digest-pinned image; `CohesionImageRequired=true` promotes the diagnostic to an error. |
| COHSDK008 | Error | `CohesionApplicationModel` is enabled for a project whose output is not an executable. |
| COHSDK009 | Error | A `CohesionResourceProperty` key does not use the current resource kind's prefix. |

See [DESIGN.md](./DESIGN.md) for the implementation contracts and
[`docs/VERSIONING.md`](../../../docs/VERSIONING.md) for repository-wide package
and pin versioning rules.
