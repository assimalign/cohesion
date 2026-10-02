# Assimalign.Cohesion.Web.Api Design

## Design Intent

`Web.Api` is the endpoint-mapping surface over `Web.Routing`. It carries two families of `Map*`
overloads plus the compile-time and runtime support for AOT-safe typed-delegate parameter binding.
Binding is delivered by an interceptor-style Roslyn source generator (`Assimalign.Cohesion.SourceGeneration.Web`)
so the framework never reflects over handler signatures or compiles expressions at run time — the
standing NativeAOT requirement.

This package is the concrete, middleware-first delivery of the source-generated binding tracked by
issue #796. The originally-filed scope (result unions, `Web.Results`, `Web.Functions`,
`IEndpointFilter`) predates the 2026-07-10 middleware-first direction and does not apply: there are
no result types. A handler either writes the response imperatively (directly, or through
`Web.Serialization`'s `WriteContentAsync`), or returns a plain value that the generated thunk writes
for it (#1059, see "Return Values").

## Two Mapping Families

- **`WebApplicationMiddleware` overloads** (`Map(method, pattern, WebApplicationMiddleware)`,
  `MapGet(pattern, WebApplicationMiddleware)`)
  register a terminal endpoint verbatim. No binding happens; a handler whose only parameter is
  `IHttpContext` binds here by ordinary overload resolution (a specific delegate type beats
  `System.Delegate`).
- **`Delegate` overloads** (`Map`, `MapGet`, `MapPost`, `MapPut`, `MapPatch`, `MapDelete`) accept a typed
  handler: a lambda such as `(int id, IHttpContext context) => ...` or `(long id) => orders.Get(id)`,
  or a method group. Their bodies **throw** `NotSupportedException`: they are placeholders the
  generator rewrites. A handler the generator cannot rewrite is a `COHWEB` compile error (see
  "Compile-Time Diagnostics"), so reaching a placeholder at run time means only that the generator was
  not wired in (missing `CohesionAnalyzerReference` or `InterceptorsNamespaces` allow-list).

All `Map*` overloads compose on the router: they resolve the `IRouterFeature` and register a `Route`,
so an application still calls `AddRouting()` (builder) and `UseRouting()` (pipeline) exactly as it
does for the raw router surface.

**Every `Map*` returns the mapped route's `IRouterRouteBuilder` (#1055).** Per-endpoint policies
attach where the endpoint is mapped, for example
`app.MapGet("/orders/{id:int}", handler).WithName("order").RequireRateLimiting("api")`. Metadata
composes when the route table is built (Web.Routing DESIGN, "Endpoint convention builders"). Before
#1055 the verbs returned the pipeline builder, and a typed endpoint could carry no metadata at all.

**Fallback (#1056).** `app.MapFallback(middleware)` and `app.MapFallback(pattern, middleware)` map
Web.Routing's fallback route (lowest precedence, `GET`/`HEAD`, never a file-name path, never a 405). For
a single-page application use `MapFallbackToFile` in `Web.StaticFiles`.

**Groups hold typed endpoints.** `app.MapGroup(prefix)` returns the router's `IRouterGroupBuilder`.
`RouterGroupBuilderEndpointExtensions` gives it the same two families: the raw middleware
overloads, and the `Delegate` placeholders the generator intercepts. `api.MapGet("orders/{id:int}",
(int id) => ...)` therefore binds exactly like an application endpoint, and the group's prefix,
metadata and policies compose onto it.

## The Source Generator

`EndpointBindingGenerator` (an `IIncrementalGenerator` in `analyzers/`, netstandard2.0 — the sanctioned
non-AOT build component) intercepts each typed `Map*` call site with a C# interceptor
(`InterceptableLocation` / `[InterceptsLocation]`). The emitted interceptor:

1. Casts the `Delegate` back to the handler's exact delegate type (its inferred `Func<...>`/`Action<...>`)
   and invokes it directly — no reflection, no `Expression.Compile`.
2. Registers a generated `WebApplicationMiddleware` thunk through the raw `Map` overload.
3. Emits inline, AOT-safe binding for each parameter, then the failure short-circuits, then the
   direct handler call.
4. Writes the value the handler returned, if any (see "Return Values").
5. Attaches the endpoint's description — its request-bound parameters and its responses — to the route
   it maps (see "Endpoint Description Metadata").

The generator reads the call site through the compiler's operation tree, so a method group
(`app.MapGet("/orders/{id}", GetOrder)`) binds exactly like a lambda, and named arguments in any order
resolve. Before #1059 a method group was silently left to the throwing placeholder.

Interceptors are emitted into `Assimalign.Cohesion.Web.Api.Generated`; consumers allow-list that
namespace with `<InterceptorsNamespaces>`. The generator is delivered two ways: in-repo/test projects
via `<CohesionAnalyzerReference Include="Assimalign.Cohesion.SourceGeneration.Web" />`, and to Sdk.Web
consumers via the `CohesionFrameworkAnalyzer` entry in the `App.Web` member list,
`resources/Web/Assimalign.Cohesion.Web.Runtime/Directory.Build.props` (bundled under
`analyzers/dotnet/cs/` in `App.Web.Ref`). See the generator's own `docs/DESIGN.md` for emission internals.

## Binding Sources and Inference

Each handler parameter is classified once, at compile time:

1. **Direct injections** take precedence: `IHttpContext` → the context; `CancellationToken` →
   `context.RequestCancelled`; any type implementing `IHttpFeature` → `context.Features.Get<T>()`.
2. **Explicit attributes** override the source: `[FromRoute]`, `[FromQuery]`, `[FromHeader]`,
   `[FromBody]`, `[FromForm]` (each with an optional `Name`, except `[FromBody]`).
3. **Convention** otherwise: a name matching a `{token}` in a literal route pattern → route;
   a scalar type (`string`, `IParsable<T>` primitives, enums, and their `Nullable<>` forms) → query;
   a complex type → body. **Route-or-query** replaces query for a scalar the call site cannot place
   (#1055). That happens in two cases: a route-group endpoint, whose group prefix is declared
   elsewhere (`MapGroup("api/{tenant}")` + `api.MapGet("orders", (string tenant) => ...)`), and a
   pattern that is not a string literal. The thunk reads the route value when the matched route
   captured one, and the query string otherwise.

Scalars convert inline with `IParsable<T>.TryParse(..., CultureInfo.InvariantCulture, ...)` (enums via
`Enum.TryParse<T>`), so no runtime binder or reflection is needed. Route values arrive as `object?`
(a boxed CLR value under a typed constraint such as `{id:int}`, or a string otherwise); the thunk uses
the boxed value directly when the runtime type matches and parses its invariant string form otherwise.
Non-nullable scalars are required; nullable/reference-nullable parameters are optional. Bodies are read
through `Web.Serialization`'s `ReadContentAsync<T>`; at most one body parameter is allowed (COHWEB0004)
and body and form binding are mutually exclusive (COHWEB0005).

## Return Values (#1059)

A typed handler may return a plain value. The thunk awaits it when the handler is asynchronous and
writes it as the response, so `app.MapGet("/orders/{id}", (long id) => orders.Get(id))` needs no
`IHttpContext`:

| Handler returns | The thunk |
| --- | --- |
| `void`, `Task`, `ValueTask` | Awaits the handler when it is asynchronous and writes nothing: the handler writes the response, as before |
| `string` (directly, or through `Task<string>`/`ValueTask<string>`) | Writes the text as UTF-8 under `Content-Type: text/plain; charset=utf-8`, or under the `Content-Type` the handler set |
| Any other `T`, `Task<T>` or `ValueTask<T>` | Writes the value through `Web.Serialization`'s `WriteNegotiatedContentAsync<T>` |
| `null` (a reference type, or an empty `Nullable<T>`) | Writes no body and consults no serializer; the status becomes `204 No Content` unless the handler set another |

The thunk's path from binding to the written response:

```mermaid
flowchart TD
    Bind["Bind parameters"] -->|"binding failure"| Problem["400 or 415 problem+json"]
    Bind -->|"body: no registry or no contract"| Fault
    Bind --> Invoke["Invoke the handler, awaiting Task or ValueTask"]
    Invoke -->|"void, Task, ValueTask"| Done["The handler wrote the response"]
    Invoke -->|"null"| NoContent["No body; 204 unless the handler set a status"]
    Invoke -->|"string"| Text["UTF-8 text, text/plain unless the handler set a Content-Type"]
    Invoke -->|"any other value"| Negotiate["WriteNegotiatedContentAsync"]
    Negotiate -->|"acceptable writer"| Written["Serialized body, Vary: Accept"]
    Negotiate -->|"nothing acceptable"| NotAcceptable["Bodyless 406, Vary: Accept"]
    Negotiate -->|"no registry or no contract"| Fault["HttpContentSerializationException to the exception boundary"]
```

The rules, and why:

- **No result types.** The non-goal stands: there is no `IResult` and no typed result union. A returned
  value is data, written one way. A handler that needs control of the response sets the status or
  headers on `IHttpContext` and either writes the body itself and returns `Task`, or returns the value
  and lets the thunk write it under the status it set.
- **Status 200 by default; a status the handler set wins.** The thunk never sets a status for a
  written value. The response starts at 200, so a value is a 200 unless the handler chose another code
  first: `context.Response.StatusCode = HttpStatusCode.Created; return order;` answers 201 with the
  order as its body.
- **`null` is 204 No Content.** A null value is the absence of a representation. Serializing it would
  answer 200 with the body `null`, which a client cannot tell apart from a resource whose JSON is
  `null`; RFC 9110 §15.3.5 defines 204 for a response with no content to send. No serializer is
  consulted, so `null` needs no registry and no contract. When the handler already chose a status (404
  for a missing resource) the thunk keeps it and writes no body; only the default 200 becomes 204. A
  `Nullable<T>` that has a value is written as its underlying `T`, so the contract registered for `int`
  serves an `int?` handler.
- **`string` is text/plain.** Through the JSON writer, `"pong"` would be written as a JSON string,
  quotes included. The thunk writes the text as UTF-8 and sets `text/plain; charset=utf-8` unless the
  handler set a `Content-Type` of its own (a handler returning an HTML fragment sets `text/html`); a
  handler that names another charset takes responsibility for it, because the bytes are always UTF-8.
  Text needs no serialization registry, and it does not vary by `Accept`: RFC 9110 §12.5.1 lets a
  server disregard `Accept` for a resource with one representation, so a string endpoint never answers
  406.
- **Any other value is negotiated** (RFC 9110 §12, server-driven negotiation). `WriteNegotiatedContentAsync<T>`
  picks the registered writer for the request's `Accept` (q-value, specificity, then registration
  order, with the structured-suffix fallback in the Web.Serialization DESIGN), appends `Vary: Accept`,
  and serializes the value as the declared type `T`, not its runtime type. When nothing the registry
  offers is acceptable, the response is a bodyless `406 Not Acceptable`, which the status-code-pages
  middleware can explain. That is an outcome, not an exception.
- **No contract for `T` is a fault, not an outcome.** Under NativeAOT the JSON writer has no
  reflection fallback: it serializes only types the application's source-generated resolver covers
  (`AddJsonSerialization(AppJsonContext.Default)` with `[JsonSerializable(typeof(Order))]`). A returned
  type the negotiated writer has no contract for, or an application with no serialization registry at
  all, throws `HttpContentSerializationException`. The thunk does not catch it: like any handler
  exception it reaches the pipeline exception boundary (#881) and becomes a 500, because it is a
  composition error the developer fixes, not a request the client can correct. The built-in JSON
  writer resolves the contract before it touches the response, so the failed response carries no
  partial body and no `Content-Type`. The generator cannot check coverage at compile time, because the
  registered resolver is a run-time choice and may come from another assembly.
- **Streams are not values.** A handler that returns a `Stream` is a compile error (COHWEB0002): copy
  the stream to `context.Response.Body` and return `Task`. File and stream response helpers arrive
  with #1061. A `byte[]` is an ordinary value, serialized as base64 JSON, not a raw body.

## Failure Semantics

Binding failures are outcomes the thunk writes imperatively as RFC 9457 `application/problem+json`
(via `Web.ProblemDetails`), never faults, and the handler does not run:

| Condition | Status | Payload |
| --- | --- | --- |
| Unparseable/missing-required route, query, header, or form scalar | 400 | `errors` extension keyed by the parameter |
| The request carries no parseable Content-Type, or the registry has no reader for it (an empty registry included) | 415 | problem+json |
| `System.Text.Json.JsonException` while deserializing the body | 400 | `errors` extension keyed `$body` |

Reading a body and writing a returned value draw the same line between the client's errors and the
server's (#1173). Both have outcomes the thunk answers and faults it never catches:

| Condition | Result |
| --- | --- |
| No registered writer satisfies the request's `Accept` | `406 Not Acceptable` with no body and `Vary: Accept`, an outcome the status-code-pages middleware can explain |
| No serialization registry is composed, and the endpoint reads a body or returns a negotiated value | `HttpContentSerializationException`, propagated as a fault |
| The reader for the request's Content-Type, or the negotiated writer, has no contract for the type | `HttpContentSerializationException`, propagated as a fault |

The thunk decides the 415 with the registry's non-throwing lookup (`GetReader`) before it reads, so
an `HttpContentSerializationException` from the read itself is never the client's doing: it means the
application registered no serialization at all, or its source-generated resolver does not cover the
parameter's type. Before #1173 the read path caught that exception and answered 415, which told the
client it had sent the wrong media type when the server was misconfigured. The distinction needed no
change in `Web.Serialization`: its lookup surface already separates "no reader for this media type"
(`GetReader` returns `null`) from "no contract for this type" (`IHttpContentReader.CanRead` is
`false`, and the read throws), per its faults-vs-outcomes model. An empty registry stays a 415 because
the registry then truthfully accepts no media type; a missing registry is a composition error, as it
is for a negotiated write.

Exceptions thrown by the **handler itself** are never caught — they propagate to the pipeline
exception boundary (#881), as do the serialization faults above.

## Compile-Time Diagnostics (#1059)

A typed call site the generator cannot rewrite is a compile error. Before #1059 the generator skipped
such a call site without a word, the call bound to the placeholder overload, and the application threw
`NotSupportedException` when it mapped the endpoint. Each `COHWEB` diagnostic is an error, names the
endpoint, says what is unsupported and what to write instead, and points at the handler (a lambda's
parameter list and arrow, or the method group) or at the offending lambda parameter:

| ID | Reported when | What to write instead |
| --- | --- | --- |
| COHWEB0001 | The handler is a delegate instance (a `Func<...>` variable, a `Delegate`, a call that returns one), so its parameter names and attributes are not visible | A lambda or a method group |
| COHWEB0002 | The return type cannot be written: `async void`, a stream, an anonymous type, a ref struct, `dynamic`, a pointer, an awaitable other than `Task`/`ValueTask` (or an awaited value that is itself awaitable), a by-reference return, a generic type parameter, or a private, protected or file-local type | What the message names: `async Task`, copying the stream to the body, a named record |
| COHWEB0003 | A parameter cannot be bound: a complex type from `[FromRoute]`/`[FromQuery]`/`[FromHeader]`/`[FromForm]`, a `ref`/`out`/`in` modifier, a default value or a `params` array (both give the handler a compiler-generated delegate type), a ref struct, `dynamic`, a pointer, a generic type parameter, or a type generated code cannot access | What the message names: `[FromBody]`, a nullable parameter in place of a default value |
| COHWEB0004 | More than one parameter binds from the request body | One body model; the other values from the route, query string or headers |
| COHWEB0005 | The handler binds a request body and form fields | Form fields only, or the model only |
| COHWEB0006 | The handler's delegate type cannot be named: more than 16 parameters, or an explicitly created delegate type that is private | A body model for the extra values; a lambda |
| COHWEB0007 | The endpoint reads a body or returns a negotiated value, but the compilation cannot name `Assimalign.Cohesion.Web.Serialization` | A reference to the package; `Sdk.Web` applications receive it through `App.Web` |

A call site with a diagnostic gets no interceptor; every other call site in the compilation is still
rewritten. The rules live in the generator (`Internal/EndpointBindingDiagnostics.cs`) and are
release-tracked in its `AnalyzerReleases.*.md` files.

## Endpoint Description Metadata (#152)

The generator is the only component that knows a typed endpoint's parameter and result types at
compile time, so it records them on the route for documentation adapters that must not reflect — the
OpenAPI adapter (#152) first. Every typed endpoint it maps carries, as route-level metadata:

- **One `EndpointParameterMetadata` per request-bound parameter**, in handler order: the `Name` the
  request supplies it under (the route parameter, query key, header or form-field name — an attribute's
  `Name` when one is given — and the handler parameter's name for a body), its
  `EndpointParameterSource` (`Route`, `RouteOrQuery`, `Query`, `Header`, `Form`, `Body`), its declared
  CLR `Type`, and `IsRequired`, which matches the 400 the thunk answers for a missing value (a body is
  always required). Injected parameters (`IHttpContext`, `CancellationToken`, features) are not request
  inputs and are not described.
- **`EndpointResponseMetadata` items**: a `200` whose `Type` is the written value's type (the `T` of
  `Task<T>`, `ValueTask<T>` or `Nullable<T>`, or `null` for a handler that writes its own response) and
  whose `ContentType` is `text/plain` for a string, or `null` when the serialization registry negotiates
  it; and a `204` with no type when the result may be `null`.

| Concern | Decision |
| --- | --- |
| Home | `Web.Api` (`src/Metadata/`, namespace `Assimalign.Cohesion.Web`), beside the `Map*` verbs that produce it. No OpenApi reference: the adapter maps these onto `OpenApiOperationMetadata` itself, so the dependency arrow stays OpenApi adapter → Web. |
| Shape | Sealed carriers with no interface, per the Web.Routing endpoint-metadata family rule. One carrier per concept: parameters and responses are read separately and compose separately. |
| Reading | `route.Metadata.GetOrderedMetadata<EndpointParameterMetadata>()` and `GetOrderedMetadata<EndpointResponseMetadata>()` on a built `IRouterRoute` — an adapter enumerates `IRouter.Routes` — or through `context.GetEndpointMetadata()` during a request. Responses are a set, so read them in order; a last-wins read returns whichever response was attached last. |
| Types | `typeof(...)` values the generator writes; nothing inspects members. An adapter produces a schema from the application's source-generated `JsonTypeInfo` for the type (System.Text.Json's `JsonSchemaExporter` over the resolver the application registered, which is NativeAOT-safe) and maps scalars such as `long` or `Guid` to primitive schemas. |
| `RouteOrQuery` | Described when the call site could not see the whole template (a group endpoint, a non-literal pattern). Resolve it against the built route's composed template, `IRouterRoute.Pattern`: a name the template contains is a path parameter, any other a query parameter. |
| The 204 | Listed when the compiler's nullability analysis says the result may be `null`: an annotated declared return (a method group's, or a lambda's explicit return type such as `Order? (long id) => ...`), a `Nullable<T>`, or — for an implicitly typed lambda, whose inferred return type is nullable-oblivious — a returned value whose null-state is maybe-null. Nullable-oblivious code lists no 204, though the thunk still answers 204 for a `null` at run time. |
| Not described | The outcomes the thunk produces on its own: 400 and 415 binding problems and the negotiated 406. An adapter adds them by policy (a required parameter can produce 400, a body 415, a negotiated response 406). |
| Extending | New sources are appended to `EndpointParameterSource` (file uploads arrive with #1061). An application describes further responses with `WithMetadata(new EndpointResponseMetadata(...))` on an endpoint or a group; they compose group items first, then the generated items, then the endpoint's own chain. |

#152 closed the one gap this left: an adapter needs the `JsonTypeInfo` the JSON writer serializes a
described type with, and `Web.Serialization` keeps the writer's options internal. `Web.Serialization`
now answers that question and no other, through
`IHttpContentSerializationFeature.TryGetJsonTypeInfo(type, out typeInfo)` (its DESIGN, "Contract
lookup"), so the description follows the wire's names and converters without the application passing
its context twice.

### Description verbs (#152)

The application curates the description with four convention verbs, shipped here as generic
`extension<TBuilder>(TBuilder builder) where TBuilder : IRouterConventionBuilder` members so they work on a
route and on a group alike, each attaching one sealed carrier from `src/Metadata/`:

| Verb | Carrier | Composition |
| --- | --- | --- |
| `WithTags(params string[])` | `EndpointTagsMetadata` | Every item applies, outer group first; an adapter lists each name once |
| `WithSummary(string)` | `EndpointSummaryMetadata` | Last wins, so a route's replaces its group's |
| `WithDescription(string)` | `EndpointDescriptionMetadata` | Last wins |
| `ExcludeFromDescription()` | `ExcludeFromDescriptionMetadata` | Absolute: present anywhere, the endpoint is not described |

They are format-neutral documentation metadata, like the generated parameter and response
descriptions, and they live here for the same reason: `Web.Api` is a member of the `App.Web` framework,
so a library that maps endpoints can describe them without referencing the OpenAPI adapter, which ships
as its own NuGet package (`Assimalign.Cohesion.Web.OpenApi`) together with the whole OpenApi family. An
application-declared `EndpointResponseMetadata` (`WithMetadata`) describes further responses; the
adapter treats any of these carriers as the application describing an endpoint, which is what brings a
raw middleware endpoint into the document.

## Antiforgery on form-bound endpoints (#1057)

A typed endpoint with a `[FromForm]` parameter requires antiforgery validation: the generator chains
`AntiforgeryMetadata.Required` onto the route it maps, but only when the consuming compilation
references `Assimalign.Cohesion.Web.Antiforgery` (every `Sdk.Web` application does, through `App.Web`).
`UseAntiforgery`, registered after `UseRouting`, validates the token and reads the form for the
form-token flow; the thunk then binds from that cached parse. Without `UseAntiforgery` such an endpoint
fails at dispatch rather than run unprotected; `.DisableAntiforgery()` on the endpoint opts it out. The
requirement is route-level metadata, so a group-level opt-out does not reach it. `Web.Api` takes no
reference to the package: the generator names the type and emits nothing when it does not resolve.

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
`Web.Api` takes no reference to `Web.Serialization`: the generated code in the application calls the
body reader and the negotiated writer, and COHWEB0007 reports an application that cannot name them.

## Non-Goals (v1)

- Request validation (descoped by owner decision — see the section above).
- Result types or typed result unions of any kind. A returned value is plain data the thunk writes
  (see "Return Values"); a handler that needs control of the response writes it.
- Filter/interceptor chains around handlers (a natural follow-up seam, not built).
- OpenAPI documents. `Web.Api` describes typed endpoints in neutral metadata (see "Endpoint Description
  Metadata"); the OpenAPI adapter and document endpoint (`Assimalign.Cohesion.Web.OpenApi`, #152) build
  on it, and `Web.Api` takes no OpenApi dependency.
- Content negotiation beyond `Web.Serialization`'s: returned values use `WriteNegotiatedContentAsync`,
  which negotiates media types only (no `Accept-Charset` or `Accept-Language`).
- Whole-object binding from form fields (form binding is per-field scalar via `[FromForm]`).
- Stream and file return values, and file binding (#1061).
