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
- `Manifests`, one member per resource manifest the build captured;
- `Externals` for references that cross an application boundary;
- `Applications.<Name>` for referenced gateway applications, and `References` for the
  endpoint names they re-export;
- `UseGateway(args)` and the provider-specific configuration overload.

The generated surface has no resource verbs. The gateway's `Program.cs` names what it
composes by calling the hand-written verb of the area's ApplicationModel package over the
generated manifest, passing the area's typed options as an instance:

```csharp
using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Gateway.CreateBuilder(args);
IDatabaseResourceDescriptor database = builder.AddDatabase(Manifests.OrdersDatabase);
database.AddDatabase("inventory").AddPrincipal("inventory", "reader");
builder.AddWeb(Manifests.OrdersApi, new WebResourceOptions { Replicas = 2 });
builder.AddResource(Manifests.OrdersWorker); // a kind with no ApplicationModel in reach
builder.UseGateway(args);
await builder.Build().RunAsync();
```

The area verbs return the area's typed descriptor, which carries its command verbs:
Database exposes `AddDatabase` and `AddPrincipal`; ConfigurationStore exposes `SetValue`
and `RemoveValue`; SecretStore exposes `AddSecret` and `IssueCertificate`; IdentityHub
exposes `AddAudience` and `AddClient`; Rezolvr exposes `AddARecord` and `AddCnameRecord`.
LogSpace supplies typed options for the telemetry sink without command verbs. A
third-party ApplicationModel package composes the same way, through its own verb over
`Manifests.<Name>`; the SDK keeps no resource-kind table for it to join.

Nothing is injected by convention beyond the area ApplicationModel packages. No area
client package is restored: commands travel through the gateway's generic control-plane
command client. Store, certificate-authority, and trust providers come from opt-in
`<Area>.ApplicationModel.Orchestration` packages that the gateway references and registers
explicitly in `Program.cs` (for example `builder.UseSecretStore(...)`); a credential issuer or
caller authenticator is assigned on `builder.Providers` there as well.

Gateway inherits the base SDK's [project defaults](../../../Assimalign.Cohesion.Sdk/Tasks/docs/OVERVIEW.md#project-defaults):
`net10.0`, preview language/features, disabled implicit usings, enabled nullable
analysis, and AOT compatibility. The base follows Microsoft's library default;
Gateway retains unconditional
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
and generated in-process bindings. `Gateway.CreateBuilder(args)` registers each binding by
manifest identity, so it applies whichever verb adds the resource: the area's typed verb,
`AddResource`, or a third-party verb over `Manifests.<Name>`. Each binding uses
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
- Area injection is derived from the resource projects a gateway references. Each
  `CohesionResourceReference` project is read at evaluation time (so every restore engine,
  including Visual Studio's, sees the result) and its `Sdk="Assimalign.Cohesion.Sdk.<Area>"`
  attribute names the area; `Area` metadata on the reference wins over the attribute. Exactly
  those areas' `<Area>.ApplicationModel` packages are restored, at `$(CohesionVersion)` (or as
  repository projects inside cohesion). A gateway that composes an area it does not reference
  as a project, for example a resource that reaches it only through another project's closure,
  either adds that area's ApplicationModel package itself to call the typed verb or composes
  the manifest with the untyped `builder.AddResource(Manifests.<Name>)`.
- Stores, certificate authorities, and trust stores are explicit registrations from opt-in
  Orchestration packages, and credential issuers and caller authenticators are explicit
  `builder.Providers` assignments; a mount source whose store has no registration fails at
  `Build()` with the package and verb that provides it. A declared command fails the same way
  when its target's manifest marks the kind `requiresInputResolver` and the gateway registers no
  resolver for it: SecretStore's `AddSecret` needs `builder.UseSecretStore(...)` from
  `Assimalign.Cohesion.SecretStore.ApplicationModel.Orchestration`. Cross-application store
  sources are rejected at `Build()` for now; supporting them is a follow-up for when the store
  resources mature.
- The base SDK has no implicit framework reference, and Gateway keeps the shared
  auto-include switch disabled. An in-process gateway references
  `Assimalign.Cohesion.App` plus `App.<Area>` for exactly the referenced areas; App
  supplies Connections once for generated resource accessors and every area hosting
  module. An out-of-process gateway references no framework.

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
