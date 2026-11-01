## Cohesion SDKs

Cohesion ships a family of MSBuild SDKs: one per resource area and one for
application gateways. They chain through a common base
(`Assimalign.Cohesion.Sdk`), which itself chains through `Microsoft.NET.Sdk`.
None of them generates application-model sources: a resource makes itself a
managed instance in its `Program.cs`, and a gateway declares its model in its own.
Pick the SDK that matches what you're building:

| SDK | Use when… |
| --- | --- |
| `Assimalign.Cohesion.Sdk`          | Cohesion library, or an explicitly configured executable with no resource-area behavior. |
| `Assimalign.Cohesion.Sdk.Web`      | HTTP / web-surface application. [Design and NativeAOT diagnostics](Assimalign.Cohesion.Sdk.Web/Tasks/docs/DESIGN.md). |
| `Assimalign.Cohesion.Sdk.Database` | Database-resident application (migrations, seeded schemas, etc.). |
| `Assimalign.Cohesion.Sdk.Gateway`  | Application gateway: an executable that declares an application model and runs it on a platform. |

Every resource area under `resources/` has a matching SDK (`Sdk.ApiManager`,
`Sdk.ConfigurationStore`, `Sdk.EventHub`, …). `Sdk.Gateway` is deliberately not
a resource-area SDK: it references one NuGet package and has no matching
`Assimalign.Cohesion.App.Gateway` framework.

## Consumption

The base SDK can be pinned inline:

```xml
<Project Sdk="Assimalign.Cohesion.Sdk/10.0.0-preview.1">
</Project>
```

The base SDK follows `Microsoft.NET.Sdk` and defaults `OutputType` to `Library`.
Every resource-area SDK marks itself before importing the base props, which selects
the base file's pre-.NET `OutputType=Exe` default; a consumer's `OutputType` in its
project body still wins. All of them supply `TargetFramework=net10.0`,
`LangVersion=Preview`, `EnablePreviewFeatures=true`, `ImplicitUsings=disable`,
`Nullable=enable`, and `IsAotCompatible=true`. Defaults are conditional on empty
values, so `-p:` properties and later consumer assignments are honored. Resource
executables do not multi-target, and a language-version override also requires
overriding `EnablePreviewFeatures`. Gateway defaults to an executable without the
area-SDK marker, so it stays framework-dependent. See the base SDK's
[project defaults](./Assimalign.Cohesion.Sdk/Tasks/docs/DESIGN.md#project-defaults-and-evaluation)
for import ordering and override constraints.

Layered Cohesion SDKs import the base SDK without an inline version. Pin the
selected SDK and `Assimalign.Cohesion.Sdk` in `global.json`; a Gateway consumer's
minimum pin set is:

```json
{
    "sdk": {
        "version": "10.0.300",
        "rollForward": "latestFeature"
    },
    "msbuild-sdks": {
        "Assimalign.Cohesion.Sdk":         "10.0.0-preview.1",
        "Assimalign.Cohesion.Sdk.Gateway": "10.0.0-preview.1"
    }
}
```

The SDK is resolved by NuGet's built-in MSBuild SDK resolver — the same machinery
that handles `Microsoft.NET.Sdk.Web`, `Microsoft.NET.Sdk.Worker`, etc. Works in
Visual Studio, Rider, the dotnet CLI, and any other MSBuild client with no
installer, no admin rights, and no custom resolver.

The base SDK validates the nearest `global.json` during restore and build. Every
present Cohesion SDK pin must use one exact version string and the pinned .NET
SDK must be `10.0.300` or newer; disagreement reports COHSDK002. This is exact
agreement among the pins, so the local identity `10.0.0-preview.1.local` is valid
when used consistently. Consumers without a `global.json` are unaffected. The
`CohesionSkipSdkPinCheck=true` escape is reserved for tooling that must inspect a
temporarily inconsistent tree.

## Strongly typed settings

The base SDK generates strongly typed settings only when a project opts in:

```xml
<PropertyGroup>
    <CohesionAppSettingsClass>AppSettings</CohesionAppSettingsClass>
</PropertyGroup>
```

The generated root and nested types are public. The root type includes an
AOT-safe `Bind(Assimalign.Cohesion.Configuration.IConfiguration)` method made of
explicit per-member reads; it does not use the reflection binder. When the
property is unset, no settings source is generated or compiled. See the base
SDK [overview](./Assimalign.Cohesion.Sdk/Tasks/docs/OVERVIEW.md) and
[design](./Assimalign.Cohesion.Sdk/Tasks/docs/DESIGN.md).

## Managed resources

No SDK has an application-model switch, and no build step generates a manifest or a
resource accessor. An application of an area makes itself a managed resource in its
`Program.cs`, through the area's `Assimalign.Cohesion.<Area>.Hosting.ApplicationModel`
integration, which the `App.<Area>` framework carries:


```csharp
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddResource("orders-api", resource => resource.UseInProcess());
```

Each platform the resource runs on is a package the project references, such as
`Assimalign.Cohesion.ApplicationModel.Hosting.InProcess` for `UseInProcess()`. Started
without a platform in its environment, the application runs unmanaged and the call
registers nothing.

## Gateway SDK

`Assimalign.Cohesion.Sdk.Gateway` is a slim SDK on the base SDK. It defaults the project
to an executable and adds one implicit reference, the
`Assimalign.Cohesion.ApplicationModel.Gateway` package at the SDK's own version
(`CohesionGatewayImplicitReference=false` turns it off). It generates nothing and adds no
framework: the gateway manages resources through their platform without loading their
runtimes. Its `Program.cs` declares the model and runs it on the platform it references:

```csharp
using Assimalign.Cohesion.ApplicationModel.Gateway;
using Assimalign.Cohesion.ApplicationModel.Gateway.InProcess;

await using var infrastructure = new InProcessInfrastructure();
ApplicationGatewayBuilder builder = ApplicationGateway.CreateBuilder("orders").UseInProcess(infrastructure);
builder.AddResource("Web", "orders-api");
await builder.Build().RunAsync();
```

A platform is a pair of packages: the gateway half the gateway project references and
registers on its builder (`Assimalign.Cohesion.ApplicationModel.Gateway.InProcess`, or a
`cohesion-platforms` package such as `Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes`),
and the instance half each resource opts into. See the
[`Sdk.Gateway` overview](./Assimalign.Cohesion.Sdk.Gateway/Tasks/docs/OVERVIEW.md) and
[design](./Assimalign.Cohesion.Sdk.Gateway/Tasks/docs/DESIGN.md).

## Framework reference policy and the App hosting kernel

The base SDK includes no framework reference by default. It is a library SDK with
additional Cohesion build tooling: strongly typed settings, name-only references,
pin validation, language/AOT defaults, and the complete framework registration
table. A base-SDK executable opts into the hosting kernel explicitly:

```xml
<PropertyGroup>
    <OutputType>Exe</OutputType>
</PropertyGroup>
<ItemGroup>
    <FrameworkReference Include="Assimalign.Cohesion.App" />
</ItemGroup>
```

Each of the 18 resource-area SDKs defaults to executable output and implicitly
includes both `Assimalign.Cohesion.App` and `Assimalign.Cohesion.App.<Area>` under
the same `CohesionAutoIncludeAppFramework` switch. `Sdk.Gateway` adds no framework.

`Assimalign.Cohesion.App` is the hosting kernel, not the general library catalog.
Its single root list names Hosting; ApplicationModel.Hosting, the instance side every
area's `Hosting.ApplicationModel` integration builds on; the CommandLine, EnvironmentVariables,
FileSystem, and Json configuration providers; Logging.Console and Logging.EventSource;
OpenTelemetry; DependencyInjection; FileSystem.Physical; and Connections with its StreamAbort,
StreamLimits, and Tls siblings. Every area hosting module depends on Connections, so the shared kernel
carries it once. The
pack manifest is derived from their transitive Assimalign project-reference closure
plus the App umbrella assembly. Libraries outside that closure remain ordinary
NuGet packages and are referenced when used. The framework reference resolves — via the
`KnownFrameworkReference` registration
in [Targets/Assimalign.Cohesion.Sdk.FrameworkReference.props](./Assimalign.Cohesion.Sdk/Targets/Assimalign.Cohesion.Sdk.FrameworkReference.props) —
to two NuGet packages:

| Package | What it contains | When restored |
| --- | --- | --- |
| `Assimalign.Cohesion.App.Ref` | Reference assemblies (`ref/<tfm>/`) + `data/FrameworkList.xml` | Compile time |
| `Assimalign.Cohesion.App.Runtime.<rid>` | Implementation assemblies (`runtimes/<rid>/lib/<tfm>/`) + `data/RuntimeList.xml` | Publish time (when self-contained) |

This is the same targeting-pack/runtime-pack shape Microsoft uses for
`Microsoft.AspNetCore.App.Ref` / `.Runtime.<rid>` and gives the trimmer a
recognizable framework boundary.

### Opting out / pinning independently

```xml
<PropertyGroup>
    <!-- On a resource-area SDK, skip both implicit FrameworkReferences. The
         registrations stay, so explicit <FrameworkReference> items still work. -->
    <CohesionAutoIncludeAppFramework>false</CohesionAutoIncludeAppFramework>

    <!-- Pin the App framework to a version different from the SDK's. -->
    <CohesionAppFrameworkVersion>10.0.1</CohesionAppFrameworkVersion>
</PropertyGroup>
```

## Local development loop

The repo's `nuget.config` maps `Assimalign.Cohesion.*` packages to an in-tree
feed at `_out/packages/`. The full chain (SDK resolution → framework reference
→ targeting pack → runtime pack) is exercised end-to-end with a single command:

```powershell
pwsh scripts\Install-Local.ps1
```

What that runs:

1. `dotnet build` on the Cohesion build tasks (prerequisite for code generation).
2. `dotnet pack` each SDK project → `Assimalign.Cohesion.Sdk[.*].nupkg`.
3. `dotnet pack -p:RuntimeIdentifier=<rid>` on every framework's Runtime project
   once per requested RID → `Assimalign.Cohesion.App[.*].Runtime.<rid>.nupkg`.
4. `dotnet pack` every framework's Refs project (which depends on Runtime, so the
   ref assemblies are there to collect) → `Assimalign.Cohesion.App[.*].Ref.nupkg`.

Every package lands in `_out/packages/`. Any consumer csproj under the repo (or a
sibling repo whose `nuget.config` points back here) then resolves the whole chain —
SDK, targeting pack, runtime pack — against the local feed. No registration step,
no admin.

Iteration shortcuts:

- `Install-Local.ps1 -SkipSdks` — re-pack the framework only.
- `Install-Local.ps1 -SkipFramework` — re-pack the SDKs only.
- `Install-Local.ps1 -Rids 'win-x64','linux-x64','osx-arm64'` — cross-RID runtime packs.

The script prunes cached package extracts under `~/.nuget/packages/` for the
same versions before packing, so same-version repacks always pick up fresh
content. No need to manually clear NuGet caches between iterations.
