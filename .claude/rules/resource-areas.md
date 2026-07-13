# Resource Areas (`resources/**`)

Rules that apply to every resource area. Each `resources/<Area>/` is an L3 service platform with
a matching `Sdk.<Area>` + `App.<Area>` framework family; these rules keep every area's dependency
architecture consistent as features are added. Area-specific rule sets (e.g. `web-area.md`)
layer on top of this file.

## The hosting-isolation rule (build-enforced, all areas)

Every resource area ships exactly one runtime module, `Assimalign.Cohesion.<Area>.Hosting` — the
composition root that integrates DI, configuration, logging, and transports.

> **COHRES001** — No library in an area may reference its area's hosting module. A feature
> library that referenced it would drag the whole composition surface into every consumer and
> push users toward container-driven design.
>
> **COHRES002** — The hosting module may reference no library in its own area except the area
> root, `Assimalign.Cohesion.<Area>`. The shared framework (`App.<Area>`, via `Sdk.<Area>`)
> delivers the family to applications, so the runtime needs no compile-time knowledge of the
> features it hosts. Builder verbs ship with their feature package and compose against the area
> root's abstractions.

Cross-references **between feature libraries in an area are fine** — the rule is
hosting-centric, not hub-and-spoke. References to anything outside the area (`Http.*`,
`Security.*`, `IdentityModel.*`, other areas' libraries per the cross-resource rules in
`build-system.md`) are likewise unconstrained by this rule.

**Enforcement** lives in `build/Targets/Build.Rules.targets` (imported for every project;
projects outside `resources/` are untouched). Violations fail the build:

- `COHRES001` is checked in two layers — the project-reference graph (every flavor:
  `CohesionProjectReference`, `CohesionPrivateProjectReference`, raw `ProjectReference`,
  transitive) and the resolved assembly closure after `ResolveAssemblyReferences` (which also
  catches `<Reference>`+`HintPath` and package-delivered DLLs). Nothing may pull hosting in by
  any route.
- `COHRES002` constrains the hosting module's **direct** references only: same-area assemblies
  legitimately arrive in its resolved closure transitively through the sanctioned area-root
  reference (e.g. `Assimalign.Cohesion.Database` aggregates its child roots — `Database.Types`/
  `Language`/`Storage`/`Transactions`/`Execution`/`Protocol`/`Security`/`Governance` — so
  `Database.Hosting → Database` pulls them all in — that is the root's own composition, not a
  hosting violation).
- Test (`tests/`), example (`examples/`), and sample (`samples/`) projects are exempt — the rule
  constrains shipped libraries, not harnesses. Everything else in an area is guarded regardless
  of folder layout.

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
- **Standing conventions — the three expected exemption holders per area** (anything else needs
  the deviation protocol case-by-case):
  - `Assimalign.Cohesion.<Area>.Testing` — the test factory drives the concrete runtime, which
    cannot be done through abstractions alone. Precedent:
    `resources/Web/Assimalign.Cohesion.Web.Testing`.
  - `Assimalign.Cohesion.<Area>.Application` — the composition-root project an SDK consumer
    loads (see "The `<Area>.Application` convention" below); composing the hosting module is
    its purpose, the analog of a user application. Precedent:
    `resources/Database/Assimalign.Cohesion.Database.Application`.
  - `Assimalign.Cohesion.<Area>.ApplicationModel` — the SDK-specific orchestration plane that
    surfaces the area's generated manifest to the gateway; sanctioned for when that bridging
    requires naming the runtime (today's instances are manifest-only and declare no exemption —
    the sanction is standing, not a mandate to take the reference).
- Do not use the property to route around design pressure: if a feature library "needs" hosting,
  the missing piece is almost always a seam on the area root (that is how the Web area moved its
  authentication builder verbs out of `Web.Hosting`).

## The `<Area>.Application` convention

Every area pairs `Assimalign.Cohesion.<Area>.Application` with
`Assimalign.Cohesion.<Area>.ApplicationModel`, and the **target design** (owner direction,
2026-07-13) is:

- `<Area>.Application` is **not an executable** — it is the **manifest-generation project**. An
  SDK consumer loads this specific project; build tasks code-generate the application manifest
  from it; the SDK-specific `<Area>.ApplicationModel` then helps the gateway access that
  manifest. This is the same convention in every resource area.
- `<Area>.ApplicationModel` stays the declarative orchestration plane (the resource manifest +
  `Add<Area>(...)` verbs) and never references the runtime.

**Interim state (pinned):** the current `Database.Application` is a composition-root
*executable* serving the SQL engine — it exists because the resource needed a real process for
the gateway E2E before the ApplicationModel program landed. The realignment to the
manifest-generation design is deliberately pinned on that program:
[assimalign/cohesion#906](https://github.com/assimalign/cohesion/issues/906). Until it lands,
new areas that need a runnable host may follow the same interim executable shape, carrying the
sanctioned COHRES001 exemption above.

## What every area is expected to provide

- `Assimalign.Cohesion.<Area>` — the area root: the base abstractions and composition seams
  every feature library builds against. **The root does not absorb feature abstractions** — a
  feature's model, builder surface, and contracts live in the feature package and compose
  against the root's seams (precedent: the Web auth model and `AuthenticationBuilder` live in
  `Web.Authentication`, not in `Web`). A large area may compose its root from **child roots** —
  packages of generic base abstractions and default implementations pulled into the parent root
  for maintainability, testability, and separation of concerns (precedent:
  `Assimalign.Cohesion.Database` aggregates `Database.Types`/`Language`/`Storage`/
  `Transactions`/`Execution`/`Protocol`/`Security`/`Governance`). **The dependency arrow always
  points root → child; a child root never references the root** — that is what keeps each child
  independently consumable, and it means a child owns its own vocabulary (value types, enums)
  and its own exception root (`StorageException`, `ProtocolException` inherit `Exception`, not
  the area exception root — the layer that owns both vocabularies translates at its boundary).
  Child-to-child references are fine. The breakdown signal for either shape is the root (or a
  child root) pulling in anything feature- or model-specific.
- **The application builder seam:** the area root provides `I<Area>ApplicationBuilder` (and the
  `I<Area>Application` it builds); the hosting module implements them and exposes the creation
  entry point (`<Area>Application.CreateBuilder()`). Feature/model registration verbs ship with
  their feature package as `extension(I<Area>ApplicationBuilder)` members and compose against
  the root builder — never against the hosting module — so a feature or model registers itself
  on any composition surface without knowing the hosting layer. Registration stays
  dependency-free (values and options objects; no container, no configuration binding).
  Precedents: `IWebApplicationBuilder` (Web root) + `WebApplication.CreateBuilder()`
  (`Web.Hosting`) + `AddAuthentication` (`Web.Authentication`); `IDatabaseApplicationBuilder`
  (Database root) + `DatabaseApplication.CreateBuilder()` (`Database.Hosting`) +
  `AddSqlDatabase` (`Database.Sql`). This pattern is expected to be the same in every area.
- `Assimalign.Cohesion.<Area>.Hosting` — the runtime module, referencing only the area root and
  non-area infrastructure. **If the hosting module ever appears to need a same-area dependency
  beyond the root, that is an architecture revisit — surface it to the user — not a case for
  the exemption property or for pushing the dependency's types into the root.**
- `Assimalign.Cohesion.<Area>.<Feature>` — feature libraries; builder verbs ship here, not in
  hosting.
- Framework delivery: shippable area assemblies (and their outside-area transitive closure)
  belong in the `App.<Area>` ItemGroup of `frameworks/Assimalign.Cohesion.App.props`, so
  applications get the family through the SDK without project wiring. Validate with
  `dotnet pack frameworks/Assimalign.Cohesion.App.<Area>.Runtime/src/...csproj`.

Relaxing the rule itself (beyond a per-project exemption) is an architectural decision: change
`build/Targets/Build.Rules.targets`, this file, and the owning area's README in the same commit,
with the user's explicit confirmation.
