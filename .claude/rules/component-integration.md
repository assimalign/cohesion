---
paths:
  - "libraries/**/*.csproj"
  - "resources/**/*.csproj"
  - "**/ComponentIntegrations.cs"
  - "**/*Components.cs"
  - "libraries/DependencyInjection/**"
  - "analyzers/Assimalign.Cohesion.SourceGeneration.ComponentModel/**"
  - "build/IntegrationCheck/**"
---

# Component Integration (reference-free DI/seam light-up)

How a library contributes a registration verb to a composition surface it does **not** reference —
`AddHttpClientFactory` on `IServiceProviderBuilder` without `Http.ClientFactory` ever referencing
`Assimalign.Cohesion.DependencyInjection`. This is the repo's answer to "where do DI extension
methods live?": **never** in the feature library via a hard DI reference, and **never** in a
per-integration glue project. Both were considered and rejected (owner decision, 2026-09-01;
rationale in `analyzers/Assimalign.Cohesion.SourceGeneration.ComponentModel/docs/DESIGN.md`).

## The mechanism

- `Assimalign.Cohesion.ComponentIntegrationAttribute` (public, assembly-target, in
  `Assimalign.Cohesion.Core` — already transitive everywhere) declares: "my public static factory
  method `F` can be projected onto the seam type named `"Ns.ISeam"` via its sink method `S`".
  The seam is named as a **string** — expressing it as a `Type` would create the very reference
  the mechanism exists to prevent.
- The `Assimalign.Cohesion.SourceGeneration.ComponentModel` incremental generator runs in each
  **consuming** compilation. It reads the attributes off referenced-assembly metadata and emits an
  `internal` C# 14 `extension(...)` verb **only when the named seam type actually resolves in that
  compilation** (`GetTypesByMetadataName` — the plural; the singular returns null on ambiguity).
  Seam absent → nothing emitted, plus a `COHCMP0005` info naming what a DI reference would unlock.
- Delivery needs **no per-integration wiring**: the generator ships inside Core's nupkg
  (`CohesionAnalyzerReference` in Core's csproj → `analyzers/dotnet/cs`, picked up transitively)
  and with the App shared framework (`CohesionFrameworkAnalyzer` in
  `frameworks/Assimalign.Cohesion.App.props`). In-repo test/consumer projects opt in with one
  `<CohesionAnalyzerReference Include="Assimalign.Cohesion.SourceGeneration.ComponentModel" />`.

## Adding an integration — the whole recipe

Two files in the owning library, one line in the canary. No csproj edits on either library, no
MSBuild edits, no generator edits, no slnx/CI edits, no new project.

1. `src/<Thing>Components.cs` — a `public static` factory class. For anything whose product is
   disposable the factory **must** return `Func<IServiceProvider, T>`, never a bare instance: an
   instance registration becomes a `ConstantCallSite`, which the resolver returns **without**
   `CaptureDisposable`, so the container would never dispose it. Compose eagerly inside the
   factory so validation fires at registration time; return the built instance from the lambda.
2. `src/Properties/ComponentIntegrations.cs`:

   ```csharp
   [assembly: ComponentIntegration(
       targetTypeName:    "Assimalign.Cohesion.DependencyInjection.IServiceProviderBuilder",
       targetMethodName:  "AddSingleton",
       factoryType:       typeof(ThingComponents),
       factoryMethodName: nameof(ThingComponents.CreateThing),
       Verb = "AddThing",
       Contract = typeof(IThing))]
   ```

3. Add one `CohesionProjectReference` for the library to
   `build/IntegrationCheck/Assimalign.Cohesion.IntegrationCheck.csproj` and exercise the verb in
   its `IntegrationSurface.cs`. **Never skip this** — the canary is the only thing that catches
   sink-signature drift, which otherwise breaks consumers while the declaring library's CI stays
   green.

Adding a factory **overload** needs no attribute change — every public static overload of the
named method is projected. Changing lifetime = change `targetMethodName` (`AddScoped`, …).
Deleting = delete the attribute line and the factory (source-breaking for consumers, same severity
as deleting a public method).

## Rules

- **The generator stays semantics-free.** It holds zero seam names, verb names, or lifetime
  tables, and never changes when an integration is added. If an integration seems to need a
  generator change, the design is wrong — surface it to the user.
- **Factories are dependency-free.** A factory signature may only use types the library already
  references. `AddThing(IConfiguration, ...)` is impossible by design — if a verb must *call into*
  the other library's API, the mechanism does not apply (see below).
- **Do not add hand-written `Add*(this IServiceProviderBuilder ...)` verbs to feature libraries**,
  and do not add a DI `CohesionProjectReference` to get them. The `*.Hosting`-only DI seam rule
  (`resource-areas.md`) still stands; this mechanism is compatible with it precisely because no
  compile-time DI reference is ever taken.
- Diagnostics are `COHCMP0001`–`0007` (category `ComponentModel`), all Warning/Info — never Error.
  `COHCMP0007` is the disposal rule above.

## When NOT to use it — hand-write the verb instead

- **Both libraries already reference each other's family** (e.g. `Configuration.Json` →
  `Configuration`): write the `extension(...)` verb in the owning package — that is the ordinary
  "builder verbs ship with their feature" rule.
- **The verb returns a sub-builder** for others to graft onto (`AddAuthentication` →
  `AuthenticationBuilder`): the projected verb always returns the seam; hand-write these.
- **Ordering matters** (`IWebApplicationPipelineBuilder` `Use*`/`Map*`): out of scope by design.
- **The glue is a real library** (own options types, tests, docs): make it a real package.

## Known limits (do not "fix" these ad hoc)

- Projected verbs are `internal` to each consuming compilation — a library cannot re-export a
  projected verb to *its* consumers.
- `targetTypeName` is an unchecked string; renaming a seam type silently disables every
  declaration naming it. Seam renames must grep for the metadata name across `**/ComponentIntegrations.cs`.
- Consumers below C# 14 get `COHCMP0006` and no verbs.
