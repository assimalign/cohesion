# Assimalign.Cohesion.Sdk.Gateway

## Summary

`Assimalign.Cohesion.Sdk.Gateway` is the MSBuild SDK for a Cohesion application
gateway. A gateway project is a `Program.cs` executable that reads resource manifests,
generates the application-specific composition surface, and selects a gateway provider
contributed through MSBuild metadata.

Unlike a resource-area SDK, `Sdk.Gateway` has no matching shared framework. Its
orchestration dependencies are ordinary NuGet or project references, and there is no
`Assimalign.Cohesion.App.Gateway` package.

## Project contract

A gateway project declares its application identity, the providers it permits, and its
resource roots:

```xml
<Project Sdk="Assimalign.Cohesion.Sdk.Gateway">
  <PropertyGroup>
    <CohesionApplicationName>appa</CohesionApplicationName>
    <CohesionGateways>Local;Docker;Kubernetes</CohesionGateways>
  </PropertyGroup>
  <ItemGroup>
    <CohesionResourceReference Include="..\Example.AppA.Api\Example.AppA.Api.csproj" />
    <CohesionResourceReference Include="Example.AppA.Worker.Manifest" Version="1.4.0" />
  </ItemGroup>
</Project>
```

`CohesionApplicationModel` is always `enabled`; a consumer cannot turn it off. The
Gateway SDK therefore imports `Assimalign.Cohesion.Sdk.ApplicationModel`
unconditionally and produces its own Composite resource manifest as well as generated
gateway source. The generated surface includes:

- `Gateway.CreateBuilder(args)` with the application name compiled into the call;
- `Manifests` and one same-application `Add<Name>()` verb per resource (there is no
  `AddAllResources()`: the gateway names what it composes);
- `Externals` for references that cross an application boundary;
- `Applications.<Name>` for referenced gateway applications;
- `UseGateway(args)` and the provider-specific configuration overload.

Generated Web, Database, ConfigurationStore, SecretStore, IdentityHub, Rezolvr, and
LogSpace `Add*` verbs accept the area's typed options and return its typed descriptor.
Database exposes `AddDatabase` and `AddPrincipal`; ConfigurationStore exposes `SetValue`
and `RemoveValue`; SecretStore exposes `AddSecret` and `IssueCertificate`; IdentityHub
exposes `AddAudience` and `AddClient`; Rezolvr exposes `AddARecord` and `AddCnameRecord`.
LogSpace supplies typed options for the telemetry sink without command verbs.
Command-bearing manifests advertise accepted kinds as bare strings in `commands` and
require their narrow client package, even when they are not used as mount sources.

The typed-kind table is open. `Targets/Sdk.Gateway.props` contributes the first-party
`CohesionGatewayResourceKind` rows, and referenced packages may contribute their own
rows from `build` or `buildTransitive` props. Each row names its resource kind,
ApplicationModel package, options type, optional descriptor type, and static add
method. Gateway unions all rows before source generation, so a third-party
ApplicationModel package can produce a typed verb without changing this SDK. See the
[ApplicationModel SDK contract](../../../Assimalign.Cohesion.Sdk.ApplicationModel/Tasks/docs/DESIGN.md#typed-gateway-resource-kind-contract).

Gateway inherits the base SDK's [project defaults](../../../Assimalign.Cohesion.Sdk/Tasks/docs/OVERVIEW.md#project-defaults):
`Exe`, `net10.0`, preview language/features, disabled implicit usings, enabled
nullable analysis, and AOT compatibility. It retains unconditional
`IsAotCompatible=true` in its props (after consumer `Directory.Build.props`) and
`OutputType=Exe` in its targets (after the csproj body). The other base defaults
follow the normal consumer override rules and documented language/TFM constraints.

## SDK pins

The Gateway SDK imports the base Cohesion SDK. NuGet's nested base-SDK import does
not inherit an inline version, so a consumer must pin the base and Gateway identities:

```json
{
  "sdk": {
    "version": "10.0.300",
    "rollForward": "latestFeature"
  },
  "msbuild-sdks": {
    "Assimalign.Cohesion.Sdk": "10.0.0-preview.1",
    "Assimalign.Cohesion.Sdk.Gateway": "10.0.0-preview.1"
  }
}
```

The two Cohesion versions must agree. Gateway imports the sibling ApplicationModel
SDK with `Version="$(CohesionVersion)"`, so that package needs no `global.json` pin.
Repositories that use other Cohesion SDKs pin those selected identities at the same
version as well.

## Provider channel

`CohesionGateways` is a semicolon-delimited allow-list. Provider packages contribute a
`CohesionGatewayProvider` item through `buildTransitive` props with `Name`,
`GatewayType`, `OptionsType`, and `RequiresJit` metadata. A provider may also contribute
`CommandLineApplyMethod`, a fully-qualified public static
`void Apply(OptionsType, string[])` method. Generated `UseGateway(args)` calls that hook only
for the selected provider and passes the original argument array after applying common options,
so platform switches such as `--context` and `--kubeconfig` remain provider-owned. Generated code
dispatches only across those contributed items; the Cohesion SDK does not contain platform-type
names.

`Local` is contributed by `Assimalign.Cohesion.ApplicationModel.Gateway`; `InProcess` is
contributed by `Assimalign.Cohesion.ApplicationModel.Gateway.InProcess`. External platform
packages are restored only when their names appear in `CohesionGateways`, at
`CohesionPlatformsVersion`. A provider with `RequiresJit=true` makes
`CohesionGatewayAot=auto` select `PublishAot=false` for the whole gateway executable.

`Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane` is a fixed gateway dependency rather
than a selectable provider. Generated `UseGateway(args)` composition installs its authenticated
resolver client in every mode and serves its listener for the realizing `Run` and `Apply` modes;
`Describe`, `Render`, and `Bootstrap` stay listener-free.

Selecting `InProcess` does not by itself authorize resource assembly loading. A gateway with
project resources must also set `CohesionGatewayInProcess=true`. The two-factor gate promotes
only enabled, composable, same-application project resources into compiler/runtime references
and generated in-process bindings. Each binding uses
`AppContext.BaseDirectory/cohesion/resources/<resource-name>`; build and publish copy that
resource's declared content into the isolated root and disable ordinary transitive content
flattening. Selected managed, native, satellite, and RID-specific runtime files are also carried
into build and publish output. Their package identities remain outside the gateway lock file until
the restore-visible resource dependency descriptor described in the design is available.

## Current implementation gates

The SDK must not be presented as a complete production gateway until these dependency
contracts are available and covered by package-boundary CI:

- Docker and Kubernetes provider packages and their `CohesionGatewayProvider`
  contributions live outside this repository and require an agreed
  `CohesionPlatformsVersion`.
- Web, Database, ConfigurationStore, SecretStore, IdentityHub, Rezolvr, and LogSpace are
  the first-party typed ApplicationModel mappings. Referenced packages may add more
  `CohesionGatewayResourceKind` rows. A manifest with no matching restore-visible row
  uses the generic `ResourceOptions`/`AddResource` path.
- Area injection is derived from the resource projects a gateway references. Each
  `CohesionResourceReference` project is read at evaluation time (so every restore engine,
  including Visual Studio's, sees the result) and its `Sdk="Assimalign.Cohesion.Sdk.<Area>"`
  attribute names the area; `Area` metadata on the reference wins over the attribute. Exactly
  those areas' `<Area>.ApplicationModel` packages are restored, at `$(CohesionVersion)` (or as
  repository projects inside cohesion). A gateway that composes an area it does not reference
  as a project, for example a resource that reaches it only through another project's closure,
  adds that area's ApplicationModel package itself; until it does, the generated verb for that
  resource uses the untyped path and the build reports `COHGW003`. The SecretStore, Database,
  and ConfigurationStore clients stay restore-visible for every gateway because mount sources
  and command targets can name a store no referenced project introduces; T11's restore-visible
  producer descriptor remains the future contract for them.
- The NuGet-only boundary requires the Gateway SDK to suppress the base SDK's implicit
  `Assimalign.Cohesion.App` reference before the base props import. An in-process gateway
  references `Assimalign.Cohesion.App` plus `App.<Area>` for exactly the referenced areas; an
  out-of-process gateway references no framework.

See [Design](./DESIGN.md) for the build ordering, dependency boundary, and recommended
first-restore contract.

## Publishing the image index

Run `dotnet publish -c Debug -p:CohesionGatewayAot=false -t:CohesionPublishImages` to publish source resources incrementally
and gather `application.images.json` beside the gateway publish output. Manifest-package
resources use their pinned `cohesion/image.json` without rebuilding. Release resources require
NativeAOT under the ApplicationModel SDK's COHSDK003/005 rules.

The frozen `cohesion/images/v1` document keeps declaration order, unique resource names,
and the application's name. Entries omit `schema`. Referenced archives are copied under the
index directory and their `archive` fields rewritten to contained relative paths.
An active, InProcess-only gateway publishes one composite image; mixed provider sets retain
the per-member images needed by their `ArtifactRef.Self` plans.
