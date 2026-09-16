# Resource Areas (`resources/**`)

Rules that apply to every resource area. Each `resources/<Area>/` is an L3 service platform with
a matching `Sdk.<Area>` + `App.<Area>` framework family; these rules keep every area's dependency
architecture consistent as features are added. Area-specific rule sets (e.g. `web-area.md`)
layer on top of this file.

## The hosting-isolation rule (build-enforced, all areas)

Every resource area ships exactly one runtime module, `Assimalign.Cohesion.<Area>.Hosting` — the
composition root that integrates DI, configuration, logging, and transports.

> **COHRES001** — No library in an area may reference its exact hosting module except a
> named exemption holder. Roots and feature libraries may not reference the area's
> `<Area>.Hosting.<Suffix>` integrations. Hosting-family integrations may reference each
> other, but never the exact `<Area>.Hosting` runtime module. Exemptions name individual
> assemblies; an exemption for the runtime does not waive the rest of the hosting family.
>
> **COHRES002** — The hosting module may reference no library in its own area except the area
> root, `Assimalign.Cohesion.<Area>`. The shared framework (`App.<Area>`, via `Sdk.<Area>`)
> delivers the family to applications, so the runtime needs no compile-time knowledge of the
> features it hosts. Builder verbs ship with their feature package and compose against the area
> root's abstractions.
>
> **COHRES003** — No shipped project under `resources/**` may resolve an
> `Assimalign.Cohesion.ApplicationModel.Gateway*` assembly. Gateway orchestration belongs outside
> resource-area packages; there is no exemption property or opt-out.

> **COHRES004** — Area roots and feature libraries may reference no
> `Assimalign.Cohesion.Hosting` or `Assimalign.Cohesion.Hosting.*` library, directly or
> transitively. Only the area's hosting family (`<Area>.Hosting` and
> `<Area>.Hosting.<Suffix>`), `<Area>.Testing`, and `<Area>.ApplicationModel` may depend
> on those libraries.

ApplicationModel packages have an additional rollout guard:

> **COHAM001** — When a non-harness `resources/**` assembly whose name ends in
> `.ApplicationModel` sets `<CohesionApplicationModelGuard>true</CohesionApplicationModelGuard>`,
> its entire dependency closure is limited to `Assimalign.Cohesion.Core` (the Core assembly's
> actual name), `Assimalign.Cohesion.ApplicationModel`, `Assimalign.Cohesion.Hosting`,
> `Assimalign.Cohesion.Hosting.Health`, `Assimalign.Cohesion.Hosting.Resources`, BCL assemblies
> supplied by `Microsoft.NETCore.App`, and the `System.Security.Cryptography.ProtectedData` BCL
> facade used by Hosting.Resources' Windows-only mount carrier. The opt-in is a migration gate, not an
> architectural exemption: once enabled, a project cannot add to the allowlist locally.

The Hosting-area dependency graph behind that closure is fixed: Hosting.Health references Core
only; Hosting.Resources references Core, plain Hosting, Hosting.Health, and ProtectedData; plain
Hosting references neither sibling. An area ApplicationModel reaches the closure through its
direct Hosting.Resources reference.

Cross-references **between feature libraries in an area are fine** — the rule is
hosting-centric, not hub-and-spoke. References to anything outside the area (`Http.*`,
`Security.*`, `IdentityModel.*`, other areas' libraries) are likewise outside
COHRES001/002. When such a cross-area dependency is an implementation detail, the sanctioned
shape is the coordinated private pair from `build-system.md`:
`CohesionPrivateProjectReference` in the consuming library and
`CohesionFrameworkPrivateAssembly` in the owning `App.<Area>` runtime pack. This lets, for
example, `Database.Hosting` implement its admin control plane with `Web.Hosting`/`Web.Health`
without exposing Web types through the Database reference pack. Public cross-area contracts use
the ordinary `CohesionProjectReference`/`CohesionFrameworkAssembly` path instead.

**Enforcement** lives in `build/Targets/Build.Rules.targets` (imported for every project;
projects outside `resources/` are untouched). Violations fail the build:

- `COHRES001` is checked in two layers — the project-reference graph (every flavor:
  `CohesionProjectReference`, `CohesionPrivateProjectReference`, raw `ProjectReference`,
  transitive) and the resolved assembly closure after `ResolveAssemblyReferences` (which also
  catches `<Reference>`+`HintPath` and package-delivered DLLs). Exact-module and hosting-family
  candidates are checked separately, with exact assembly-name exemptions applied to each.
- `COHRES002` constrains the hosting module's **direct** references only: same-area assemblies
  legitimately arrive in its resolved closure transitively through the sanctioned area-root
  reference (e.g. `Assimalign.Cohesion.Database` aggregates its child roots — `Database.Types`/
  `Language`/`Storage`/`Transactions`/`Execution`/`Indexing`/`Protocol`/`Security`/`Governance` — so
  `Database.Hosting → Database` pulls them all in — that is the root's own composition, not a
  hosting violation).
- `COHAM001`, `COHRES003`, and `COHRES004` are checked in two layers: the direct/transitive
  project-reference graph, then the resolved assembly closure after `ResolveAssemblyReferences`.
  The latter also catches package-delivered and `<Reference>`+`HintPath` assemblies. Every error
  names the offending assembly or assemblies.
- `COHAM001` applies only to opted-in `.ApplicationModel` assemblies. All 18 resource
  `*.ApplicationModel` assemblies are guarded. `COHRES003` applies automatically to every shipped
  resource project and has no opt-in or exemption.
- `COHRES004` applies automatically outside the hosting family, the area's exact `Testing`
  package, and assemblies ending in `.ApplicationModel`. It rejects the base Hosting library
  and every `Hosting.*` sibling in both layers.
- Test (`tests/`), example (`examples/`), and sample (`samples/`) projects are automatically
  excluded from these guards — the rule constrains shipped libraries, not harnesses. This
  path-based exclusion applies to COHRES001–004 and COHAM001; everything else in an area is
  guarded regardless of folder layout. It is distinct from holding an explicit
  `CohesionHostingIsolationExemptions` waiver.

## Opting out — `CohesionHostingIsolationExemptions`

A project with a **sanctioned architectural exception** (deviation protocol in `deviations.md`,
user-approved) lists the exact assembly names it must be allowed to depend on,
semicolon-delimited, in its own csproj:

```xml
<PropertyGroup>
	<!-- Sanctioned exception to the resource hosting-isolation rule (COHRES001, ...): <why>. -->
	<CohesionHostingIsolationExemptions>Assimalign.Cohesion.Web.Hosting</CohesionHostingIsolationExemptions>
</PropertyGroup>
```

- The exemption is **per-assembly and per-project**: it waives `COHRES001` for a listed hosting
  assembly, or `COHRES002` for a listed same-area assembly, in the declaring project only.
- Setting the property is itself the deviation marker at the point of use — always pair it with
  a comment stating the rationale, and surface it in the change summary.
- **Exactly one standing exemption holder is permitted per area:**
  `Assimalign.Cohesion.<Area>.Testing`. The test factory drives the concrete runtime, which
  cannot be done through abstractions alone. Precedents:
  `resources/Web/Assimalign.Cohesion.Web.Testing` and
  `resources/Database/Assimalign.Cohesion.Database.Testing`. Any other project needs the
  deviation protocol case-by-case; there is no standing `.Application` exemption.
- The former standing exemption for `Assimalign.Cohesion.<Area>.ApplicationModel` is retired by
  the signed-off realization-plan design. An area ApplicationModel directly references
  `Assimalign.Cohesion.ApplicationModel` and `Assimalign.Cohesion.Hosting.Resources`, but never
  the plain host directly or its area's runtime module `Assimalign.Cohesion.<Area>.Hosting`;
  `COHAM001` enforces the complete resolved-assembly boundary as each project opts in.
- Do not use the property to route around design pressure: if a feature library "needs" hosting,
  the missing piece is almost always a seam on the area root (that is how the Web area moved its
  authentication builder verbs out of `Web.Hosting`).

## The resource instance is the SDK consumer's project

The framework-owned `Assimalign.Cohesion.<Area>.Application` name is retired. A resource
instance is the customer's ordinary executable project using `Assimalign.Cohesion.Sdk.<Area>`:
it contains a `Program.cs`, composes the area's application through its public builder, and runs
like any other .NET executable. Domain content also lives there — database schema, resource
policies, zones, user-flow pipelines, and equivalent area-owned declarations are application
code, not a generated entry point or a framework-owned apphost.

Orchestration is an opt-in build behavior on that executable:

- With `<CohesionApplicationModel>enabled</CohesionApplicationModel>`, the SDK emits
  `resource.json`, typed `Resource.g.cs` accessors over the ambient
  `Assimalign.Cohesion.Hosting.Resources.ResourceContext`, and
  `ResourceControlPlane.g.cs`, which registers the default control plane supplied by the area's
  `<Area>.ApplicationModel` package.
- With the default `disabled` value, the project is a plain application: no manifest, generated
  resource surface, control-plane registration, or orchestration diagnostics.
- `<Area>.ApplicationModel` remains the declarative orchestration plane (manifest-backed typed
  resource, planner, `Add<Area>(...)` verbs, and default-control-plane contract) and never
  references the area's runtime.

An SDK consumer is the composition root and may reference `<Area>.Hosting`; it is not a shipped
resource-area library governed as an exemption holder. In-repo executable acceptance fixtures
belong under an automatically excluded `samples/` path, are non-packable, and exercise their
real `Program.cs`. `<Area>.Testing` invokes that program under a test-scoped
`Assimalign.Cohesion.Hosting.Resources.ResourceRuntime.CreateScope(...)` and remains the area's
sole explicit exemption holder.

## What every area is expected to provide

- `Assimalign.Cohesion.<Area>` — the area root: the base abstractions and composition seams
  every feature library builds against. **The root does not absorb feature abstractions** — a
  feature's model, builder surface, and contracts live in the feature package and compose
  against the root's seams (precedent: the Web auth model and `AuthenticationBuilder` live in
  `Web.Authentication`, not in `Web`). A large area may compose its root from **child roots** —
  packages of generic base abstractions and default implementations pulled into the parent root
  for maintainability, testability, and separation of concerns (precedent:
  `Assimalign.Cohesion.Database` aggregates `Database.Types`/`Language`/`Storage`/
  `Transactions`/`Execution`/`Indexing`/`Protocol`/`Security`/`Governance`). **The dependency arrow always
  points root → child; a child root never references the root** — that is what keeps each child
  independently consumable, and it means a child owns its own vocabulary (value types, enums)
  and its own exception root (`StorageException`, `ProtocolException` inherit `Exception`, not
  the area exception root — the layer that owns both vocabularies translates at its boundary).
  Child-to-child references are fine. The breakdown signal for either shape is the root (or a
  child root) pulling in anything feature- or model-specific.
- **The hosting integration family:** `Assimalign.Cohesion.<Area>.Hosting.<Suffix>`
  integrates `Assimalign.Cohesion.Hosting.<Suffix>`; precedents are
  `Web.Hosting.Resources` and `Web.Hosting.Health`. These libraries may reference the
  base Hosting library and siblings, the area root, features, other hosting-family
  integrations, and other areas' packages. They may never reference their own exact
  `<Area>.Hosting` module; roots and features may not reference them.
- **The application builder seam:** the area root provides `I<Area>ApplicationBuilder` (and the
  `I<Area>Application` it builds); the hosting module implements them and exposes the creation
  entry point (`<Area>Application.CreateBuilder(string[] args)` — every resource is a `Program.cs` executable;
  when the project opts into orchestration (`CohesionApplicationModel=enabled`) the builder honors
  the ambient `Assimalign.Cohesion.Hosting.Resources.ResourceContext` and the registered default
  control plane, otherwise it behaves as a plain application — see
  `docs/DEVELOPER_EXPERIENCE_DESIGN.md` §2/§4.2). Feature/model registration verbs ship with
  their feature package as `extension(I<Area>ApplicationBuilder)` members and compose against
  the root builder — never against the hosting module — so a feature or model registers itself
  on any composition surface without knowing the hosting layer. Registration stays
  dependency-free (values and options objects; no container, no configuration binding).
  Precedents: `IWebApplicationBuilder` (Web root) + `WebApplication.CreateBuilder(args)`
  (`Web.Hosting`) + `AddAuthentication` (`Web.Authentication`); `IDatabaseApplicationBuilder`
  (Database root) + `DatabaseApplication.CreateBuilder(args)` (`Database.Hosting`) +
  `AddSqlDatabase` (`Database.Sql`). The root application exposes `Context`, `StartAsync`,
  and `StopAsync`; its builder exposes area verbs and `Build()`. Background-work
  registration (`AddService`) is a concrete-builder verb in `<Area>.Hosting`, absent
  from the root contract; no area-owned service abstraction is introduced. This pattern
  is expected to be the same in every area (O34).
  In every area, `CreateBuilder` returns the public concrete builder, and its `Build()`
  returns the public concrete `Host<TContext>` application (Web, Database, and all 16 fillers).
- `Assimalign.Cohesion.<Area>.Hosting` — the runtime module, referencing only the area root and
  non-area infrastructure. Roots and feature libraries reference no
  `Assimalign.Cohesion.Hosting*` library. Enabled-resource implementations consume the plain lifecycle host,
  `Assimalign.Cohesion.Hosting.Resources`, and `Assimalign.Cohesion.Hosting.Health` without
  referencing their area's ApplicationModel package. **If the hosting module ever appears to need a same-area dependency
  beyond the root, that is an architecture revisit — surface it to the user — not a case for
  the exemption property or for pushing the dependency's types into the root.**
- `Assimalign.Cohesion.<Area>.ApplicationModel` — the AOT-compatible, dependency-guarded declarative plane: a
  manifest-backed typed resource, platform-neutral planner, `Add<Area>(...)` graph verbs, and the
  area's default-control-plane contract/factory. Its direct Cohesion references are
  `Assimalign.Cohesion.ApplicationModel` and `Assimalign.Cohesion.Hosting.Resources`. It never
  references `<Area>.Hosting`; generated code in an enabled consumer executable registers the two
  sides through `Assimalign.Cohesion.Hosting.Resources.ResourceRuntime`.
- `Assimalign.Cohesion.<Area>.Testing` — the optional shippable test factory that invokes a
  consumer's real `Program.cs` under a test-scoped ambient resource context. When present, this
  is the area's sole explicit `CohesionHostingIsolationExemptions` holder.
- `Assimalign.Cohesion.<Area>.<Feature>` — feature libraries; builder verbs ship here, not in
  hosting.
- Framework delivery: shippable area assemblies (and their outside-area transitive closure)
  belong in the `App.<Area>` ItemGroup of `frameworks/Assimalign.Cohesion.App.props`, so
  applications get the family through the SDK without project wiring. Validate with
  `dotnet pack frameworks/Assimalign.Cohesion.App.<Area>.Runtime/src/...csproj`.
  `<Area>.ApplicationModel` and `<Area>.Client` packages are NuGet-only, injected by
  `Sdk.<Area>` / `Sdk.Gateway`, and never members of an `App.<Area>` shared framework
  (owner-signed developer-experience design O2/O27).

Relaxing the rule itself (beyond a per-project exemption) is an architectural decision: change
`build/Targets/Build.Rules.targets`, this file, and the owning area's README in the same commit,
with the user's explicit confirmation.
