
# Cohesion Custom MSBuild Items

- [Cohesion Custom MSBuild Items](#cohesion-custom-msbuild-items)
  - [Items](#items)
    - [Reference Items](#reference-items)
      - [Projects](#projects)
      - [Packages](#packages)
    - [Code Generation](#code-generation)
      - [Value Types](#value-types)
  - [Related Docs](#related-docs)

---
## Items

This document describes Cohesion-specific MSBuild items that simplify common tasks in project files. Use these items directly in a `.csproj` to take advantage of centralized behavior managed by the Cohesion build.

<br/><br/>

### Reference Items

Custom items for managing dependencies within a project.
<br/>
#### Projects
>`CohesionProjectReference` lets you reference another project by its `.csproj` name. Paths are resolved within the repository, so if the referenced project moves, the reference continues to work without changes.
<br/>
>Example: `<CohesionProjectReference Include="{Project Name}"/>`
>```xml
><ItemGroup>
 > <CohesionProjectReference Include="Assimalign.Cohesion.Core" />
 > <!-- Add more by project name as needed -->
 > <!-- <CohesionProjectReference Include="Assimalign.Cohesion.Web" /> -->
 > <!-- <CohesionProjectReference Include="Assimalign.Cohesion.Database" /> -->
 > <!-- <CohesionProjectReference Include="Assimalign.Cohesion.Logging" /> -->
 > <!-- <CohesionProjectReference Include="Assimalign.Cohesion.Hosting" /> -->
>></ItemGroup>
>```

Notes:
- Intended for projects that live inside this repository.
- Use the exact project name (without `.csproj`).

#### Packages

`CohesionPackageReference` lets you add a package by name. Package versions and common metadata are centrally managed by the Cohesion build so individual projects do not repeat version numbers.

Versions are declared centrally in [`build/Targets/Build.References.Packages.targets`](../../build/Targets/Build.References.Packages.targets); see the build-system rules in [`.claude/rules/build-system.md`](../../.claude/rules/build-system.md) for details.

Example: `<CohesionPackageReference Include="{Package Name}"/>`

```xml
<ItemGroup>
  <CohesionPackageReference Include="Newtonsoft.Json" />
  <!-- Add more packages by name as needed -->
  <!-- <CohesionPackageReference Include="Serilog" /> -->
  <!-- <CohesionPackageReference Include="Polly" /> -->
  <!-- <CohesionPackageReference Include="FluentValidation" /> -->
</ItemGroup>
```

---

### Code Generation

Items that trigger Cohesion’s MSBuild-driven code generation.

#### Value Types

`CohesionCodeGenValueType` generates a lightweight value object type (e.g., a strongly-typed ID) based on the provided settings.

Example

```xml
<ItemGroup>
  <CohesionCodeGenValueType
      Include="ValueTypes\HostId.cs"
      ObjectType="Ulid"
      ObjectNamespace="Assimalign.Cohesion.Hosting"
      ObjectAccessModifier="public"
      IncludeImplicitOperators="true" />
</ItemGroup>
```

Attribute parameters

| Attribute | Required | Description |
| --- | --- | --- |
| `Include` | Yes | Relative path (within the project) of the file to generate for the value type. |
| `ObjectType` | Yes | The underlying type to wrap (e.g., `Ulid`, `Guid`, `string`, or a fully-qualified type). |
| `ObjectNamespace` | No | Namespace for the generated type. |
| `ObjectAccessModifier` | No | Access modifier for the generated type, typically `public` or `internal`. |
| `IncludeImplicitOperators` | No | `true`/`false`. When `true`, generates implicit conversion operators to and from the underlying type. |

---
## Related Docs

- Build: [Common MSBuild Properties](./MSBUILD_COMMON_PROPS.md)
- Build: [Common MSBuild Targets](./MSBUILD_COMMON_TARGETS.md)
