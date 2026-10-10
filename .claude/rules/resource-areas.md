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
> **COHRES002** — The exact hosting module may reference any library in its own area **except**:
> `<Area>.Testing`, which references the hosting module, so the reverse reference is a cycle;
> `<Area>.ApplicationModel`, which the realization-plan design keeps the runtime off (generated
> code in the consumer executable joins the declarative plane to the runtime; COHAM001 bounds the
> other direction); `<Area>.ApplicationModel.Orchestration`, the gateway-side provider package
> that R8 (owner decisions of 2026-09-25) makes opt-in, NuGet-only, and never an `App.<Area>`
> member, so a runtime reference would force it into every framework that carries the module
> (the Orchestration rules below bound the other direction); the framework producers
> `<Area>.Refs` and `<Area>.Runtime`, packaging shells that themselves reference the hosting
> module; and test, example, sample, and fixture projects, which are harnesses. The two
> ApplicationModel exclusions hold by any route: the module's resolved closure may carry neither.
> Owner decision 2026-10-09 ("allow the Hosting project tobe [sic] able reference all `Web.*`
> projects, or more generically all the `<Area>.*` projects"; the exclusions follow from earlier
> decisions); until then the module could reference only the area root,
> `Assimalign.Cohesion.<Area>`, and its own hosting family (`<Area>.Hosting.<Suffix>`). The
> permission is not a reason to reference features: each reference lands its closure in every
> framework that carries the module (the `<Area>.Hosting` bullet under "What every area is
> expected to provide").
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

The opposite direction has its own guard, because resources consume libraries and libraries never
depend on resources:

> **COHLIB001** — No non-harness project under `libraries/**` or `sdks/**` may reference a
> `resources/**` project, whether by project reference or by a resolved assembly. The one
> exception is the same-area SDK: a project under `sdks/Assimalign.Cohesion.Sdk.<Area>/` may
> reference `resources/<Area>/**` (precedent: `Sdk.Database.Tasks → Database.Sql.Schema`).

When a library needs behavior an area owns, the library defines the seam and the area implements
it: the gateway's store, certificate-authority, trust-store, command-input, telemetry, and
credential seams live in `Assimalign.Cohesion.ApplicationModel`, and the shipped implementations
are the opt-in `<Area>.ApplicationModel.Orchestration` packages below. The guard's two layers,
harness exemptions, and scope computation are in `build-system.md` ("Library and SDK boundary
guard (COHLIB001)"); relaxing it is an architectural decision on the same terms as this file's
rules (owner decision 5 of 2026-09-25, recorded as R8 in `docs/DEVELOPER_EXPERIENCE_DESIGN.md`).

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

**Enforcement** lives in `build/Targets/Build.Rules.targets` (imported for every project; the
COHRES and COHAM guards leave projects outside `resources/` untouched, and COHLIB001 is the only
boundary guard that applies to `libraries/` and `sdks/`; the repo-wide COHNS001 `RootNamespace` check
in the same file applies everywhere, see `general-rules.md`). Violations fail the build:

- `COHRES001` is checked in two layers — the project-reference graph (every flavor:
  `CohesionProjectReference`, `CohesionPrivateProjectReference`, raw `ProjectReference`,
  transitive) and the resolved assembly closure after `ResolveAssemblyReferences` (which also
  catches `<Reference>`+`HintPath` and package-delivered DLLs). Exact-module and hosting-family
  candidates are checked separately, with exact assembly-name exemptions applied to each.
- `COHRES002` rejects only its excluded categories, in two layers. The first checks the hosting
  module's **direct** references (the evaluation-time snapshot, taken before NuGet adds transitive
  project references) against every category. A reference is same-area when its name carries the
  `Assimalign.Cohesion.<Area>.` prefix or its project lives under `resources/<Area>/`. `Testing`,
  `ApplicationModel`, `ApplicationModel.Orchestration`, `Refs`, and `Runtime` are matched by exact
  name; a harness is a referenced project with a `tests/`, `examples/`, `samples/`, or `fixtures/`
  segment in its path below the repository root. The second checks the module's resolved assembly
  closure after `ResolveAssemblyReferences` for `<Area>.ApplicationModel` and
  `<Area>.ApplicationModel.Orchestration` only, because no rule on an intermediate library stops
  either: an Orchestration package resolves no `Hosting*` assembly, so COHRES004 lets a root or
  feature the module references take it, and COHRES004 exempts the hosting family, so a
  `<Area>.Hosting.<Suffix>` integration could take either package. The other categories are
  checked on direct references only: a route to `Testing` or a producer is a cycle, and a route to
  a harness through another library is not checked.
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
- Test (`tests/`), example (`examples/`), sample (`samples/`), and fixture (`fixtures/`) projects
  are automatically excluded from these guards — the rule constrains shipped libraries, not
  harnesses. This path-based exclusion applies to COHRES001–004 and COHAM001; everything else in an
  area is guarded regardless of folder layout. It is distinct from holding an explicit
  `CohesionHostingIsolationExemptions` waiver.
- The area's two **framework producers**, `Assimalign.Cohesion.<Area>.Refs` and
  `Assimalign.Cohesion.<Area>.Runtime`, are packaging shells, not libraries: the Runtime producer
  references the whole `App.<Area>` framework, hosting module and `Hosting.*` closure included.
  COHRES001, COHRES002, and COHRES004 skip them by **exact identity**, never by path: the
  conventional project name for this area, `CohesionFrameworkName=Assimalign.Cohesion.App.<Area>`,
  and the matching `CohesionFrameworkKind` (`Ref`/`Runtime`). A project matching only part of that
  identity is guarded like any library, so the property cannot be used as an opt-out. COHRES003
  still applies to both producers. Owner decision, 2026-09-22; naming convention and details in
  `build-system.md` ("Framework producer projects").

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
  assembly, or `COHRES002` for a listed same-area assembly, in the declaring project only. Since
  2026-10-09 COHRES002 rejects only its excluded categories, so a COHRES002 exemption can only name
  a `Testing`, ApplicationModel, framework-producer, or harness project — every one of which
  signals a design problem rather than a missing permission.
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
  the missing piece is almost always a seam on the area root or a component integration (that is
  how the Web area moved its authentication builder verbs out of `Web.Hosting`; since owner
  decision 34 of 2026-10-09 they ship as the `builder.Services.AddAuthentication(...)` component
  integration).

## The resource instance is the SDK consumer's project

The framework-owned `Assimalign.Cohesion.<Area>.Application` name is retired. A resource
instance is the customer's ordinary executable project using `Assimalign.Cohesion.Sdk.<Area>`:
it contains a `Program.cs`, composes the area's application through its public builder, and runs
like any other .NET executable. Domain content also lives there — database schema, resource
policies, zones, user-flow pipelines, and equivalent area-owned declarations are application
code, not a generated entry point or a framework-owned apphost.

Orchestration is an opt-in build behavior on that executable:

- With `<CohesionApplicationModel>enabled</CohesionApplicationModel>`, the area SDK
  imports `Assimalign.Cohesion.Sdk.ApplicationModel`; that SDK emits `resource.json`,
  typed `Resource.g.cs` accessors over the ambient
  `Assimalign.Cohesion.Hosting.Resources.ResourceContext`, and
  `ResourceControlPlane.g.cs`, which registers the default control plane supplied by the area's
  `<Area>.ApplicationModel` package.
- With the default `disabled` value, the project is a plain application: no manifest, generated
  resource surface, control-plane registration, or orchestration diagnostics.
- `<Area>.ApplicationModel` remains the declarative orchestration plane (manifest-backed typed
  resource, planner, `Add<Area>(...)` verbs, and default-control-plane contract) and never
  references the area's runtime.

An SDK consumer is the composition root and may reference `<Area>.Hosting`; it is not a shipped
resource-area library governed as an exemption holder. `<Area>.Testing` invokes that program under
a test-scoped `Assimalign.Cohesion.Hosting.Resources.ResourceRuntime.CreateScope(...)` and remains
the area's sole explicit exemption holder.

### Executable acceptance fixtures live in `fixtures/`, beside the project that drives them

An in-repo executable acceptance fixture — a real `Program.cs` built from source that a test
project launches — belongs in a **`fixtures/` folder inside the owning project**, a sibling of
`src/`, `tests/`, and `docs/`, one folder per fixture named for its project:

```
resources/Database/Assimalign.Cohesion.Database.Testing/
├── src/
├── tests/
├── docs/
└── fixtures/
    └── Assimalign.Cohesion.Database.SampleHost/
```

The owner is whichever project's tests the fixture exists for, and the name says which: the Web
area's `Assimalign.Cohesion.Web.Testing.TestHost` lives under `Web.Testing`, and the Gateway's two
resource fixtures live under `ApplicationModel.Gateway`, not in the areas whose runtimes they
happen to compose.

- **Fixtures are not samples.** A sample demonstrates the product to a reader; a fixture is a test
  input that must compile from source against the current tree. Consumer-facing examples and
  package-only smoke consumers live in the separate `cohesion-examples` repository; clone it when
  it is missing rather than recreating its content here (`workflow.md`, *Companion repositories*).
- **Non-packable, always.** Set `IsPackable=false` in the fixture's csproj.
- **The guards exempt `fixtures/` by path**, on the same terms as `tests/`: a fixture composes the
  very `<Area>.Hosting` runtime module the area's own libraries may not touch, which is the whole
  point of it. The exclusion is in `build/Targets/Build.Rules.targets` alongside
  `tests|examples|samples`, and it applies to COHRES001–004 and COHAM001.
- **No wiring needed.** `fixtures/` sits under `libraries/` or `resources/`, both of which
  `build/Targets/Build.References.Projects.targets` already indexes, so a test project resolves the
  fixture by name through `CohesionProjectReference` — and both trees' `Directory.Build.props`
  already supply the TFM.
- **A relative path inside a fixture changes depth when it moves.** The Database fixture's
  `NuGet.Config` points at `_out/packages` relatively and
  `installer/scripts/modules/tests/CohesionReleasePolicy.Tests.ps1` reads that file by path; check
  both when relocating one.
- **The owning area's CI workflow carries the fixture path** in its `paths:` trigger filter, so a
  change to the fixture still builds the area that depends on it.

## Hosting composition — DI is the dependency control

The area root and its feature libraries stay DI-free (COHRES004, `component-integration.md`); they
meet in `<Area>.Hosting`, and inside that module the container is the **only** composition
registry. Not every area has `Add(...)`/`Use(...)` feature verbs, but every hosting module composes
this way. Web is the reference implementation (`resources/Web/Assimalign.Cohesion.Web.Hosting/docs/DESIGN.md`,
"Application lifecycle composition"); the filler template is EmailHub.

- **The concrete builder owns one `ServiceProviderBuilder Services`**, created with
  `EnableDynamicCode = false`, `ValidateOnBuild = true`, `ValidateScopes = true`. The module
  registers factories and instances only, never implementation types, so resolution stays
  reflection-free. The hosting module takes a public `CohesionProjectReference` to
  `Assimalign.Cohesion.DependencyInjection` (the App kernel already carries it; no framework
  member-list change).
- **Every composition input is a registration; there is no parallel private registry.**
  `AddService(IHostService)` is `Services.AddSingleton<IHostService>(service)`;
  `AddService(Func<<Area>ApplicationContext, IHostService>)` is a factory registration closed over
  the context. Health checks register `IHealthContributor`, control-plane command handlers
  register `IResourceCommandHandler` (both handed to the control plane once, at `Build`), the
  module's own endpoints and data-plane services register `IHostService`, and collaborators they
  share (repositories, stores, key providers) are singletons their factories resolve. DI holds the
  dependency graph, not option values: single-valued configuration of one component (listener
  options, a concurrency cap, Web's `AddPipeline` replacement) stays builder state.
- **Root verbs are explicit-interface shims.** Each `I<Area>ApplicationBuilder` member is
  implemented explicitly and does one thing: translate the root's dependency-free shape into a
  singleton registration. A value becomes an instance registration; a
  `Func<I<Area>ApplicationContext, T>` becomes a factory registration invoked once with the area
  context. Factories receive the context, **never `IServiceProvider`** — that is what keeps the
  container out of the libraries that call these verbs. A public concrete counterpart that returns
  the concrete builder (for chaining with `AddService`) is allowed; the explicit member forwards to
  it. Registration-time duplicate checks read `Services.Container`, never a second collection.
  Precedents: `IWebApplicationBuilder.AddFeature/AddServer` → `IHttpFeature`/`IWebApplicationServer`;
  `ISchedulerApplicationBuilder.AddJob/AddScheduleProvider` → `IScheduleJob`/`IScheduleProvider`.
  `AddFeature` is Web's raw feature path; the Web feature packages' own registration verbs are
  component integrations that register the same `IHttpFeature` singleton directly on `Services`
  (owner decision 34, 2026-10-09).
- **Validate what the container cannot.** `ValidateOnBuild` cannot see the lifetime a factory or
  instance registration implies for a consumer, so a module whose aggregate has a lifetime
  contract checks the descriptors itself in `Build`, before `MakeReadOnly`, and names the
  offending registration. Precedent (owner decision 35, 2026-10-09): `WebApplicationBuilder.Build`
  rejects a scoped or transient `IHttpFeature` registration, a registration under a contract
  derived from `IHttpFeature`, and a disposable `IHttpFeature` instance or implementation type;
  the pipeline build, which resolves the aggregate, rejects a disposable feature a factory
  registration produced.
- **The service type is the lifecycle phase; registration order is the order within a phase.**
  A single-phase area registers everything as `IHostService` and places its own services by
  *when* they register: telemetry in the builder constructor (first), hard-wired endpoints in
  `Build` (last). An area with more than one phase gives each phase its own service type — Web runs
  `IHostService` (application services) before `IWebApplicationServer` (servers) — so no
  registration-order hack is needed across phases.
- **Resolve once, at the composition boundary.** `Build` makes the container read-only
  (`ServiceContainer.MakeReadOnly()`; a later registration throws instead of silently missing the
  provider), creates the provider exactly once, validates and resolves every aggregate it consumes
  into arrays, and throws on a second call. An aggregate consumed later resolves once at its own
  boundary (Web: features at pipeline build, servers at host start). Nothing resolves per request
  or per operation, and no `IServiceProvider` reaches root or feature code.
- **The application owns the provider.** `DisposeAsync` stops the host, then disposes the provider:
  factory-created services are owned and disposed in reverse creation order; instance
  registrations are borrowed and left to their callers. A failed `Build` disposes the provider
  before rethrowing. The concrete context exposes the provider as `ServiceProvider` for
  hosting-layer factories. Known limit: the provider's disposal is fail-fast — the first
  `Dispose` that throws ends the pass and later services stay undisposed.
- **Not yet migrated: `Database.Hosting`.** Its engine and service registries keep explicit
  ownership that is stronger than the provider's (engine factories observe preceding engines;
  build rollback and disposal continue past a failure and aggregate it, which its tests pin).
  Moving them into the container needs the provider to continue-and-aggregate first. Its
  `Services` registry (reflection-free options, closed registrations) and its ownership of the
  built provider already follow this section; its explicit `IDatabaseApplicationBuilder.AddEngine`
  shims still forward to the private engine registry rather than to `Services`.

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
  `<Area>.Hosting` module; roots and features may not reference them. The exact
  `<Area>.Hosting` module may consume them; they may never reference it (COHRES001).
- **The application builder seam:** the area root provides `I<Area>ApplicationBuilder` (and the
  `I<Area>Application` it builds); the hosting module implements them and exposes the creation
  entry point (`<Area>Application.CreateBuilder(string[] args)` — every resource is a `Program.cs` executable;
  when the project opts into orchestration (`CohesionApplicationModel=enabled`) the builder honors
  the ambient `Assimalign.Cohesion.Hosting.Resources.ResourceContext` and the registered default
  control plane, otherwise it behaves as a plain application — see
  `docs/DEVELOPER_EXPERIENCE_DESIGN.md` §2/§4.2). Feature/model registration verbs ship with
  their feature package — never in the hosting module — so a feature or model registers itself
  without knowing the hosting layer. They take one of two forms:
  `extension(I<Area>ApplicationBuilder)` members that compose against the root builder, or
  component integrations (`component-integration.md`) that the generator projects onto the
  hosting builder's `Services` (`IServiceProviderBuilder`), as other .NET hosting models register
  features. Either way the package stays dependency-free: it composes values and options objects
  and references no container and no configuration binding. Precedents:
  `IWebApplicationBuilder` (Web root) + `WebApplication.CreateBuilder(args)` (`Web.Hosting`) +
  `builder.Services.AddAuthentication(auth => auth.AddCookie(...))` (`Web.Authentication`, a
  component integration; owner decisions 34 and 35 of 2026-10-09 moved the eight Web feature
  registration verbs there and made every `IHttpFeature` registration a singleton that
  `Web.Hosting` enforces at `Build`; `IWebApplicationBuilder.AddFeature` stays the raw path);
  `IDatabaseApplicationBuilder` (Database root) + `DatabaseApplication.CreateBuilder(args)`
  (`Database.Hosting`) + `AddSql` (`Database.Sql`, an `extension(IDatabaseApplicationBuilder)`
  member with nested engine server factories). The root application exposes `Context`, `StartAsync`,
  and `StopAsync`; its builder exposes area verbs and `Build()`. Background-work
  registration (`AddService`) is a concrete-builder verb in `<Area>.Hosting`, absent
  from the root contract; no area-owned service abstraction is introduced. This pattern
  is expected to be the same in every area (O34).
  In every area, `CreateBuilder` returns the public concrete builder, and its `Build()`
  returns the public concrete `Host<TContext>` application (Web, Database, and all 16 fillers).
- `Assimalign.Cohesion.<Area>.Hosting` — the runtime module, referencing the area root, its own hosting
  family, the same-area libraries the runtime itself needs, and non-area infrastructure. Roots and
  feature libraries reference no `Assimalign.Cohesion.Hosting*` library. Enabled-resource
  implementations consume the plain lifecycle host, `Assimalign.Cohesion.Hosting.Resources`, and
  `Assimalign.Cohesion.Hosting.Health` without referencing their area's ApplicationModel package.
  **The module may reference any library in its own area except COHRES002's exclusions** (owner
  decision 2026-10-09), so runtime machinery that belongs in its own package — a server, a router
  the host drives — no longer has to be pushed into the root or routed through an exemption.
  **Reference only what the runtime needs:** each library the module references lands, with its
  closure, in every framework that carries the module — its own `App.<Area>`, plus every area
  framework that carries it privately (`Web.Hosting` is a private member of all 17 other area
  frameworks, so one new `Web.Hosting` reference is a framework-membership change in each of them).
  Feature registration never needs the module to reference the feature: the feature's verbs ship
  with the feature, as root-builder extensions or as component integrations on `Services` (Web,
  owner decision 34). It composes through its container ("Hosting composition — DI is the dependency
  control", above).
- `Assimalign.Cohesion.<Area>.ApplicationModel` — the AOT-compatible, dependency-guarded declarative plane: a
  manifest-backed typed resource, platform-neutral planner, `Add<Area>(...)` graph verbs, and the
  area's default-control-plane contract/factory. Its direct Cohesion references are
  `Assimalign.Cohesion.ApplicationModel` and `Assimalign.Cohesion.Hosting.Resources`. It never
  references `<Area>.Hosting`; generated code in an enabled consumer executable registers the two
  sides through `Assimalign.Cohesion.Hosting.Resources.ResourceRuntime`.
  **Every ApplicationModel package declares the `Assimalign.Cohesion.ApplicationModel` namespace**
  (`RootNamespace` and every `namespace` directive; `COHAM002` checks the project property), never
  `Assimalign.Cohesion.<Area>.ApplicationModel`: an apphost's `Program.cs` composes every area with
  one `using`, the way the base library's own verbs work. The package and assembly names keep the
  area segment, so types are area-prefixed (`WebResourceOptions`, `IDatabaseResourceDescriptor`,
  `AddIdentityHub`) to stay unambiguous inside the shared namespace. The same rule binds
  companion-repository ApplicationModel packages such as `Assimalign.Cohesion.Viu.ApplicationModel`.
  `Sdk.Gateway` injects an area's ApplicationModel only for the areas of the resource projects a
  gateway names in `CohesionResourceReference` (read from each project's `Sdk="Assimalign.Cohesion.Sdk.<Area>"`
  attribute, or the reference's `Area` metadata), and in-process gateways get exactly those areas'
  `App.<Area>` frameworks; any other area package is the gateway's own explicit reference.
  `Sdk.Gateway` generates no per-resource verb: the gateway's `Program.cs` calls the area's
  hand-written verb over the generated manifest,
  `builder.AddWeb(Manifests.AppAApi, new WebResourceOptions { ... })`, and adds a kind without an
  ApplicationModel package with `builder.AddResource(Manifests.<Name>)`. The `Add<Area>` verb is
  therefore the area's public composition API, not an SDK implementation detail.
- `Assimalign.Cohesion.<Area>.ApplicationModel.Orchestration` — **optional**: the gateway-side
  provider package. It implements the gateway provider seams that `Assimalign.Cohesion.ApplicationModel`
  defines (`IResourceSourceProvider`, `IResourceCertificateAuthority`, `ITrustedIssuerStore`,
  `IResourceCommandInputResolver`, and the other roles on `ApplicationProviders`), so the area's
  wire knowledge (paths, payload schemas, command kinds) lives here and not in
  `libraries/ApplicationModel/**`. It ships explicit `Use<Area>(...)` registration verbs in two
  forms that make the same registrations, each in its receiver's `Providers`: an
  `extension(IApplicationBuilder)` member taking the
  store's resource descriptor, for an application built in code, and an
  `extension(IApplicationProviderBuilder)` member taking the store's `ResourceName`, which an
  application set uses for a member whose model it imports
  (`set.AddApplication(Applications.X, member => member.Use<Area>("<store>"))`).
  `IApplicationBuilder` does not extend `IApplicationProviderBuilder`: they are separate
  interfaces and the default builder implements both, so a single application registers through
  the descriptor form and the `ResourceName` form is the application-set member surface (owner
  decision O45 of 2026-09-27, R10 in `docs/DEVELOPER_EXPERIENCE_DESIGN.md`). A new area's
  Orchestration package ships both forms. Precedents:
  `SecretStore.ApplicationModel.Orchestration` (`UseSecretStore(store)`, then
  `.AsCertificateAuthority()` / `.AsTrustStore()`) and
  `ConfigurationStore.ApplicationModel.Orchestration` (`UseConfigurationStore(store)`). A verb
  registers only for its own application; bindings to a store of another application are
  rejected at `Build()`, or for an application-set member when the set starts (owner decision 4
  of 2026-09-25; member validation accepted in O45).
  - **Name and verb are load-bearing.** When a mount source `<source>:<key>` in the application's
    own manifests names a store resource that has no registered provider, `Build()` (for an
    application-set member, the set's start) throws an error built from that store's manifest
    Kind: reference `Assimalign.Cohesion.<Kind>.ApplicationModel.Orchestration` and call
    `builder.Use<Kind>(...)`, or for a member
    `set.AddApplication(Applications.<Member>, application => application.Use<Kind>("<store>"))`.
    The same package-and-verb hint, built from the target's manifest Kind, is produced when the
    application declares a command whose target manifest marks its kind `requiresInputResolver`
    and registers no `IResourceCommandInputResolver` for that kind (owner decision of 2026-09-28,
    R11 in `docs/DEVELOPER_EXPERIENCE_DESIGN.md`; for a target of another application the error
    asks for the resolver in `Providers.CommandInputs` instead). So an area whose command payloads
    carry source expressions (`parameter:<name>`, `<store>:<key>`) that only the delivering gateway
    can resolve marks those kinds `RequiresInputResolver="true"` on their `CohesionCommand` items in
    `sdks/Assimalign.Cohesion.Sdk.<Area>/Targets/Sdk.<Area>.props`, and its Orchestration package's
    `Use<Area>(...)` registers the resolver (precedent: `secretstore.add-secret` in
    `Sdk.SecretStore.props`, resolved by `UseSecretStore`). An unmarked kind with no resolver is
    delivered as declared; the SecretStore runtime still rejects an unresolved `add-secret` as
    defense in depth.
    A package or verb named any other way sends the developer to a package that does not exist.
  - **References:** `Assimalign.Cohesion.ApplicationModel`, the area's own `<Area>.Client`, and
    other `libraries/**` packages outside the Hosting and Gateway families. It never references
    `Assimalign.Cohesion.Hosting*`, `Assimalign.Cohesion.ApplicationModel.Gateway*`, or
    `<Area>.ApplicationModel`. The existing guards enforce all three. The assembly name ends in
    `.Orchestration`, not `.ApplicationModel`, so COHRES004 classifies the package as a feature
    library: it rejects Hosting directly, and it rejects `<Area>.ApplicationModel` transitively
    through that package's direct `Hosting.Resources` reference. COHRES003 rejects the Gateway
    family and COHRES001 rejects the runtime module; COHAM001 does not apply. Between the two
    packages the arrow points Orchestration → Client, never back: `<Area>.Client` takes no
    ApplicationModel reference, which keeps the client eligible for a shared framework (O2).
  - **Delivery:** NuGet-only. It is never a member of an `App.<Area>` shared framework, and no
    SDK injects it (neither `Sdk.<Area>` nor `Sdk.Gateway`). The gateway project references the
    package and calls its verb in `Program.cs`; nothing registers by convention.
  - **Namespace:** it declares the shared `Assimalign.Cohesion.ApplicationModel` namespace
    (`RootNamespace` pinned in the csproj) with area-prefixed type names
    (`SecretStoreSourceProvider`, `ConfigurationStoreSourceProvider`), so the gateway's one
    `using` composes it with the ApplicationModel verbs. COHAM002 covers assembly names ending
    in `.ApplicationModel.Orchestration` as well as `.ApplicationModel`, so the build rejects any
    other `RootNamespace` (`build-system.md`, COHAM002).
- `Assimalign.Cohesion.<Area>.Testing` — the optional shippable test factory that invokes a
  consumer's real `Program.cs` under a test-scoped ambient resource context. When present, this
  is the area's sole explicit `CohesionHostingIsolationExemptions` holder.
- `Assimalign.Cohesion.<Area>.<Feature>` — feature libraries; builder verbs ship here, not in
  hosting (registration verbs may be component integrations on `Services`, as Web's are).
- `Assimalign.Cohesion.<Area>.Refs` and `Assimalign.Cohesion.<Area>.Runtime` — the producers of
  the area's `App.<Area>` shared framework, named for the area while their assembly and package
  names keep the `App` segment (`Assimalign.Cohesion.App.<Area>.Refs` / `Assimalign.Cohesion.App.<Area>`,
  packages `Assimalign.Cohesion.App.<Area>.Ref` / `.Runtime.<rid>`). They compile nothing and are
  exempt from COHRES001/002/004 by exact identity (above), and COHRES002 rejects a hosting-module
  reference to either. Both names are reserved in every area;
  the naming convention is `build-system.md`, "Framework producer projects". The Runtime
  producer's folder holds the framework's hand-curated member list in its `Directory.Build.props`,
  which the Refs producer's own `Directory.Build.props` imports (`build-system.md`, "Framework
  membership").
- Framework delivery: `Sdk.<Area>` defaults to executable output and includes both the App
  hosting kernel and `App.<Area>` under `CohesionAutoIncludeAppFramework`. Shippable area
  assemblies (and their outside-area transitive closure that App does not carry) belong in the
  `App.<Area>` public/private items of
  `resources/<Area>/Assimalign.Cohesion.<Area>.Runtime/Directory.Build.props`, so applications
  get the family without project wiring. The framework closure tests guard every
  area, including feature dependencies such as Web.Caching → Caching/InMemory. Validate with
  `dotnet pack resources/<Area>/Assimalign.Cohesion.<Area>.Runtime/src/Assimalign.Cohesion.<Area>.Runtime.csproj`.
  App carries `Assimalign.Cohesion.Connections` because every generated resource accessor exposes
  its `ConnectionString` type and every area hosting module depends on it; area frameworks must
  not duplicate that kernel entry.
  `<Area>.ApplicationModel` packages are NuGet-only, injected by `Sdk.<Area>` (its own area) and
  `Sdk.Gateway` (the areas of its referenced resource projects), and never members of an
  `App.<Area>` shared framework (owner-signed developer-experience design O2/O27). No SDK injects
  an `<Area>.Client` package, and a gateway references none directly: it delivers resource commands
  through its generic control-plane command client, and it reads a store only through the
  `<Area>.ApplicationModel.Orchestration` package that wraps the store's client (the client arrives
  transitively with that package). The owner
  decisions of 2026-09-25 supersede O13's "gateways may reference `<Area>.Client`". A client takes
  no ApplicationModel reference, so it stays eligible for a shared framework: an area lists it in
  `App.<Area>` when the area's own applications consume it at run time (`App.Database` carries
  `Database.Client` and `Database.Sql.Client`) and otherwise ships it NuGet-only
  (`Database.Graph.Client`, `SecretStore.Client`, `ConfigurationStore.Client`). The runtime
  producer's `Directory.Build.props` records which choice the area made. `<Area>.ApplicationModel.Orchestration`
  packages are NuGet-only, never framework members, and injected by no SDK; the gateway references
  each one explicitly (above).

Relaxing the rule itself (beyond a per-project exemption) is an architectural decision: change
`build/Targets/Build.Rules.targets`, this file, and the owning area's README in the same commit,
with the user's explicit confirmation.
