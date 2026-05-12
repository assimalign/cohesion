
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
 > <!-- <CohesionProjectReference Include="Assimalign.Cohesion.Messaging" /> -->
>></ItemGroup>
>```

Notes:
- Intended for projects that live inside this repository.
- Use the exact project name (without `.csproj`).

#### Packages

`CohesionPackageReference` lets you add a package by name. Package versions and common metadata are centrally managed by the Cohesion build so individual projects do not repeat version numbers.

See the Package Management Guidelines for details: [docs/guidelines/guidelines-package-management.md](docs/guidelines/guidelines-package-management.md)

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

- Build: [docs/build/msbuild-common-props.md](docs/build/msbuild-common-props.md)
- Build: [docs/build/msbuild-common-targets.md](docs/build/msbuild-common-targets.md)
- Guidelines: [docs/guidelines/guidelines-package-management.md](docs/guidelines/guidelines-package-management.md)
