# Resource Areas (`resources/**`)

Rules that apply to every resource area. Each `resources/<Area>/` is an L3 service platform with
a matching `Sdk.<Area>` + `App.<Area>` framework family; these rules keep every area's dependency
architecture consistent as features are added. Area-specific rule sets (e.g. `web-area.md`)
layer on top of this file.

## The hosting-isolation rule (build-enforced, all areas)

Every resource area ships exactly one runtime module, `Assimalign.Cohesion.<Area>.Hosting` — the
composition root that integrates DI, configuration, logging, and transports.

> **COHRES001** — No library in an area may reference its exact hosting module except the
> area's application-model integration, `<Area>.Hosting.ApplicationModel`, and a named exemption
> holder. Roots and feature libraries may not reference the area's
> `<Area>.Hosting.<Suffix>` integrations. Hosting-family integrations may reference each
> other, but never the exact `<Area>.Hosting` runtime module; the integration is recognized by its
> exact name for its own area and needs no exemption property. Exemptions name individual
> assemblies; an exemption for the runtime does not waive the rest of the hosting family.
>
> **COHRES002** — The exact hosting module may reference any library in its own area **except**:
> `<Area>.Testing` and `<Area>.Hosting.ApplicationModel`, which both reference the hosting
> module, so the reverse reference is a cycle; the framework producers `<Area>.Refs` and
> `<Area>.Runtime`, packaging shells that themselves reference the hosting module; and test,
> example, sample, and fixture projects, which are harnesses.
> Owner decision 2026-10-09 ("allow the Hosting project tobe [sic] able reference all `Web.*`
> projects, or more generically all the `<Area>.*` projects"; the exclusions follow from earlier
> decisions); until then the module could reference only the area root,
> `Assimalign.Cohesion.<Area>`, and its own hosting family (`<Area>.Hosting.<Suffix>`). The
> permission is not a reason to reference features: each reference lands its closure in every
> framework that carries the module (the `<Area>.Hosting` bullet under "What every area is
> expected to provide").
>
> **COHRES003** — No shipped project under `resources/**` may resolve an
> `Assimalign.Cohesion.ApplicationModel.Gateway*` assembly. A resource is a managed instance, never
> a gateway: it builds on the instance side (`Assimalign.Cohesion.ApplicationModel.Hosting`), and
> gateway orchestration belongs outside resource-area packages. There is no exemption property or
> opt-out.

> **COHRES004** — Area roots and feature libraries may reference no
> `Assimalign.Cohesion.Hosting` or `Assimalign.Cohesion.Hosting.*` library, directly or
> transitively. Only the area's hosting family (`<Area>.Hosting` and
> `<Area>.Hosting.<Suffix>`, which includes the application-model integration
> `<Area>.Hosting.ApplicationModel`) and `<Area>.Testing` may depend on those libraries.

The opposite direction has its own guard, because resources consume libraries and libraries never
depend on resources:

> **COHLIB001** — No non-harness project under `libraries/**` or `sdks/**` may reference a
> `resources/**` project, whether by project reference or by a resolved assembly. The one
> exception is the same-area SDK: a project under `sdks/Assimalign.Cohesion.Sdk.<Area>/` may
> reference `resources/<Area>/**` (precedent: `Sdk.Database.Tasks → Database.Sql.Schema`).

When a library needs behavior an area owns, the library defines the seam and the area implements
it: the managed-instance contracts (`IApplicationInstance`, `IApplicationInstanceAction`,
`ApplicationInstanceOptions`) live in `Assimalign.Cohesion.ApplicationModel.Hosting`, and each
area implements them in its `<Area>.Hosting.ApplicationModel` package below. The guard's two
layers, harness exemptions, and scope computation are in `build-system.md` ("Library and SDK
boundary guard (COHLIB001)"); relaxing it is an architectural decision on the same terms as this
file's rules (owner decision 5 of 2026-09-25).

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
COHRES guards leave projects outside `resources/` untouched, and COHLIB001 is the only
boundary guard that applies to `libraries/` and `sdks/`; the repo-wide COHNS001 `RootNamespace` check
in the same file applies everywhere, see `general-rules.md`). Violations fail the build:

- `COHRES001` is checked in two layers — the project-reference graph (every flavor:
  `CohesionProjectReference`, `CohesionPrivateProjectReference`, raw `ProjectReference`,
  transitive) and the resolved assembly closure after `ResolveAssemblyReferences` (which also
  catches `<Reference>`+`HintPath` and package-delivered DLLs). Exact-module and hosting-family
  candidates are checked separately, with exact assembly-name exemptions applied to each. The
  exact-module check skips the project named `Assimalign.Cohesion.<Area>.Hosting.ApplicationModel`
  in its own area; the hosting-family checks still apply to it.
- `COHRES002` rejects only its excluded categories, and only among the hosting module's
  **direct** references (the evaluation-time snapshot, taken before NuGet adds transitive project
  references). A reference is same-area when its name carries the `Assimalign.Cohesion.<Area>.`
  prefix or its project lives under `resources/<Area>/`. `Testing`, `Refs`, and `Runtime` are
  matched by exact name; a harness is a referenced project with a `tests/`, `examples/`,
  `samples/`, or `fixtures/` segment in its path below the repository root. A route to `Testing`,
  `Hosting.ApplicationModel`, or a producer is a cycle the build already rejects, and a route to a
  harness through another library is not checked.
- `COHRES003` and `COHRES004` are checked in two layers: the direct/transitive project-reference
  graph, then the resolved assembly closure after `ResolveAssemblyReferences`. The latter also
  catches package-delivered and `<Reference>`+`HintPath` assemblies. Every error names the
  offending assembly or assemblies.
- `COHRES003` applies automatically to every shipped resource project and has no opt-in or
  exemption.
- `COHRES004` applies automatically outside the hosting family and the area's exact `Testing`
  package. It rejects the base Hosting library and every `Hosting.*` sibling in both layers.
- Test (`tests/`), example (`examples/`), sample (`samples/`), and fixture (`fixtures/`) projects
  are automatically excluded from these guards — the rule constrains shipped libraries, not
  harnesses. This path-based exclusion applies to COHRES001–004; everything else in an
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
  a `Testing`, `Hosting.ApplicationModel`, framework-producer, or harness project — every one of
  which signals a design problem rather than a missing permission.
- Setting the property is itself the deviation marker at the point of use — always pair it with
  a comment stating the rationale, and surface it in the change summary.
- **Exactly one standing exemption holder is permitted per area:**
  `Assimalign.Cohesion.<Area>.Testing`, naming only the exact module
  `Assimalign.Cohesion.<Area>.Hosting`. The test factory drives the concrete runtime, which cannot
  be done through abstractions alone. Precedents: `resources/Web/Assimalign.Cohesion.Web.Testing`
  and `resources/Database/Assimalign.Cohesion.Database.Testing`.
- **The application-model integration needs no exemption.**
  `Assimalign.Cohesion.<Area>.Hosting.ApplicationModel` joins the area's runtime module to
  `ApplicationModel.Hosting`, so it must reference that module (owner decisions 2026-10-28 and
  2026-10-29). The rule itself sanctions that one reference, by the project's exact name in its own
  area (since 2026-10-31; until then each integration declared an exemption). Precedent:
  `resources/Web/Assimalign.Cohesion.Web.Hosting.ApplicationModel`.

  Any other project needs the deviation protocol case-by-case; there is no standing
  `.Application` exemption.
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

Being managed is a registration in that program, not a build behavior. There is no build switch,
no generated manifest or accessor, and no generated control-plane registration:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddResource("orders-api", resource => resource.UseInProcess());

var app = builder.Build();
await app.RunAsync();
```

- `AddResource` is an `extension(IServiceProviderBuilder)` member that the area's
  `<Area>.Hosting.ApplicationModel` package ships, called on the concrete builder's `Services`.
  The callback configures the area's `ApplicationInstanceOptions` subclass: the platforms the
  resource opts into (`UseInProcess()` from `ApplicationModel.Hosting.InProcess`, and each
  out-of-process platform's own verb) and the actions the application adds (`AddAction`).
- **Unmanaged** — no `COHESION_GATEWAY` platform in the environment — `AddResource` registers
  nothing, and the program is a plain application.
- **Managed**, it registers one singleton `IHostService` factory: an `ApplicationInstanceService`
  over an `ApplicationInstanceControlPlane` over the area's internal `IApplicationInstance`. The
  service publishes the instance's manifest and restores the gateway's state through the client of
  the platform the environment names, then opens the control plane to the gateway's calls. A
  platform the resource did not opt into fails the application's build.

An SDK consumer is the composition root and may reference `<Area>.Hosting`; it is not a shipped
resource-area library governed as an exemption holder. The gateway that manages it is a separate
executable on `Assimalign.Cohesion.Sdk.Gateway` (`build-system.md`), built with
`ApplicationGateway.CreateBuilder(name)` from `Assimalign.Cohesion.ApplicationModel.Gateway`; the
library area's design is `docs/libraries/ApplicationModel/DESIGN.md`.

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

The owner is whichever project's tests the fixture exists for: the Database SampleHost lives under
`Database.Testing`, whose tests launch it. A fixture a library's tests drive lives under that
library, not in the area whose runtime it happens to compose.

- **Fixtures are not samples.** A sample demonstrates the product to a reader; a fixture is a test
  input that must compile from source against the current tree. Consumer-facing examples and
  package-only smoke consumers live in the separate `cohesion-examples` repository; clone it when
  it is missing rather than recreating its content here (`workflow.md`, *Companion repositories*).
- **Non-packable, always.** Set `IsPackable=false` in the fixture's csproj.
- **The guards exempt `fixtures/` by path**, on the same terms as `tests/`: a fixture composes the
  very `<Area>.Hosting` runtime module the area's own libraries may not touch, which is the whole
  point of it. The exclusion is in `build/Targets/Build.Rules.targets` alongside
  `tests|examples|samples`, and it applies to COHRES001–004.
- **No wiring needed.** `fixtures/` sits under `libraries/` or `resources/`, both of which
  `build/Targets/Build.References.Projects.targets` already indexes, so a test project resolves the
  fixture by name through `CohesionProjectReference` — and both trees' `Directory.Build.props`
  already supply the TFM.
- **A relative path inside a fixture changes depth when it moves.** The Database fixture's
  `NuGet.Config` points at `_out/packages` relatively and
  `scripts/tests/CohesionReleasePolicy.Tests.ps1` reads that file by path; check
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
  the context. The module's own endpoints and data-plane services register `IHostService` (so
  does a managed resource's `ApplicationInstanceService`, through `AddResource`), and
  collaborators they
  share (repositories, stores, key providers) are singletons their factories resolve. Web registers
  its default HTTP policies as `IHttpExchangeInterceptor` singleton instances before application
  registrations and resolves that aggregate once in the default server factory, before listener
  callbacks; custom servers own their listeners. DI holds the
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
  registration produced. Web also rejects a non-singleton `IHttpExchangeInterceptor` registration
  or one under a narrower interceptor service type: the listener shares each instance across all
  connections and consumes only the exact aggregate contract.
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
  built provider already follow this section; its explicit `IDatabaseApplicationBuilder.AddEngine(DatabaseEngine)`
  and `AddEngine(string name, Func<IDatabaseApplicationContext, DatabaseEngine>)` shims still
  forward to the private engine registry rather than to `Services`, which reserves each engine
  name at registration (owner decision 52 of 2026-10-09).

## `Add` configures, `Use` places (owner decision, 2026-10-28)

An area whose features compose with a registration verb and a pipeline verb keeps all of a
feature's configuration on the registration verb.

- **`Add<Feature>(...)` configures.** The registration verb, a component integration on
  `builder.Services` (`component-integration.md`) or an `extension(I<Area>ApplicationBuilder)`
  member, owns the feature's options, policies and defaults, and validates them where they are
  registered. It registers what the pipeline reads, one of three things:
  - **The configured middleware**, for configuration fixed at startup. Web: the builder's `Build()`
    returns an `IWebApplicationMiddleware` (Contract `IWebApplicationMiddleware`), which
    `IWebApplicationContext.Middleware` lists. Precedent:
    `builder.Services.AddHsts(hsts => hsts.Options.Preload = true)`.
  - **A setting**, for a value an operator may change while the application runs. Precedent:
    `builder.Services.AddCors(cors => cors.Options.AddDefaultPolicy(...))` registers the `cors`
    setting.
  - **A request feature**, for state each exchange carries (Web: `IHttpFeature`, such as
    `AddErrorHandling`'s handlers). Never register configuration as a feature of its own: the host
    stamps every registered feature onto every exchange, so each one costs every request. A feature
    the package already registers may carry its composition-time options (precedent:
    `AddErrorHandling` carries the exception boundary's).
- **`Use<Feature>()` places.** The pipeline verb only positions the feature's middleware. It takes
  no configuration callback, options object or policy, and it never sets or overrides a registered
  value: it reads what `Add` registered (Web: `IWebApplicationContext.Middleware`, `Settings` and
  `Features`, at composition). The last registration wins, except where registrations add up: each
  `AddStaticFiles` registration is a mount, and `UseStaticFiles()` places them all, in registration
  order. With none, it places the feature's
  defaults, or fails the pipeline's composition with a message naming the `Add` verb when the
  feature has no safe default (precedent: `UseForwardedHeaders()`). It may complete a registration
  with what only the application context has, such as a file system for the web root. Arguments
  that shape the pipeline rather than configure a feature, such as `UseWhen`'s predicate and branch
  or a `Map*` route pattern, are not configuration.
- **Why.** Configuration on `Use` splits one feature's settings across two call sites and keeps
  them out of the registrations the host and an operator surface enumerate. An
  `<Area>.Hosting.ApplicationModel` package, which turns registered settings into resource actions
  (Web: the `cors` setting becomes the `cors` action), could then expose the feature only if the
  application were rewritten.
- **Web follows it throughout** since 2026-10-28: every Web `Use<Feature>` verb is parameterless
  (Web.Cors moved first; the other 16 feature packages followed the same day).

## What every area is expected to provide

- `Assimalign.Cohesion.<Area>` — the area root: the base abstractions and composition seams
  every feature library builds against. **The root does not absorb feature abstractions** — a
  feature's model, builder surface, and contracts live in the feature package and compose
  against the root's seams (precedent: the Web auth model and `AuthenticationBuilder` live in
  `Web.Authentication`, not in `Web`). A large area may compose its root from **child roots** —
  packages of generic base abstractions and default implementations pulled into the parent root
  for maintainability, testability, and separation of concerns (precedent:
  `Assimalign.Cohesion.Database` aggregates `Database.Types`/`Language`/`Storage`/
  `Transactions`/`Execution`/`Indexing`/`Protocol`/`Security`). **The dependency arrow always
  points root → child; a child root never references the root** — that is what keeps each child
  independently consumable, and it means a child owns its own vocabulary (value types, enums)
  and its own exception root (`StorageException`, `ProtocolException` inherit `Exception`, not
  the area exception root — the layer that owns both vocabularies translates at its boundary).
  Child-to-child references are fine. The breakdown signal for either shape is the root (or a
  child root) pulling in anything feature- or model-specific.
- **Hosting integrations:** an area's application-model integration ships as
  `Assimalign.Cohesion.<Area>.Hosting.ApplicationModel` (below), which references the area's
  hosting module, the one reference COHRES001 sanctions by name. `Assimalign.Cohesion.Hosting.Health` was removed on
  2026-10-28: a Web application serves health through Web.Health.
  Any other `Assimalign.Cohesion.<Area>.Hosting.<Suffix>` package is permitted only for an
  integration that must not land in every framework that carries the runtime module, so it stays
  opt-in; no area has one today. Such a package keeps the hosting-family constraints: it may
  reference the base Hosting library and siblings, the area root, features, other hosting-family
  integrations, and other areas' packages. It may never reference its own exact `<Area>.Hosting`
  module, and roots and features may not reference it (COHRES001). The exact module may consume
  hosting-family packages under the generic guard, but doing so brings their closure into every
  framework that carries the module, which is the cost the separate package exists to avoid.
- **The application builder seam:** the area root provides `I<Area>ApplicationBuilder` (and the
  `I<Area>Application` it builds); the hosting module implements them and exposes the creation
  entry point (`<Area>Application.CreateBuilder(string[] args)` — every resource is a `Program.cs`
  executable, managed only when it calls `builder.Services.AddResource(...)`; see "The resource
  instance is the SDK consumer's project", above). Feature/model registration verbs ship with
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
  feature libraries reference no `Assimalign.Cohesion.Hosting*` library. The application-model
  wiring lives in `<Area>.Hosting.ApplicationModel`, never in the module, which cannot reference
  that package (COHRES002) and needs no ApplicationModel package of its own (precedent:
  `Web.Hosting` references none).
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
- `Assimalign.Cohesion.<Area>.Hosting.ApplicationModel` — the area's **one** application-model
  integration: it makes the area's application a managed resource instance. An area ships no
  definition package and nothing gateway-side: a gateway's model is declared with the library
  area's `Assimalign.Cohesion.ApplicationModel.Definition`.
  - **What it ships:** the `AddResource(ResourceName name, Action<<Area>ResourceOptions>? configure = null)`
    verb as an `extension(IServiceProviderBuilder)` member, an `<Area>ResourceOptions` deriving
    from `ApplicationInstanceOptions` (so every platform's verb and `AddAction` apply), and an
    internal `IApplicationInstance` whose actions are the area's own (Web: one per registered
    setting it knows, such as `cors`) followed by the ones the application adds. Behavior,
    managed and unmanaged, is in "The resource instance is the SDK consumer's project" above.
    The verb is an ordinary extension member, not a generated component integration, because the
    package references the container directly. Precedent:
    `resources/Web/Assimalign.Cohesion.Web.Hosting.ApplicationModel` (its `docs/DESIGN.md`).
  - **References:** the area root, the area's exact `<Area>.Hosting` module (sanctioned by COHRES001 itself, with no
    `CohesionHostingIsolationExemptions` entry), `Assimalign.Cohesion.DependencyInjection`,
    `Assimalign.Cohesion.Hosting`, and `Assimalign.Cohesion.ApplicationModel.Hosting`, plus the
    area's feature packages whose settings it turns into actions (Web: `Web.Cors`). It references
    no platform: the application references each platform it opts into
    (`ApplicationModel.Hosting.InProcess`, or an out-of-process platform's instance half from
    `cohesion-platforms`). It never resolves an `ApplicationModel.Gateway*` assembly (COHRES003).
  - **Transport:** none. Every transport belongs to a platform; the package selects the client
    of the platform the environment names and registers nothing else — no server, listener, or
    HTTP client.
  - **Namespace:** its own `RootNamespace`, `Assimalign.Cohesion.<Area>.Hosting.ApplicationModel`,
    like any library. Area-prefixed type names (`WebResourceOptions`) keep the options unambiguous.
  - **Delivery:** a public member of the area's `App.<Area>` framework, so an `Sdk.<Area>`
    application calls `AddResource` with no package reference.
- `Assimalign.Cohesion.<Area>.Testing` — the optional shippable test factory that composes the
  area's concrete runtime for tests. When present, it is the area's sole standing
  `CohesionHostingIsolationExemptions` holder.
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
  assemblies (and their outside-area transitive closure that App does not carry, including the
  assemblies of packages outside `Microsoft.NETCore.App`) belong in the
  `App.<Area>` public/private items of
  `resources/<Area>/Assimalign.Cohesion.<Area>.Runtime/Directory.Build.props`, so applications
  get the family without project wiring. The framework closure tests guard every
  area, including feature dependencies such as Web.Caching → Caching/InMemory. Validate with
  `dotnet pack resources/<Area>/Assimalign.Cohesion.<Area>.Runtime/src/Assimalign.Cohesion.<Area>.Runtime.csproj`.
  App carries `Assimalign.Cohesion.Connections` because every area hosting module depends on it,
  and `Assimalign.Cohesion.ApplicationModel.Hosting` because every area's
  `<Area>.Hosting.ApplicationModel` builds on it; area frameworks must not duplicate those kernel
  entries. `<Area>.Hosting.ApplicationModel` is a public `App.<Area>` member; the platform
  packages a resource opts into (`ApplicationModel.Hosting.InProcess`, the `cohesion-platforms`
  instance halves) are NuGet packages the application references, and no `ApplicationModel.Gateway*`
  assembly is a member of any shared framework (COHRES003).
  No SDK injects an `<Area>.Client` package. A client takes no ApplicationModel reference, so it
  stays eligible for a shared framework: an area lists it in `App.<Area>` when the area's own
  applications consume it at run time (`App.Database` carries `Database.Client` and
  `Database.Sql.Client`) and otherwise ships it NuGet-only (`Database.Graph.Client`,
  `SecretStore.Client`, `ConfigurationStore.Client`). The runtime producer's
  `Directory.Build.props` records which choice the area made.

Relaxing the rule itself (beyond a per-project exemption) is an architectural decision: change
`build/Targets/Build.Rules.targets`, this file, and the owning area's README in the same commit,
with the user's explicit confirmation.
