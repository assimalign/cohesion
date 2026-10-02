# Assimalign.Cohesion.OpenApi.SourceGeneration — Design

## Design intent

The AOT-safe discovery half of the attribute path: find the OpenApi authoring attributes at compile
time and emit their metadata as static data, so an application never reflects over its own assembly at
run time to build an OpenAPI description. This is the reflection-free replacement for a
`Type.GetCustomAttributes` scan, and it is what makes the attribute path NativeAOT- and trimming-safe.

## Pipeline

An `IIncrementalGenerator` with three inputs combined into one emit:

- **Operations** — `ForAttributeWithMetadataName(OpenApiOperationAttribute)` over method declarations.
  The transform reads the operation attribute plus the method's parameter, request-body, response, and
  security-requirement attributes, and builds one `OpenApiOperationMetadata` initializer.
- **Schemas** — `ForAttributeWithMetadataName(OpenApiSchemaAttribute)` over type declarations, reading
  each member's `[OpenApiSchemaProperty]`.
- **Document-level** — a `CompilationProvider` scan for `[OpenApiTag]` and `[OpenApiSecurityScheme]` on
  the assembly and on types (these attributes allow assembly targets, which `ForAttributeWithMetadataName`
  does not surface, so a scan is used; there are few of them and they are cross-cutting).

The three are `Combine`d and `RegisterSourceOutput` emits a single `OpenApiMetadataRegistry` class in
the `Assimalign.Cohesion.OpenApi.Generated` namespace exposing four `IReadOnlyList` properties.

## Emitting data, not the model

The generator emits the flat **metadata** records (from `Assimalign.Cohesion.OpenApi.Attributes`) as
object initializers — not the canonical model. This keeps the generated code simple (plain data) and
defers model assembly to the generation pipeline (feature .06's `OpenApiDocumentGenerator`), which the
consumer references. `ModelType = typeof(Pet)` resolves to `#/components/schemas/Pet` by the type's
name — the same convention the runtime mapper uses — so a symbol name and a runtime `Type` produce the
same reference.

## Incremental caching

Generator pipeline models must compare by value or the incremental cache never recognizes unchanged
inputs. Two decisions enforce that:

- Each per-symbol transform returns a **string** initializer fragment (value-equatable) rather than a
  model holding `ISymbol`s.
- Diagnostics are captured as `DiagnosticInfo` — a value-equatable record holding a serializable
  location (file path + spans) rather than a `Location` — wrapped in an `EquatableArray<T>` so the
  collection compares by element.

## Diagnostics

The generator reports the runtime mapper's rules for the attributes it reads, under the mapper's ids:
empty operation path (`OPENAPIATTR0001`), ambiguous body schema (`OPENAPIATTR0002`), non-required path
parameter (`OPENAPIATTR0003`, a warning; the metadata is corrected), and an incomplete API key scheme
(`OPENAPIATTR0006`). It does not read `[OpenApiExample]`, so the two example rules are raised only by
the runtime mapper: `OPENAPIATTR0004` (conflicting values) has a descriptor here but nothing reports it,
and `OPENAPIATTR0005` (neither value) has none. Analyzer release tracking is declared in
`AnalyzerReleases.*.md`.

## Build posture

netstandard2.0, `IsRoslynComponent=true`, `IsAotCompatible=false` — inherited from
`analyzers/Directory.Build.props`. The generator is a build-time component; the AOT constraint applies
to the *generated* code (which is reflection-free static data), not to the generator assembly itself.

## Delivery

The generator ships inside the `Assimalign.Cohesion.OpenApi.Attributes` package. That project declares
`<CohesionAnalyzerReference Include="Assimalign.Cohesion.OpenApi.SourceGeneration" />`, which runs the
generator in the Attributes compile (it finds no annotations there and emits nothing) and packs the DLL
at `analyzers/dotnet/cs/` in the Attributes `.nupkg`. This is the precedent the repository already set
for an ordinary NuGet library: `ObjectMapping` carries `SourceGeneration` and `Core` carries
`SourceGeneration.ComponentModel` the same way.

Attributes is the carrier for two reasons:

- **It reaches every compilation that needs the generator.** The generator sees only the compilation it
  runs in, and every compilation that applies the attributes references Attributes. The SDK also loads
  analyzers from every package in the restore graph, transitive ones included, so a consumer that
  references only `OpenApi.Generation` or `OpenApi.Integration` gets the generator through their
  Attributes dependency. This was checked against locally packed packages: a plain `Microsoft.NET.Sdk`
  project with a single package reference to Generation produced `OpenApiMetadataRegistry.g.cs`.
- **The package that delivers the generator delivers what its output compiles against.** The emitted
  registry names only Attributes' metadata records and the root model's enums, both in Attributes'
  own closure.

A project reference carries no analyzer: `CohesionAnalyzerReference` marks its reference
`PrivateAssets="all"` (`build/Targets/Build.References.Analyzers.targets`), so the generator never flows
past the project that declares it. A project inside this repository that needs the registry declares
its own `CohesionAnalyzerReference`, as the Generation and Integration test projects do.

Alternatives rejected:

- **Carry it in `OpenApi.Generation`.** Generation consumes the registry, but the generated code does
  not reference Generation. An assembly that only declares annotated endpoints references Attributes and
  has no reason to reference Generation, so its annotations would compile with no registry.
- **A package of its own.** No analyzer in the repository ships alone (`analyzers/Directory.Build.props`
  sets `IsPackable=false`), and an opt-in generator package lets a project apply the attributes and
  silently produce no metadata.
- **A shared framework's `CohesionFrameworkAnalyzer` entry.** That is how `SourceGeneration.Web`
  reaches `Sdk.Web` applications, but OpenApi is in no framework (`App.Web` lists the root only as a
  commented-out candidate), and a targeting pack reaches only SDK consumers. If OpenApi joins `App.Web`,
  `resources/Web/Assimalign.Cohesion.Web.Runtime/Directory.Build.props` needs a
  `CohesionFrameworkAnalyzer` entry for this generator, because an assembly resolved from a framework
  brings no package analyzer with it.
- **Not shipping it.** The runtime mapper maps attribute instances it is handed, and obtaining them from
  an assembly takes `GetCustomAttributes` reflection, which the attribute design rules out for
  NativeAOT. Without the generator, the attribute path has no AOT-safe discovery.

## Non-goals

- Emitting the canonical model — that is the generation pipeline's job; the generator stops at metadata.
- Structural schema inference — a body schema is referenced by type name, matched to an
  `[OpenApiSchema]` model; the generator does not synthesize property schemas from arbitrary CLR types.
- Discovering attributes across referenced assemblies — the generator sees the compilation it runs in.
