## Cohesion SDKs

Cohesion ships a family of MSBuild SDKs, one per application domain. They chain
through a common base (`Assimalign.Cohesion.Sdk`), which itself chains through
`Microsoft.NET.Sdk`. Pick the SDK that matches what you're building:

| SDK | Use when… |
| --- | --- |
| `Assimalign.Cohesion.Sdk`          | Generic Cohesion app — services, hosts, libraries with no domain affinity. |
| `Assimalign.Cohesion.Sdk.Web`      | HTTP / web-surface application. |
| `Assimalign.Cohesion.Sdk.Database` | Database-resident application (migrations, seeded schemas, etc.). |

Every resource domain under `resources/` has a matching SDK (`Sdk.ApiManager`,
`Sdk.ConfigurationStore`, `Sdk.EventHub`, …) — the three above are just the most
common entry points. The full set mirrors the folders under `sdks/`.

## Consumption

Pin the SDK version inline:

```xml
<Project Sdk="Assimalign.Cohesion.Sdk.Web/10.0.0">
    <PropertyGroup>
        <OutputType>Exe</OutputType>
        <TargetFramework>net10.0</TargetFramework>
    </PropertyGroup>
</Project>
```

…or factor the version out into `global.json`:

```json
{
    "msbuild-sdks": {
        "Assimalign.Cohesion.Sdk":          "10.0.0",
        "Assimalign.Cohesion.Sdk.Web":      "10.0.0",
        "Assimalign.Cohesion.Sdk.Database": "10.0.0"
    }
}
```

The SDK is resolved by NuGet's built-in MSBuild SDK resolver — the same machinery
that handles `Microsoft.NET.Sdk.Web`, `Microsoft.NET.Sdk.Worker`, etc. Works in
Visual Studio, Rider, the dotnet CLI, and any other MSBuild client with no
installer, no admin rights, and no custom resolver.

## Implicit Cohesion.App framework reference

All Cohesion SDKs implicitly include `<FrameworkReference Include="Assimalign.Cohesion.App" />`.
That single reference resolves — via the `KnownFrameworkReference` registration
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
