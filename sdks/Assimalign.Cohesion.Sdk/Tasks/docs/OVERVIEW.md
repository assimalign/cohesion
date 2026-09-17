# Assimalign.Cohesion.Sdk

`Assimalign.Cohesion.Sdk` is the common MSBuild SDK for Cohesion applications and
the base imported by every resource-area SDK. It chains to `Microsoft.NET.Sdk`,
adds the Cohesion application framework reference, and owns common source
generation and validation.

## Project defaults

The base SDK supplies the following defaults to every `Assimalign.Cohesion.Sdk`
and `Sdk.<Area>` consumer, including Gateway:

| Property | Default |
| --- | --- |
| `OutputType` | `Exe` |
| `TargetFramework` | `net10.0` |
| `LangVersion` | `Preview` |
| `EnablePreviewFeatures` | `true` |
| `ImplicitUsings` | `disable` |
| `Nullable` | `enable` |
| `IsAotCompatible` | `true` |

Each base default uses `Condition="'$(PropertyName)' == ''"` and is overridable.
The conditions honor command-line `-p:` global properties. A consumer's
`Directory.Build.props` and csproj body are evaluated later and override the
defaults through MSBuild's last-assignment-wins rule; those consumer values are
not visible when the conditions run.

`Targets/Assimalign.Cohesion.Sdk.Defaults.props` is the first import in the base
`Sdk.props`, before `Microsoft.NET.Sdk`'s `Sdk.props`. Microsoft's props already
default `OutputType` to `Library`, so an emptiness condition after that import
would never select `Exe`. The shipped TFM is a literal because the repository's
`build/Targets/Build.TargetFramework.props` is not shipped. That repository-side
source, this literal, and the SDK's `KnownFrameworkReference` TFMs must move together.

This implements developer-experience design §4.1: `Directory.Build.props` carries
identity only (`CohesionOrganization`, `CohesionContainerRegistry`, `VersionPrefix`).
A resource needs its SDK declaration, Cohesion items/properties, and `Program.cs`.
Library-style base-SDK consumers pulled in through `CohesionProjectReference`
declare `<OutputType>Library</OutputType>` explicitly; the
`Sdk.Gateway/tests/TestProjects/GatewaySmokeSupport` fixture is an in-repo example.

Two constraints apply:

- A consumer's `TargetFrameworks` (plural) is invisible at props time. Setting it
  leaves both `TargetFramework` and `TargetFrameworks` populated, so Microsoft's
  `Sdk.targets` does not set `IsCrossTargetingBuild=true`. Resource executables
  never multi-target (design R3).
- On the latest TFM, `Microsoft.NET.Sdk.Common.targets` forces
  `LangVersion=Preview` whenever `EnablePreviewFeatures=true`. The base SDK's
  language default is therefore effectively redundant: override `LangVersion`
  together with `EnablePreviewFeatures` (for example, `14.0` and `false`).

Gateway preserves two stricter assignments: `Targets/Sdk.Gateway.props` sets
`IsAotCompatible=true` unconditionally after the consumer's
`Directory.Build.props`, and `Sdk/Sdk.targets` forces `OutputType=Exe` after
the csproj body. These Gateway assignments are not conditional base defaults;
in particular, a csproj `OutputType=Library` cannot turn a gateway into a library.

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
| COHSDK003 | Error | NativeAOT image production has no available host or in-container route. |
| COHSDK004 | Warning or error | A packed resource lacks a digest-pinned image; `CohesionImageRequired=true` promotes the diagnostic to an error. |
| COHSDK005 | Error | A framework-dependent container cannot start because its base lacks Cohesion shared frameworks. |
| COHSDK008 | Error | `CohesionApplicationModel` is enabled for a project whose output is not an executable. |
| COHSDK009 | Error | A `CohesionResourceProperty` key does not use the current resource kind's prefix. |
| COHSDK010 | Error | HTTPS Certificate must name a declared Secret mount; non-HTTPS Certificate metadata is rejected. Reserved `public` is exempt. |

See [DESIGN.md](./DESIGN.md) for the implementation contracts and
[`docs/VERSIONING.md`](../../../../docs/VERSIONING.md) for repository-wide package
and pin versioning rules.

## Publishing images

Set `CohesionOrganization` (or an explicit lowercase `CohesionContainerRepository`) and run
`dotnet publish -c Debug -t:CohesionPublishImage` for a daemon-free OCI archive. The image
uses a self-contained Linux x64 apphost and the .NET 10 runtime-deps base. Release requires
NativeAOT; the SDK probes host capability and the in-container CLI route, and reports COHSDK003
when unavailable. The in-container build recipe is still awaiting specification.

`CohesionImageAot=auto|true|false` follows the [decision table](./DESIGN.md#container-image-production).
Release `false` is diagnosed as a deviation. `CohesionImageFreshness=Rebuild` hashes publish
inputs and verifies cached image artifacts before skipping image creation. `Pinned` applies
provisionally to package-only resources in the gateway gather and builds nothing.

`CohesionContainerBaseImage=auto`, `CohesionContainerPush=false`, and
`CohesionContainerArchiveOutputPath=$(IntermediateOutputPath)cohesion/images/$(CohesionResourceName).tar`
are the defaults. The frozen `cohesion/image/v1` index at
`$(IntermediateOutputPath)cohesion/image.json` records the repository, verified digest,
`linux/amd64`, `aot`, base image, and a contained relative archive. Registry is late-bound;
an actual push pins only the authority. A registry sink omits `archive` entirely.
`linux-musl-x64` currently uses `linux/amd64` plus an Alpine base identity; the variant decision is open.

`CohesionPackImageArchive=true` ships the archive under `cohesion/images/` to preserve
index containment. Uncontained archive destinations are errors. Framework-dependent
`PublishContainer` is COHSDK005. Exactly one SDK image creation selects archive or registry.
Digest-preserving `CohesionContainerPushTool` transfer is deferred; Cohesion's own images
push only from `release.yml`.
