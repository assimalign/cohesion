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
  reference (e.g. `Assimalign.Cohesion.Database` aggregates `Database.Types`/`Language`/
  `Execution`, so `Database.Hosting → Database` pulls them in — that is the root's own
  composition, not a hosting violation).
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
- **Standing convention:** each area's test factory, `Assimalign.Cohesion.<Area>.Testing`, is
  the one expected exemption per area — it drives the concrete runtime, which cannot be done
  through abstractions alone. Precedent: `resources/Web/Assimalign.Cohesion.Web.Testing`.
- Do not use the property to route around design pressure: if a feature library "needs" hosting,
  the missing piece is almost always a seam on the area root (that is how the Web area moved its
  authentication builder verbs out of `Web.Hosting`).

## What every area is expected to provide

- `Assimalign.Cohesion.<Area>` — the area root: the abstractions and composition seams every
  feature library builds against.
- `Assimalign.Cohesion.<Area>.Hosting` — the runtime module, referencing only the area root and
  non-area infrastructure.
- `Assimalign.Cohesion.<Area>.<Feature>` — feature libraries; builder verbs ship here, not in
  hosting.
- Framework delivery: shippable area assemblies (and their outside-area transitive closure)
  belong in the `App.<Area>` ItemGroup of `frameworks/Assimalign.Cohesion.App.props`, so
  applications get the family through the SDK without project wiring. Validate with
  `dotnet pack frameworks/Assimalign.Cohesion.App.<Area>.Runtime/src/...csproj`.

Relaxing the rule itself (beyond a per-project exemption) is an architectural decision: change
`build/Targets/Build.Rules.targets`, this file, and the owning area's README in the same commit,
with the user's explicit confirmation.
