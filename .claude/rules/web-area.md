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

Composition verbs live in the package that owns the feature — never in `Web.Hosting`:

- `Add<Feature>` extends the root `IWebApplicationBuilder`; `Use<Feature>` extends
  `IWebApplicationPipelineBuilder` (both from `Assimalign.Cohesion.Web`).
- Registration must stay dependency-free: attach an `IHttpFeature` via `builder.AddFeature(...)`
  and hold composition state in values/options objects. No service container, no configuration
  binding, no request-time service location — the `*.Hosting`-only DI rule still stands; feature
  verbs simply must not need DI.
- A sub-family that extends another feature's builder surface grafts onto it with C# 14
  `extension(...)` members in its own package (precedent: `AddCookie`/`AddJwtBearer` on
  `AuthenticationBuilder`). Static extension members on the target's static classes work too,
  when a package needs to extend a plain-static surface.
- **Composition model (2026-07-10 direction):** the Web area is middleware-first — fluent
  `.Use(...)` and `IWebApplicationMiddleware` over a return-value result model. The IResult
  abstraction was withdrawn before merge; request/response formatting and error handling are
  the re-scoped #864 design (content-serialization registry + `OnError` hook). Controller and
  function binding surfaces are set aside entirely.

## Adding a new Web feature or hosting-family library — required wiring

A new `resources/Web/Assimalign.Cohesion.Web.<Feature>/` or `Web.Hosting.<Suffix>` project
(precedents: `Web.Hosting.Resources`, `Web.Hosting.Health`) is not done until all of these
are updated (each has bitten before):

1. **csproj** — references per the dependency rule above; `CohesionProjectReference` only.
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
