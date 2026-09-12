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
gateway therefore produces its own Composite resource manifest as well as generated
gateway source. The generated surface includes:

- `Gateway.CreateBuilder(args)` with the application name compiled into the call;
- `Manifests`, same-application `Add*` verbs, and `AddAllResources()`;
- `Externals` for references that cross an application boundary;
- `Applications.<Name>` for referenced gateway applications;
- `UseGateway(args)` and the provider-specific configuration overload.

Generated Database, ConfigurationStore, and Web `Add*` verbs return the area's typed
descriptor. Database descriptors expose `AddDatabase` and `AddPrincipal`;
ConfigurationStore descriptors expose `SetValue` and `RemoveValue`. Their manifests
advertise accepted kinds as bare strings in `commands`. A command-bearing target also
requires its narrow client package, even when it is not used as a mount source.

Gateway inherits the base SDK's [project defaults](../../Assimalign.Cohesion.Sdk/docs/OVERVIEW.md#project-defaults):
`Exe`, `net10.0`, preview language/features, disabled implicit usings, enabled
nullable analysis, and AOT compatibility. It retains unconditional
`IsAotCompatible=true` in its props (after consumer `Directory.Build.props`) and
`OutputType=Exe` in its targets (after the csproj body). The other base defaults
follow the normal consumer override rules and documented language/TFM constraints.

## SDK pins

The Gateway SDK imports the base Cohesion SDK. NuGet's nested MSBuild SDK import does
not inherit an inline version, so a consumer must pin both SDK identities:

```json
{
  "sdk": {
    "version": "10.0.300",
    "rollForward": "latestFeature"
  },
  "msbuild-sdks": {
    "Assimalign.Cohesion.Sdk": "10.0.1-preview.3",
    "Assimalign.Cohesion.Sdk.Gateway": "10.0.1-preview.3"
  }
}
```

The two Cohesion versions must agree. Repositories that use other Cohesion SDKs pin
those identities at the same version as well.

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
- Web, Database, and ConfigurationStore are the typed area ApplicationModel mappings.
  Other manifest kinds use the generic `ResourceOptions`/`AddResource` path until their
  area packages land.
- Manifest-derived ApplicationModel and mount/command-client requirements become known after
  restore. The current guarded implementation makes the shipped Web, Database,
  ConfigurationStore, SecretStore client, Database client, and ConfigurationStore client dependencies available up front;
  it must not expand that fallback to every future area package.
- The NuGet-only boundary requires the Gateway SDK to suppress the base SDK's implicit
  `Assimalign.Cohesion.App` reference before the base props import. The in-process bridge
  currently names the shipped Web and Database frameworks; selective per-manifest framework
  injection remains future work as more typed areas ship.

See [Design](./DESIGN.md) for the build ordering, dependency boundary, and recommended
first-restore contract.

## Publishing the image index

Run `dotnet publish -c Debug -p:CohesionGatewayAot=false -t:CohesionPublishImages` to publish source resources incrementally
and gather `application.images.json` beside the gateway publish output. Manifest-package
resources use their pinned `cohesion/image.json` without rebuilding. Release resources require
NativeAOT under the base SDK's COHSDK003/005 rules.

The frozen `cohesion/images/v1` document keeps declaration order, unique resource names,
and the application's name. Entries omit `schema`. Referenced archives are copied under the
index directory and their `archive` fields rewritten to contained relative paths.
An active, InProcess-only gateway publishes one composite image; mixed provider sets retain
the per-member images needed by their `ArtifactRef.Self` plans.
