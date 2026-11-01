# Assimalign.Cohesion.Sdk.Gateway Design

## Design intent

`Sdk.Gateway` makes an ordinary executable project a Cohesion application gateway with
the least the build can do: it defaults the project to an executable and references the
gateway package. Everything that describes the application is code in the gateway's
`Program.cs`: the model (`ApplicationGateway.CreateBuilder(name)` and one
`AddResource(provider, name)` per resource), the platform it runs on, and the run itself.

The SDK generates no source of its own. Its one build step hands the compiler the resource
names an in-process gateway puts on its project references, so the Cohesion source generator can
bind each resource's program (In-process resource bindings, below). Its name is what the CLI's
gateway discovery and consumers' `global.json` pins rely on.

## Package and import shape

The package has the normal Cohesion MSBuild SDK layout:

```text
Sdk/Sdk.props
Sdk/Sdk.targets
Targets/Sdk.Gateway.props
Targets/Sdk.Gateway.targets
Targets/Build.Version.props   (frozen at pack time)
```

The package carries no `Tasks/` folder: the SDK runs no task, so its project sets
`CohesionSdkPackTasks=false` and takes no MSBuild package reference.

```mermaid
flowchart LR
    Gateway["Assimalign.Cohesion.Sdk.Gateway"] --> Base["Assimalign.Cohesion.Sdk"]
    Base --> DotNet["Microsoft.NET.Sdk"]
    Gateway -.->|"PackageReference"| Package["Assimalign.Cohesion.ApplicationModel.Gateway"]
```

The solid arrows are SDK imports; the dotted arrow is the one package reference the SDK
adds to the consumer.

- `Sdk/Sdk.props` defaults `OutputType` to `Exe` when it is empty, before it imports the
  base props (which import `Microsoft.NET.Sdk`'s, where `Library` would otherwise win),
  then imports `Targets/Sdk.Gateway.props`. A consumer's own `OutputType` still wins. It
  does not set the resource-SDK marker, so the base SDK adds no App framework.
- `Targets/Sdk.Gateway.props` adds
  `<PackageReference Include="Assimalign.Cohesion.ApplicationModel.Gateway" Version="$(CohesionVersion)" IsImplicitlyDefined="true" />`.
  `$(CohesionVersion)` comes from the base SDK's frozen `Build.Version.props`, which
  `Sdk.props` imports first; this SDK's own frozen copy ships but is not imported. A
  consumer pins both SDKs to one version in `global.json`, so the two agree. The reference is the
  package's whole closure: Definition, Protocol, and the identity package.
  `CohesionGatewayImplicitReference=false` removes it for a project that pins the package
  itself.
- `Sdk/Sdk.targets` defaults an executable gateway to `SelfContained=true` for the host
  runtime identifier (`RuntimeIdentifier=$(NETCoreSdkRuntimeIdentifier)`) with
  `ValidateExecutableReferencesMatchSelfContained=false`, the defaults a resource
  executable takes, then imports the base targets and `Targets/Sdk.Gateway.targets`, which
  holds the in-process resource bindings target. A project that sets `SelfContained` or `RuntimeIdentifier` keeps its values.

## Decisions

- **No framework.** A gateway manages resources through their platform; it never loads a
  resource's runtime, so it takes no `App` or `App.<Area>` framework and no area package.
  The old in-process mode added App and the referenced areas' frameworks; the in-process
  platform now runs programs the gateway references itself.
- **Self-contained by default.** An in-process gateway references each resource's
  `Sdk.<Area>` project, and resource executables are self-contained and RID-specific. A
  framework-dependent gateway then fails NETSDK1151 at build; with that check off it cannot
  start, because the App and App.<Area> framework references flow in transitively and those
  frameworks ship only as runtime packs ("Framework: 'Assimalign.Cohesion.App' ... No
  frameworks were found"). Self-contained output carries the runtime packs, so the
  in-process gateway builds and runs. A gateway with no in-process resource pays only the
  larger output and may set `SelfContained=false`.
- **No platform by default.** A gateway runs on exactly the platform its `Program.cs`
  registers, so the platform is the consumer's own package reference: the in-process
  gateway half (`Assimalign.Cohesion.ApplicationModel.Gateway.InProcess`) or a
  `cohesion-platforms` gateway half such as `Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes`.
  Choosing one by an MSBuild property would hide the dependency the
  code already names.
- **No generated model.** Identity and topology are values the program declares. Nothing in
  the build has to agree with them, so nothing can drift from them. The in-process bindings
  below generate the binding of a program to a resource name the reference states, not the
  model: the program still declares `AddResource(provider, name)`.
- **In-process resource bindings (2026-11-01).** An in-process gateway names each resource it
  runs on the project reference:

  ```xml
  <ProjectReference Include="..\Shop.Orders\Shop.Orders.csproj" CohesionResource="orders-api" />
  ```

  `CohesionProvider="Web"` on the reference overrides the provider the resource's build
  recorded. `CohesionAddInProcessResources` in `Targets/Sdk.Gateway.targets` runs after
  `ResolveAssemblyReferences` and before `GenerateMSBuildEditorConfigFileShouldRun`. It turns
  each resolved `ReferencePath` that carries `CohesionResource` into an `AdditionalFiles` item
  (the referenced project file, so an analyzer that reads additional files never reads an
  assembly) with `CohesionResource`, `CohesionProvider` and `CohesionResourceAssembly` (the
  reference's file name), and the SDK declares the three as `CompilerVisibleItemMetadata`. The
  Cohesion source generator, which reaches the gateway through the Core package, reads them when
  the gateway references `Assimalign.Cohesion.ApplicationModel.Gateway.InProcess` and emits a
  module initializer that registers each program in `InProcessProgramCatalog`, so a bare
  `UseInProcess()` runs every referenced resource (Gateway.InProcess I15). Without that package
  the metadata is inert. The target ships only in this SDK: a plain `Microsoft.NET.Sdk` gateway
  binds its programs with `AddProgram`.
- **Keep the SDK at all.** A plain `Microsoft.NET.Sdk` project with the package reference
  works too. The SDK stays because it is the marker the CLI discovers (`cohesion run`), it
  keeps the package version in step with the rest of the Cohesion SDK pins, and it carries
  the base SDK's defaults and pin validation.

## Verification

`Tasks/tests` builds a package-only consumer (`TestProjects/GatewaySdkConsumer`) against the
packed SDKs and the gateway package closure in `_out/packages`, in an isolated workspace that
links the base SDK's `ConsumerWorkspace` (`COHESION_GATEWAY_SDK_TESTS`). The consumer's
`Program.cs` declares a model with no reference of its own; the test asserts the build
succeeds, the output carries the gateway assembly and an apphost, and no App framework,
manifest, or generated gateway source appears. A second consumer
(`TestProjects/GatewayInProcessConsumer`) is the in-process path end to end. It references a
top-level `Sdk.Web` resource (`TestProjects/GatewayWebResource`, which calls
`AddResource("smoke-api", resource => resource.UseInProcess())` and answers one request) with
`CohesionResource="smoke-api"`, and builds its gateway with a bare `UseInProcess()`. The test
asserts the build passes without NETSDK1151 or a `COHAPM` diagnostic, and that the built gateway
starts the resource in its own process, reads its endpoint output, gets the resource's answer
over HTTP, then stops and disposes with exit code 0. It needs the App and App.Web packs for the
host RID, the Core package (which carries the generator), and the ApplicationModel packages,
Hosting.InProcess and Gateway.InProcess included, in the feed.
`.github/workflows/sdk-smoke.yml` packs the closure and the frameworks and runs both on each
OS leg.

## Non-goals

The SDK does not generate a model, select platforms, publish images, or describe a model at
build time. It reads no other project beyond the metadata on its own references.
