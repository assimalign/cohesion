# Web Area (`resources/Web/**`)

Rules specific to the Web resource area. They apply to every project under `resources/Web/` and
exist to keep the area's dependency architecture intact as new feature libraries are added. The
canonical prose lives in `resources/Web/README.md`; this file is the working rule set.

## The dependency rule (build-enforced)

> **`Assimalign.Cohesion.Web.Hosting` is the runtime module — no Web-area library may reference
> it, and it may reference any Web-area library except `Web.Testing`, `Web.ApplicationModel`,
> the `App.Web` producers (`Web.Refs`, `Web.Runtime`), and test, example, sample, and fixture
> projects.** (COHRES002, relaxed by owner decision 2026-10-09; before that, `Web.Hosting` could
> reference only the root `Assimalign.Cohesion.Web` and its own hosting family.) `Web.ApplicationModel`
> stays out of `Web.Hosting`'s resolved closure by any route, including through a Web library it
> references.

- A Web feature library (`Assimalign.Cohesion.Web.<Feature>`) may reference: the root
  `Assimalign.Cohesion.Web`, **other Web feature libraries**, and anything outside the Web area
  (`Http.*`, `Security.*`, `IdentityModel.*`, `Connections.*`, …), except
  `Assimalign.Cohesion.Hosting*` libraries (COHRES004). Cross-feature references are
  fine; the rule is Hosting-centric.
- **Never reference `Assimalign.Cohesion.Web.Hosting`** from a feature library. Hosting composes
  DI, configuration, logging, and transports; a reference to it drags that surface into every
  consumer and pushes users toward container-driven design.
- The hosting family includes `Web.Hosting.Resources` and `Web.Hosting.Health`, integrating
  the corresponding shared `Hosting.*` libraries. These may reference the Web root,
  feature libraries, other hosting-family integrations, and other areas' packages, but
  never the exact `Web.Hosting` module. The exact module may consume its own hosting family
  under COHRES002. Roots and features may not reference the hosting
  family (COHRES001) or shared Hosting libraries (COHRES004).
- **`Web.Hosting` references a Web library only when the runtime itself needs it** — runtime
  machinery it composes, such as a router it drives or a server package. COHRES002 permits any
  Web library but the exclusions above. Applications still get the whole family through the
  `App.Web` shared framework (via `Sdk.Web`), and feature registration still ships with the
  feature, so hosting a feature never needs the reference. Every reference costs more than one
  framework: `Web.Hosting` is a private member of all 17 other area frameworks, so its new
  reference's closure must be added to each of them.
- Sole sanctioned exception: `Assimalign.Cohesion.Web.Testing → Web.Hosting` and
  `Web.Hosting.Resources` (the test factory drives the concrete runtime and its control plane), declared via `CohesionHostingIsolationExemptions` in its own
  csproj. Do not add others without the deviation protocol (`deviations.md`) — see
  `resource-areas.md` for the opt-out mechanism.

**Enforcement:** this is the Web instance of the repo-wide **resource hosting-isolation rule** —
see `resource-areas.md` for the general rule, the `COHRES001`/`COHRES002`/`COHRES004` build errors, the
two-layer check semantics, COHRES002's excluded categories (direct references, plus the resolved closure for the ApplicationModel packages), and the per-project `CohesionHostingIsolationExemptions` opt-out
(deviation protocol required; `Web.Testing` is the standing exemption, declared in its own
csproj). Every Web library is in the `.github/workflows/resource-web.yml` matrix so the guard
executes in CI. The two framework producers, `Assimalign.Cohesion.Web.Refs` and
`Assimalign.Cohesion.Web.Runtime`, are neither libraries nor exemption holders: they are the
`App.Web` packaging shells, which the guard skips by exact identity, and `sdk-smoke.yml` packs them
instead of the area matrix (`build-system.md`, "Framework producer projects").

## Builder verbs ship with their feature

Composition verbs live in the package that owns the feature — never in `Web.Hosting`. Owner
decisions 34 and 35 of 2026-10-09 (#1380, `docs/programs/HTTP_WEB_PROGRAM_PLAN.md` §7.4) split them
by kind:

- **Registration verbs are component integrations on `builder.Services`.** A feature package
  registers its `IHttpFeature` as `builder.Services.Add<Feature>(...)`, as other .NET hosting models
  do, by declaring `[assembly: ComponentIntegration(...)]` in `src/Properties/ComponentIntegrations.cs`
  with `targetTypeName: "Assimalign.Cohesion.DependencyInjection.IServiceProviderBuilder"`,
  `targetMethodName: "AddSingleton"` and `Contract = typeof(IHttpFeature)`
  (`component-integration.md`). The generator projects the verb into the application, so the
  package takes **no** DI reference and `Web.Hosting` takes no reference to the package for it
  (each `Web.Hosting` reference would land the feature's closure in all 18 area frameworks). No
  feature package declares an `extension(IWebApplicationBuilder)` registration verb.
  Precedents: the eight packages Antiforgery, Authentication, Authorization, ErrorHandling,
  OpenApi, Routing, Serialization and Validation.
- **Choose the shape by the verb's arguments.** A verb whose callback configures a builder that
  other packages graft onto uses the **builder template**: a public builder with a public
  parameterless constructor and one zero-parameter instance `Build()` that returns the feature
  (`AuthenticationBuilder`, `ErrorHandlingBuilder`, `ContentSerializationBuilder` →
  `builder.Services.AddAuthentication(auth => auth.AddCookie(...))`). A verb with no arguments, an
  optional options callback, a value argument the template cannot carry, or a required callback
  over an options type that has no `Build()` uses the **static factory**: an
  `[EditorBrowsable(Never)]` `<Feature>Components` class in a `ComponentModel/` folder that
  declares the feature's **root namespace**, so the projected verb lands in the namespace callers
  already import (`RoutingComponents` → `builder.Services.AddRouting()`, `AntiforgeryComponents` →
  `AddAntiforgery(dataProtectionProvider, ...)`, `ValidationComponents` →
  `AddValidation(validation => ...)` over `EndpointValidationOptions`). The builder template
  always takes a required `Action<TBuilder>`, so it never fits a verb that is called bare, and it
  needs a builder with `Build()`, so an options type does not get one just to fit the template.
- **Every request feature is a singleton (decision 35).** The host stamps one snapshot of the
  `IHttpFeature` aggregate onto every exchange, and middleware reads it while the pipeline is
  composed. `WebApplicationBuilder.Build` rejects an `IHttpFeature` registration that is scoped
  or transient, a registration under a contract derived from `IHttpFeature` (it would never
  be stamped), and an `IHttpFeature` instance or implementation type that is disposable (the
  exchange would dispose the shared instance after its first request). A factory registration's
  product exists only once it is resolved, so the pipeline build rejects a disposable one; that
  covers the builder-template verbs and `AddFeature(factory)`, and fails host start before any
  request. Each error names the registration. Request-scoped
  services for handlers are a separate future decision — a lazily created scope owned by the
  server, for a separate service type — never a looser lifetime on `IHttpFeature`.
- **`IWebApplicationBuilder.AddFeature` (both overloads) stays the raw path** for a feature no
  package ships a verb for, and for composition surfaces without a container. It registers the
  same singleton.
- **Pipeline verbs ship with the feature as `extension(...)` members:** `Use<Feature>` extends
  `IWebApplicationPipelineBuilder`, and `Map*` verbs extend the pipeline or router surface (all
  from `Assimalign.Cohesion.Web` and `Web.Routing`). Ordering matters there, which the
  component-integration mechanism does not model.
- Registration stays dependency-free: a factory or builder composes values and options objects
  into the feature eagerly. No configuration binding and no request-time service location — the
  `*.Hosting`-only DI rule still stands; the feature package never sees the container.
- A sub-family that extends another feature's builder surface grafts onto it with C# 14
  `extension(...)` members in its own package (precedent: `AddCookie`/`AddJwtBearer` on
  `AuthenticationBuilder`, reached through the `AddAuthentication` callback). Static extension
  members on the target's static classes work too, when a package needs to extend a plain-static
  surface.
- In-repo projects that call a projected verb opt into the generator with
  `<CohesionAnalyzerReference Include="Assimalign.Cohesion.SourceGeneration.ComponentModel" />`
  (tests, the AOT guard); `Sdk.Web` applications receive it through the App framework analyzer.
  `build/IntegrationCheck` exercises every verb and overload.
- **Composition model (2026-07-10 direction):** the Web area is middleware-first — fluent
  `.Use(...)` and `IWebApplicationMiddleware` over a return-value result model. The IResult
  abstraction was withdrawn before merge; request/response formatting and error handling are
  the re-scoped #864 design (content-serialization registry + `OnError` hook). Controller and
  function binding surfaces are set aside entirely.

## Adding a new Web feature or hosting-family library — required wiring

A new `resources/Web/Assimalign.Cohesion.Web.<Feature>/` or `Web.Hosting.<Suffix>` project
(precedents: `Web.Hosting.Resources`, `Web.Hosting.Health`) is not done until all of these
are updated (each has bitten before):

1. **csproj** — references per the dependency rule above; `CohesionProjectReference` only. A
   package that registers an `IHttpFeature` declares its `builder.Services.Add<Feature>` verb as a
   component integration (above), adds itself to `build/IntegrationCheck` (reference plus a call to
   every overload), and adds its `src/**` path to the `.github/workflows/analyzers.yml` push filter
   so a signature change rebuilds the canary.
2. **Framework membership** — add the assembly to the `Assimalign.Cohesion.App.Web` list in
   `resources/Web/Assimalign.Cohesion.Web.Runtime/Directory.Build.props` (the Refs producer
   imports the same file), plus any new outside-area transitive dependencies the App/App.Web
   lists don't already carry. Validate with
   `dotnet pack resources/Web/Assimalign.Cohesion.Web.Runtime/src/Assimalign.Cohesion.Web.Runtime.csproj`
   (its collection target hard-fails on unresolvable assemblies). Exclusions from the list are
   documented in that file's comment. **If `Web.Hosting` references the new library**, its closure
   also becomes a `CohesionFrameworkPrivateAssembly` in every other area's
   `resources/<Area>/Assimalign.Cohesion.<Area>.Runtime/Directory.Build.props` (all 17 carry
   `Web.Hosting` privately); pack each of those producers too.
3. **Solutions** — entries in `resources/Web/Assimalign.Cohesion.Web.slnx`,
   `resources/Assimalign.Cohesion.Resources.slnx`, and the root `Assimalign.Cohesion.slnx`
   (src, tests, docs files).
4. **CI and inventory** — add the project to the matrix in `.github/workflows/resource-web.yml`
   and to `installer/scripts/modules/CohesionPackaging.psm1`. Keep new entries sorted within
   the Web block without reordering unrelated existing entries; the inventory guard is two-way.
5. **Docs** — `docs/OVERVIEW.md` + `docs/DESIGN.md` (plus `docs/Assembly/` per
   `documentation.md`'s layout as the public API stabilizes), and a row in the project map in
   `resources/Web/README.md`.

Background-work registration (`AddService`) lives on the concrete `WebApplicationBuilder`
in `Web.Hosting`; the root builder supplies Web verbs and `Build()` without hosting types.
