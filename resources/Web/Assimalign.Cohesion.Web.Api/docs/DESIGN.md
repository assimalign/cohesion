# Assimalign.Cohesion.Web.Api Design

## Design Intent

`Web.Api` is the endpoint-mapping surface over `Web.Routing`. It carries two families of `Map*`
overloads plus the compile-time and runtime support for AOT-safe typed-delegate parameter binding.
Binding is delivered by an interceptor-style Roslyn source generator (`Assimalign.Cohesion.SourceGeneration.Web`)
so the framework never reflects over handler signatures or compiles expressions at run time — the
standing NativeAOT requirement.

This package is the concrete, middleware-first delivery of the source-generated binding tracked by
issue #796. The originally-filed scope (result unions, `Web.Results`, `Web.Functions`,
`IEndpointFilter`) predates the 2026-07-10 middleware-first direction and does not apply: handlers
write responses imperatively (directly, or through `Web.Serialization`'s `WriteContentAsync`), and
there are no result types.

## Two Mapping Families

- **`WebApplicationMiddleware` overloads** (`Map(method, pattern, WebApplicationMiddleware)`,
  `MapGet(pattern, WebApplicationMiddleware)`)
  register a terminal endpoint verbatim. No binding happens; a handler whose only parameter is
  `IHttpContext` binds here by ordinary overload resolution (a specific delegate type beats
  `System.Delegate`).
- **`Delegate` overloads** (`Map`, `MapGet`, `MapPost`, `MapPut`, `MapPatch`, `MapDelete`) accept a typed handler lambda
  such as `(int id, IHttpContext context) => ...`. Their bodies **throw** `NotSupportedException`:
  they are placeholders the generator rewrites. Reaching one at run time means the generator was not
  wired in (missing `CohesionAnalyzerReference` or `InterceptorsNamespaces` allow-list).

All `Map*` overloads compose on the router: they resolve the `IRouterFeature` and register a `Route`,
so an application still calls `AddRouting()` (builder) and `UseRouting()` (pipeline) exactly as it
does for the raw router surface.

## The Source Generator

`EndpointBindingGenerator` (an `IIncrementalGenerator` in `analyzers/`, netstandard2.0 — the sanctioned
non-AOT build component) intercepts each typed `Map*` call site with a C# interceptor
(`InterceptableLocation` / `[InterceptsLocation]`). The emitted interceptor:

1. Casts the `Delegate` back to the handler's exact inferred delegate type (`Func<...>`/`Action<...>`)
   and invokes it directly — no reflection, no `Expression.Compile`.
2. Registers a generated `WebApplicationMiddleware` thunk through the raw `Map` overload.
3. Emits inline, AOT-safe binding for each parameter, then the failure short-circuits, then the
   direct handler call.

Interceptors are emitted into `Assimalign.Cohesion.Web.Api.Generated`; consumers allow-list that
namespace with `<InterceptorsNamespaces>`. The generator is delivered two ways: in-repo/test projects
via `<CohesionAnalyzerReference Include="Assimalign.Cohesion.SourceGeneration.Web" />`, and to Sdk.Web
consumers via the `CohesionFrameworkAnalyzer` entry in `App.props` (bundled under `analyzers/dotnet/cs/`
in `App.Web.Ref`). See the generator's own `docs/DESIGN.md` for emission internals.

## Binding Sources and Inference

Each handler parameter is classified once, at compile time:

1. **Direct injections** take precedence: `IHttpContext` → the context; `CancellationToken` →
   `context.RequestCancelled`; any type implementing `IHttpFeature` → `context.Features.Get<T>()`.
2. **Explicit attributes** override the source: `[FromRoute]`, `[FromQuery]`, `[FromHeader]`,
   `[FromBody]`, `[FromForm]` (each with an optional `Name`, except `[FromBody]`).
3. **Convention** otherwise: a name matching a `{token}` in a literal route pattern → route;
   a scalar type (`string`, `IParsable<T>` primitives, enums, and their `Nullable<>` forms) → query;
   a complex type → body.

Scalars convert inline with `IParsable<T>.TryParse(..., CultureInfo.InvariantCulture, ...)` (enums via
`Enum.TryParse<T>`), so no runtime binder or reflection is needed. Route values arrive as `object?`
(a boxed CLR value under a typed constraint such as `{id:int}`, or a string otherwise); the thunk uses
the boxed value directly when the runtime type matches and parses its invariant string form otherwise.
Non-nullable scalars are required; nullable/reference-nullable parameters are optional. Bodies are read
through `Web.Serialization`'s `ReadContentAsync<T>`; at most one body parameter is allowed and body and
form binding are mutually exclusive. Handlers may return `Task`, `ValueTask`, or `void`.

## Failure Semantics

Failures are outcomes the thunk writes imperatively as RFC 9457 `application/problem+json` (via
`Web.ProblemDetails`), never faults:

| Condition | Status | Payload |
| --- | --- | --- |
| Unparseable/missing-required route, query, header, or form scalar | 400 | `errors` extension keyed by the parameter |
| Request has no reader for its Content-Type (or none registered) | 415 | problem+json |
| `HttpContentSerializationException` while reading the body | 415 | problem+json |
| `System.Text.Json.JsonException` while deserializing the body | 400 | problem+json |

Exceptions thrown by the **handler itself** are never caught — they propagate to the pipeline
exception boundary (#881).

## Validation — descoped (owner decision, 2026-07-20)

An opt-in per-endpoint validation seam (`IValidator`-carrying `Map*` overloads + an
`EndpointValidationMetadata` carrier threading an `Assimalign.Cohesion.ObjectValidation` validator
into the thunk) was implemented on the #796 branch and **removed before merge** — the owner descoped
request validation from this package entirely, so `Web.Api` carries no `ObjectValidation`
dependency. The `ObjectValidation` AOT hardening done alongside it was kept (it stands on its own).
A future validation integration is an open design question, not a v1 feature.

## Homing Rationale

Everything ships from `Web.Api` because the typed overloads are additional overloads of the same
`Map*` methods that already live here — splitting them into a new package would put two overloads of
`MapGet` in two packages. `Web.Api` gains a reference to `Web.ProblemDetails` (failure rendering),
already in the `App`/`App.Web` framework closure, so no manifest assembly was added. The binding
attributes live here rather than in the `Web` root, per the feature-contract packaging discipline.

## Non-Goals (v1)

- Request validation (descoped by owner decision — see the section above).
- Result types or typed result unions of any kind (middleware-first; handlers write responses).
- Filter/interceptor chains around handlers (a natural follow-up seam, not built).
- OpenApi surfacing (#555 consumes the endpoint metadata later).
- Content negotiation beyond what `WriteContentAsync` already offers handlers.
- Whole-object binding from form fields (form binding is per-field scalar via `[FromForm]`).
- `Task<T>`/`ValueTask<T>` (result-shaped) handler returns.
- Compile-time diagnostics for unsupported handler shapes: an unmodelable call site is left to the
  throwing placeholder overload rather than reported as a diagnostic.
