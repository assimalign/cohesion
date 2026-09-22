# Assimalign.Cohesion.Sdk.ApplicationModel

`Assimalign.Cohesion.Sdk.ApplicationModel` is the opt-in MSBuild SDK for
Cohesion resource manifests, generated resource APIs, certificates, OCI image
production, and application image gathering. It is shipped separately from the
general-purpose `Assimalign.Cohesion.Sdk` so third parties can build compatible
resource-area SDKs without putting orchestration behavior in the base package.

## How consumers receive it

First-party resource-area SDKs import this package when a project body sets:

```xml
<CohesionApplicationModel>enabled</CohesionApplicationModel>
```

`Assimalign.Cohesion.Sdk.Gateway` imports it unconditionally because a gateway is
itself an enabled Composite resource. Disabled area consumers remain plain
applications and do not load this package's targets.

The importing area supplies `Version="$(CohesionVersion)"`. `CohesionVersion`
comes from the base SDK's shipped `Targets/Build.Version.props`, whose values are
frozen when the SDK package is packed. Consequently the sibling package resolves
at exactly the importing SDK's identity, including `.local`, without adding an
ApplicationModel SDK entry to `global.json`.

## Third-party area SDKs

A third-party area SDK can use only the shipped packages. Its `Sdk.props` imports
the base props, then its `Sdk.targets` uses this order after the consumer project
body:

```xml
<Import Project="Sdk.targets"
        Sdk="Assimalign.Cohesion.Sdk.ApplicationModel"
        Version="$(CohesionVersion)"
        Condition="'$(CohesionApplicationModel)' == 'enabled'" />
<Import Project="Sdk.targets"
        Sdk="Assimalign.Cohesion.Sdk"
        Condition="'$(_CohesionApplicationModelSdkImported)' != 'true'" />
```

An SDK that is always a resource uses the unconditional form:

```xml
<Import Project="Sdk.targets"
        Sdk="Assimalign.Cohesion.Sdk.ApplicationModel"
        Version="$(CohesionVersion)" />
<Import Project="Sdk.targets"
        Sdk="Assimalign.Cohesion.Sdk"
        Condition="'$(_CohesionApplicationModelSdkImported)' != 'true'" />
```

The ApplicationModel import supplies the base targets itself; the marker condition
prevents the area SDK from importing them twice. The enabled
resource defaults for `SelfContained`, `RuntimeIdentifier`, and
`ValidateExecutableReferencesMatchSelfContained` must exist before
`Microsoft.NET.Sdk.targets` consumes them.

## Generated outputs

An enabled executable produces:

- `resource.json`, the frozen `cohesion/resource/v1` manifest;
- `Resource.g.cs`, typed accessors for endpoints, mounts, settings, references,
  and resource properties;
- `ResourceControlPlane.g.cs`, registration for the area's default control
  plane; and
- image metadata and OCI archives when image targets are invoked.

The public MSBuild surface keeps its existing names, including
`CohesionResourceName`, `CohesionEndpoint`, `CohesionMount`, `CohesionSetting`,
`CohesionResourceReference`, `CohesionCreateResourceManifest`,
`CohesionPublishImage`, `CohesionPublishImages`, and
`CohesionBuildResourceContainersDependsOn`.

## Gateway typed-kind contributions

Packages can contribute a `CohesionGatewayResourceKind` item from
`build` or `buildTransitive` props. Gateway unions those rows with its
first-party rows and uses the matching ApplicationModel identity to emit a typed
`Add<Name>()` verb instead of the generic `AddResource` fallback.

| Field | Meaning |
| --- | --- |
| Item identity | Stable resource-kind name. |
| `ApplicationModel` | Required manifest `applicationModel` package identity used for matching. |
| `OptionsType` | Required fully qualified generated options type. |
| `DescriptorType` | Optional fully qualified return type; defaults to `IApplicationResourceDescriptor`. |
| `AddMethod` | Required fully qualified static factory method called with builder, manifest, and options. |

See [DESIGN.md](./DESIGN.md) for the complete item contract and build ordering.

## Image targets

`CohesionPublishImage` produces one resource image. `CohesionPublishImages`
gathers source and package-backed resource images for a gateway. Image runtime
identity is controlled independently by `CohesionImageRuntimeIdentifier`, which
defaults to an explicit build RID and then `linux-x64`. The frozen image index
records repository, verified digest, platform, AOT decision, base image, and an
optional contained archive.

`CohesionBuildResourceContainersDependsOn` remains the reserved extension hook
used by platform packages to add container gathering behavior.

## Diagnostics

The package owns COHSDK001 and COHSDK003–COHSDK010. The base package owns pin
validation (COHSDK002) and the missing-import guard (COHSDK011). The detailed
contracts are listed in [DESIGN.md](./DESIGN.md#diagnostics).
