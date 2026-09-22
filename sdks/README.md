## Cohesion SDKs

Cohesion ships a family of MSBuild SDKs: one per resource area and one for
application gateways. They chain through a common base
(`Assimalign.Cohesion.Sdk`), which itself chains through `Microsoft.NET.Sdk`.
Pick the SDK that matches what you're building:

| SDK | Use when… |
| --- | --- |
| `Assimalign.Cohesion.Sdk`          | Generic Cohesion app — services, hosts, libraries with no domain affinity. |
| `Assimalign.Cohesion.Sdk.Web`      | HTTP / web-surface application. |
| `Assimalign.Cohesion.Sdk.Database` | Database-resident application (migrations, seeded schemas, etc.). |
| `Assimalign.Cohesion.Sdk.Gateway`  | Application gateway generated from resource manifests and contributed providers. |

Every resource area under `resources/` has a matching SDK (`Sdk.ApiManager`,
`Sdk.ConfigurationStore`, `Sdk.EventHub`, …). `Sdk.Gateway` is deliberately not
a resource-area SDK: it is the NuGet-only orchestration composition root and has
no matching `Assimalign.Cohesion.App.Gateway` framework.

## Consumption

The base SDK can be pinned inline:

```xml
<Project Sdk="Assimalign.Cohesion.Sdk/10.0.0-preview.1">
</Project>
```

Add a `Program.cs` entry point. The base SDK and every layered SDK supply
`OutputType=Exe`, `TargetFramework=net10.0`, `LangVersion=Preview`,
`EnablePreviewFeatures=true`, `ImplicitUsings=disable`, `Nullable=enable`, and
`IsAotCompatible=true`. Base defaults are conditional on empty values; `-p:`
properties are honored, and later consumer `Directory.Build.props` or csproj
assignments override them. An ordinary `Directory.Build.props` carries identity
only. Library-style base-SDK consumers explicitly set `OutputType=Library`.
Resource executables do not multi-target, and a language-version override also
requires overriding `EnablePreviewFeatures`. Gateway retains unconditional
`IsAotCompatible=true` in its props and `OutputType=Exe` in its targets. See the
base SDK's [project defaults](./Assimalign.Cohesion.Sdk/Tasks/docs/DESIGN.md#project-defaults)
for import ordering and override constraints.

Layered Cohesion SDKs import the base SDK without an inline version. Pin both the
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

## Gateway SDK boundary

`Assimalign.Cohesion.Sdk.Gateway` always enables `CohesionApplicationModel` and
builds an executable Composite resource. Its MSBuild task reads referenced
`resource.json` documents and generates `Gateway.CreateBuilder(args)`, manifest
constants, same-application `Add*` verbs, boundary-crossing `Externals`, referenced
gateway `Applications`, and provider-driven `UseGateway(args)`. There is no
`AddAllResources()`: a gateway names what it composes, one verb per resource. The SDK
injects an area's ApplicationModel package (and, in process, its `App.<Area>` framework)
only for the areas of the resource projects the gateway references; any other area
package is the gateway's own explicit reference.

Providers are not discovered by reflection. Packages contribute
`CohesionGatewayProvider` items through `buildTransitive` props; the
semicolon-delimited `<CohesionGateways>` property selects which provider packages
are restored. Each item names its provider, gateway type, options type, and whether
the package requires JIT. Cohesion source therefore never names a platform type.

The orchestration plane is delivered through PackageReferences, never an
`App.Gateway` framework. In-process composition is the narrow exception that adds
the explicit resource-area frameworks needed by nested project references.

The current implementation remains guarded while external Docker/Kubernetes provider
contributions and a first-restore manifest dependency channel are incomplete. Web,
Database, and ConfigurationStore are the typed ApplicationModel mappings today.
The transitional SDK dependency set
must not be expanded to every area merely to hide the restore-order gap; see
[`Sdk.Gateway` design](./Assimalign.Cohesion.Sdk.Gateway/Tasks/docs/DESIGN.md) for the
required restore-visible producer contract and release gates.

## Implicit Cohesion.App framework reference

The base and resource-area Cohesion SDKs implicitly include
`<FrameworkReference Include="Assimalign.Cohesion.App" />`. `Sdk.Gateway` is the
exception: it suppresses that implicit reference to preserve its NuGet-only
orchestration boundary, and in-process composition adds required area frameworks
explicitly. The base framework reference resolves — via the
`KnownFrameworkReference` registration
in [Targets/Assimalign.Cohesion.Sdk.FrameworkReference.props](./Assimalign.Cohesion.Sdk/Targets/Assimalign.Cohesion.Sdk.FrameworkReference.props) —
to two NuGet packages:

| Package | What it contains | When restored |
| --- | --- | --- |
| `Assimalign.Cohesion.App.Ref` | Reference assemblies (`ref/<tfm>/`) + `data/FrameworkList.xml` | Compile time |
| `Assimalign.Cohesion.App.Runtime.<rid>` | Implementation assemblies (`runtimes/<rid>/lib/<tfm>/`) + `data/RuntimeList.xml` | Publish time (when self-contained) |

This is the same shape Microsoft uses for `Microsoft.AspNetCore.App.Ref` /
`.Runtime.<rid>`. It gives consumers a single one-line reference that pulls in
the whole Cohesion framework while keeping per-consumer disk footprint small
(no duplicating all framework DLLs into every consumer's `bin/`) and giving the
trimmer a recognizable framework boundary.

### Opting out / pinning independently

```xml
<PropertyGroup>
    <!-- Skip the implicit FrameworkReference. The KnownFrameworkReference
         registration stays, so an explicit <FrameworkReference> still works. -->
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
pwsh installer\scripts\Install-Local.ps1
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
