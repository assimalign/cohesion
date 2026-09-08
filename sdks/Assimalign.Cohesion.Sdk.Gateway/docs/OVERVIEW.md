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
`GatewayType`, `OptionsType`, and `RequiresJit` metadata. Generated code dispatches only
across those contributed items; the Cohesion SDK does not contain platform-type names.

`Local` is contributed by `Assimalign.Cohesion.ApplicationModel.Gateway`. External
platform packages are restored only when their names appear in `CohesionGateways`, at
`CohesionPlatformsVersion`. A provider with `RequiresJit=true` makes
`CohesionGatewayAot=auto` select `PublishAot=false` for the whole gateway executable.

## Current implementation gates

The SDK must not be presented as a complete production gateway until these dependency
contracts are available and covered by package-boundary CI:

- `Assimalign.Cohesion.ApplicationModel.Gateway.InProcess` is not present; selecting
  InProcess is rejected.
- `Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane` is not present; the
  generated Composite manifest records its control-plane contract, but no gateway
  control-plane host can serve it yet.
- Docker and Kubernetes provider packages and their `CohesionGatewayProvider`
  contributions live outside this repository and require an agreed
  `CohesionPlatformsVersion`.
- Web and Database are the only typed area ApplicationModel packages currently shipped.
  Other manifest kinds use the generic `ResourceOptions`/`AddResource` path until their
  area packages land.
- Manifest-derived ApplicationModel and mount-client requirements become known after
  restore. The current guarded implementation makes the shipped Web, Database,
  SecretStore client, and ConfigurationStore client dependencies available up front;
  it must not expand that fallback to every future area package.
- The NuGet-only boundary requires the Gateway SDK to suppress the base SDK's implicit
  `Assimalign.Cohesion.App` reference before the base props import. The guarded
  in-process bridge currently names the shipped Web and Database frameworks; selective
  per-manifest framework injection belongs with the missing in-process package contract.

See [Design](./DESIGN.md) for the build ordering, dependency boundary, and recommended
first-restore contract.
