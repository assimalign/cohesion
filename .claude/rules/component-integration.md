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
  `libraries/App/Assimalign.Cohesion.App.props`). In-repo test/consumer projects opt in with one
  `<CohesionAnalyzerReference Include="Assimalign.Cohesion.SourceGeneration.ComponentModel" />`.

## Adding an integration — the whole recipe

One declaration file in the owning library, one line in the canary, and one path in the canary's
CI trigger. No csproj edits on either library, no MSBuild edits, no generator edits, no slnx edits,
no new project.

**Preferred shape — the builder template (zero added public surface).** If the library already has
a publicly-constructible builder (`new ThingBuilder()` → configure → `Build()`), name its
**instance** build method; the generator inlines new → configure → build eagerly and registers the
product through the `Func<>` sink (always disposal-safe):

```csharp
// src/Properties/ComponentIntegrations.cs
[assembly: ComponentIntegration(
    targetTypeName:    "Assimalign.Cohesion.DependencyInjection.IServiceProviderBuilder",
    targetMethodName:  "AddSingleton",
    factoryType:       typeof(ThingBuilder),            // public parameterless ctor required
    factoryMethodName: nameof(ThingBuilder.Build),      // public instance, zero params
    Verb = "AddThing",
    Contract = typeof(IThing))]
```

The projected verb is `AddThing(Action<ThingBuilder> configure)`. **Do not create a
`XxxComponents` factory class when the builder template can express the integration** — the whole
point of the instance shape is that the integration adds nothing to the library's public API
(precedent: `Http.ClientFactory`, whose `HttpClientFactoryComponents` class was deleted in favor
of declaring `HttpClientFactoryBuilder.Build`).

**Fallback shape — a static factory** for compositions a builder can't express: a `public static`
factory class whose method the attribute names. Quarantine it — `[EditorBrowsable(
EditorBrowsableState.Never)]`, ideally in a `<RootNamespace>.ComponentModel` sub-namespace — since
it exists only for the generator to call. The generator emits the verb into the **factory type's
namespace**, so a factory in a sub-namespace makes every caller import that sub-namespace too; when
the verb belongs beside the package's other verbs, keep the factory in a `ComponentModel/` folder
but declare the root namespace (Web precedent: `RoutingComponents` in `Assimalign.Cohesion.Web.Routing`
projects `builder.Services.AddRouting()`). The builder template always takes a required
`Action<TBuilder>`, so a verb callers invoke bare (`AddRouting()`) or with only an optional options
callback is a static factory. If the product is disposable the factory **must** return
`Func<IServiceProvider, T>`, never a bare instance: an instance registration becomes a
`ConstantCallSite`, which the resolver returns **without** `CaptureDisposable`, so the container
would never dispose it (`COHCMP0007` guards this; the builder template is immune by construction).
Compose eagerly inside the factory so validation fires at registration time. Every public static
overload of the named method is projected, so adding an overload needs no attribute change.

**Both shapes:** add one `CohesionProjectReference` for the library to
`build/IntegrationCheck/Assimalign.Cohesion.IntegrationCheck.csproj` and exercise the verb in its
`IntegrationSurface.cs`, calling every overload, then add the library's path to the `push.paths`
filter of `.github/workflows/analyzers.yml`, which builds the canary. **Never skip this** — the
canary is the only thing that catches sink-signature drift, which otherwise breaks consumers while
the declaring library's CI stays green.

**Lifetime is not just `targetMethodName`.** Naming `AddScoped` or `AddTransient` changes the
descriptor's lifetime, but what a resolve returns depends on the shape:

- **Builder template:** the verb builds one product eagerly, at registration, and registers
  `_ => product`, so every scope and every transient resolve gets that same instance, and a
  disposable product is disposed with the first scope and then handed out disposed. A builder
  template targets `AddSingleton`.
- **Static factory returning an instance:** only the `AddSingleton` instance overload binds; any
  other sink fails to compile in the consumer.
- **Static factory returning `Func<IServiceProvider, T>`:** the only shape where the sink's
  lifetime is real, because the container invokes the factory per scope or per resolve.

A seam owner may also require a lifetime: `Web.Hosting` rejects any `IHttpFeature` registration
that is not a singleton at `Build` (owner decision 35, 2026-10-09), so every Web feature
integration targets `AddSingleton`. Deleting = delete the attribute line (source-breaking for
consumers, same severity as deleting a public method). A method group mixing static and instance
members is rejected (`COHCMP0003`).

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
- **The verb must return a sub-builder** that callers keep using after the call: the projected
  verb always returns the seam. A sub-builder that is only configured at registration is the
  builder template's configure callback instead: owner decision 34 (2026-10-09) moved Web's
  `AddAuthentication` → `AuthenticationBuilder` to `builder.Services.AddAuthentication(auth =>
  auth.AddCookie())`, and `AddErrorHandling` and `AddContentSerialization` the same way.
- **Ordering matters** (`IWebApplicationPipelineBuilder` `Use*`/`Map*`): out of scope by design.
- **The glue is a real library** (own options types, tests, docs): make it a real package.

## Known limits (do not "fix" these ad hoc)

- Projected verbs are `internal` to each consuming compilation — a library cannot re-export a
  projected verb to *its* consumers.
- `targetTypeName` is an unchecked string; renaming a seam type silently disables every
  declaration naming it. Seam renames must grep for the metadata name across `**/ComponentIntegrations.cs`.
- Consumers below C# 14 get `COHCMP0006` and no verbs.
