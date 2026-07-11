# Web Area (`resources/Web/**`)

Rules specific to the Web resource area. They apply to every project under `resources/Web/` and
exist to keep the area's dependency architecture intact as new feature libraries are added. The
canonical prose lives in `resources/Web/README.md`; this file is the working rule set.

## The dependency rule (build-enforced)

> **`Assimalign.Cohesion.Web.Hosting` is the runtime module — no Web-area library may reference
> it, and it may reference no Web-area library except the root `Assimalign.Cohesion.Web`.**

- A Web feature library (`Assimalign.Cohesion.Web.<Feature>`) may reference: the root
  `Assimalign.Cohesion.Web`, **other Web feature libraries**, and anything outside the Web area
  (`Http.*`, `Security.*`, `IdentityModel.*`, `Connections.*`, …). Cross-feature references are
  fine; the rule is Hosting-centric.
- **Never reference `Assimalign.Cohesion.Web.Hosting`** from a feature library. Hosting composes
  DI, configuration, logging, and transports; a reference to it drags that surface into every
  consumer and pushes users toward container-driven design.
- **Never reference a Web feature library from `Web.Hosting`.** Applications get the whole family
  through the `App.Web` shared framework (via `Sdk.Web`), so the runtime needs no compile-time
  knowledge of the features it hosts.
- Sole sanctioned exception: `Assimalign.Cohesion.Web.Testing → Web.Hosting` (the test factory
  drives the concrete runtime). Do not add others without the deviation protocol
  (`deviations.md`) and an update to the enforcement target.

**Enforcement:** `resources/Web/Directory.Build.targets` fails the build of any library project
that violates the rule (`COHWEB001`/`COHWEB002`), in two layers: the project-reference graph
(every flavor — `CohesionProjectReference`, `CohesionPrivateProjectReference`, raw
`ProjectReference`, transitive) and the resolved assembly closure after
`ResolveAssemblyReferences` (which also catches `<Reference>`+`HintPath` and package-delivered
DLLs). Test and example projects are exempt — the rule constrains shipped libraries, not
harnesses; everything else under `resources/Web` is guarded regardless of folder layout. Every
Web project is in the `.github/workflows/resource-web.yml` matrix so the guard executes in CI.
If a legitimate architectural change requires relaxing the rule, change the target and
`resources/Web/README.md` in the same commit, with the user's explicit confirmation.

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
  `AuthenticationBuilder`; `Results.ServerSentEvents(...)` on the `Results` factories).

## Adding a new Web feature library — required wiring

A new `resources/Web/Assimalign.Cohesion.Web.<Feature>/` project is not done until all of these
are updated (each has bitten before):

1. **csproj** — references per the dependency rule above; `CohesionProjectReference` only.
2. **Framework manifest** — add the assembly to the `Assimalign.Cohesion.App.Web` ItemGroup in
   `frameworks/Assimalign.Cohesion.App.props`, plus any new outside-area transitive dependencies
   the App/App.Web lists don't already carry. Validate with
   `dotnet pack frameworks/Assimalign.Cohesion.App.Web.Runtime/src/Assimalign.Cohesion.App.Web.Runtime.csproj`
   (its collection target hard-fails on unresolvable assemblies). Exclusions from the manifest
   (test harnesses, source-less placeholders) are documented in the manifest comment.
3. **Solutions** — entries in `resources/Web/Assimalign.Cohesion.Web.slnx` **and** the root
   `Assimalign.Cohesion.slnx` (src, tests, docs files).
4. **CI** — add the project to the matrix in `.github/workflows/resource-web.yml`.
5. **Docs** — `docs/OVERVIEW.md` + `docs/DESIGN.md` (plus `docs/Assembly/` per
   `documentation.md`'s layout as the public API stabilizes), and a row in the project map in
   `resources/Web/README.md`.
