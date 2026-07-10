# Assimalign.Cohesion.Web.Results — Design

`Assimalign.Cohesion.Web.Results` owns the Web pipeline's **general result abstraction**: the
public `IResult` contract, the v1 built-in result set (status, no-content/empty, text/content,
source-generated JSON, files, redirects, RFC 9457 ProblemDetails, push streaming), and the
plain-static `Results` / `TypedResults` factories. It is the fan-out seam that the exception
boundary (#881), negotiated results (#149), the precondition-aware file result (#777),
source-generated endpoint binding (#796), and controller/function execution (#151) all build on.

> **Supersedes** the ProblemDetails-only design shipped on the abandoned PR #844 branch
> (issue #776). That branch's `ProblemDetails` model, hand-rolled writer, and
> `WriteProblemDetailsAsync` extension were salvaged into this package essentially verbatim — but
> the exception-boundary middleware, status-code pages, `IHttpExceptionFeature`, and the 404
> terminal it also carried are **not here**: they are re-expressed over `IResult` in #881, which
> consumes this package's `Results.Problem`. The prior DESIGN.md's middleware-lane reasoning moves
> with them.

## Design intent

A result is a **deferred response**: a value a handler returns that knows how to write itself onto
the exchange when the pipeline executes it. This inverts the imperative
"handler writes `context.Response` inline" idiom into "handler returns a value; the pipeline
executes it" — the shape that return-type-based endpoint metadata (OpenApi), source-generated
binding, and uniform error handling all require. The contract is one method:

```csharp
public interface IResult
{
    Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default);
}
```

It mirrors the existing handler idiom (`IRouterRouteHandler.InvokeAsync`,
`WebApplicationMiddleware`): a result executes against the **bare exchange** —
`context.Response` plus the typed features on `context.Features` — never against an injected
provider. `IHttpContext` has no `IServiceProvider`, so "no request-time DI" is structural, not a
convention. Everything a result needs is captured as constructor state at creation time.

## Why-this-not-that decisions

- **One interface, not an executor registry.** ASP.NET Core ships `IResult` *plus* an
  `IResultExecutor<T>` DI indirection so results can be swapped at request time. Rejected: the
  indirection exists to serve request-time DI, which this pipeline structurally lacks; a result
  here is self-executing and the composition seam is *which factory you call*, decided at
  builder/handler-authoring time. This is the package's expression of the repo-wide
  **builder-time-only** posture: composition happens when the application is built, execution is a
  pure write against the exchange.
- **Factories, not public constructors.** Every concrete result has an `internal` constructor and
  is reachable only through `Results` (returns `IResult`) or `TypedResults` (returns the concrete
  carrier). The factory surface is the one place future slices (#149 negotiation, #777 file
  preconditions) extend, so consumers never bind to construction details.
- **Public concrete carriers (documented deviation).** The repo rule is interface-first with
  internal implementations. The carrier types (`StatusCodeHttpResult`, `OkHttpResult<T>`, …) are
  deliberately `public sealed` because `TypedResults` must return them for endpoint return-type
  inference and for OpenApi metadata to read carrier properties. The deviation is scoped to the
  carriers; constructors stay internal, and every other implementation type
  (`HttpResultWriter`, `ProblemDetailsJsonWriter`, defaults) is `internal`. Recorded at the entry
  point in `TypedResults.cs`.
- **Static extension members are the cross-package factory graft.** Sibling packages add
  factories to `Results`/`TypedResults` via C# 14 `extension(Results)` static members instead of
  defining their own factory classes — `Assimalign.Cohesion.Web.Results.ServerSentEvents` is the
  first consumer (`Results.ServerSentEvents(...)`). One factory vocabulary, lean per-package
  dependency trees.
- **No result filters, no view results, no ambient `JsonOptions`.** Filters re-introduce a
  request-time interception chain the interceptor seam (#875) already owns at the HTTP layer; view
  rendering is out of scope for the resource; ambient serializer options would silently couple
  results to global mutable state and undermine the explicit `JsonTypeInfo<T>` contract below.

## The serialization rule: hand-rolled writer vs `JsonTypeInfo<T>`

Two JSON write paths coexist, and the boundary between them is a rule, not an accident:

| Payload kind | Path | Why |
| --- | --- | --- |
| **Fixed framework payloads** — shapes the framework itself defines (`ProblemDetails`; the OpenApi document writer follows the same precedent) | Hand-rolled `Utf8JsonWriter` walk (`ProblemDetailsJsonWriter`) | The shape is closed and owned here, so an explicit member walk is trivially AOT-safe, needs no generated metadata, and must never fault (it runs inside the #881 last-chance boundary). |
| **Open DTOs** — application types the framework has never seen (`Json<T>`, `Ok<T>`, `WriteJsonAsync<T>`) | `JsonSerializer.Serialize(Utf8JsonWriter, value, JsonTypeInfo<T>)` with **caller-supplied** source-generated metadata | The framework cannot hand-roll a writer for a type it doesn't know, and reflection serialization is banned repo-wide. The endpoint author supplies the `JsonTypeInfo<T>` from their `JsonSerializerContext` — the repo's first System.Text.Json source-gen use, deliberately scoped to the Web layer. |

There is exactly **one** problem+json serializer in the framework: `ProblemDetailsWriter.Default`.
`ProblemHttpResult`, the imperative `WriteProblemDetailsAsync`, and #881's boundary all flow
through it.

The `ProblemDetails` extension bag is constrained to an AOT-safe, JSON-shaped set rendered by a
closed allow-list type switch (scalars, strings, string-keyed maps, sequences — recursively);
anything else degrades to `ToString()` rather than throwing, and extension keys that collide with
the five standard members are skipped so a stray extension can never emit a duplicate property.
(Carried unchanged from the #844 review.)

## Execution semantics

- **Buffered results** (`Text`/`Content`, `Json<T>`/`Ok<T>`, `File` family, `Problem`) materialize
  the payload, set `Content-Type` and an exact `Content-Length`, and write once to
  `Response.Body`. The transport never needs to chunk them.
- **Streaming results** (`PushStream`; `ServerSentEvents` in the adapter package) resolve
  `IHttpResponseStreamingFeature` (#769) from `context.Features` and **fail loudly** with
  `NotSupportedException` when streaming is not enabled — they never silently buffer, and they
  **never set `Content-Length`** (framing is the transport's concern: h1 chunking, h2/h3 DATA
  frames). Status code and `Content-Type` are applied *before* the first write because the
  response head commits and locks at that moment. Both complete the response via `CompleteAsync`.
- **Status-only results** (`StatusCode`, `NoContent`, `Redirect`) mutate status/headers and write
  no body. `Empty` writes nothing at all — it exists so a handler that already wrote imperatively
  can still satisfy an `IResult` return contract.
- **Cancellation**: `context.ExecuteResultAsync(result)` defaults an omitted token to
  `context.RequestCancelled`, so a result write never outlives its request; passing an explicit
  token overrides (typically a linked, tighter token).
- **Reuse**: built-ins carry immutable state and may execute on many exchanges. The two exceptions
  are stream-backed: `FileStreamHttpResult` owns and disposes its stream (single-use), and an SSE
  result is only as re-enumerable as its `IAsyncEnumerable`.

## Normalization in `Problem`

`ProblemHttpResult` normalizes at construction: `Status` defaults to `500`, and a missing `Title`
is filled with the status phrase **only** for the reserved `about:blank` type (RFC 9457 §4.2) — a
custom problem type owns its own vocabulary and is left alone. The status code on the wire always
comes from the payload, so body and status can never disagree.

## The negotiation boundary (deferred to #149)

`Ok<T>` is **JSON-only** in this foundation. It does not read the request's `Accept` header, and
there is no `IResultFormatter` registry. That is a deliberate seam, not an omission: the
re-scoped #149 layers negotiated `ObjectResult`/`Ok<T>` behavior over the merged #771
`HttpMediaType`/Accept primitives, arriving as a new composition **behind the same factory
methods** — the conservative surface here is exactly what lets #149 land without a breaking
change. Nothing else in this package consumes #771, so the package is not blocked on
negotiation work.

Likewise deferred: the Range/If-Range/precondition-aware file result belongs to #777 (which
composes this package with the #792 range primitives); the file built-ins here are unconditional
and surface I/O errors as exceptions (mapping filesystem state to status codes is a static-file
middleware concern, also #777's).

## Family map

| Package | Role | Depends on |
| --- | --- | --- |
| `Assimalign.Cohesion.Web.Results` (this) | `IResult`, built-ins, factories, ProblemDetails + writer, execution glue | `Assimalign.Cohesion.Http`, `Assimalign.Cohesion.Http.Streaming` |
| `Assimalign.Cohesion.Web.Results.ServerSentEvents` | The SSE result + `Results.ServerSentEvents` factory graft | this + `Assimalign.Cohesion.Http.ServerSentEvents` |

The core package deliberately references **only** the HTTP protocol core and the streaming feature
package — not `Assimalign.Cohesion.Web`. `IResult` executes against `IHttpContext`, which lives in
the protocol core; nothing in the result set needs the Web composition surface. The SSE result is
homed in its own adapter package (the issue's "next to `Http.ServerSentEvents`") for two reasons:
it keeps the core package's dependency tree lean (only event-streaming apps pull the SSE
primitives), and the layering only works this direction — an L1 `libraries/Http` package can never
reference an L3 `resources/Web` package, so the adapter must live on the Web side and bridge the
two.

## AOT posture

`IsAotCompatible=true` with zero concessions: no reflection serialization (the two-path rule
above), no runtime code generation, no service location. Point-in-time evidence (2026-07-10):
`examples/Assimalign.Cohesion.Web.Results.Examples.AotJson` — a real router + `MapGet` endpoint
returning `Results.Ok<T>` and `Results.Problem` over the real HTTP/1.1 transport — published with
`PublishAot=true` for `win-arm64` with **zero trim/AOT warnings** (IL2026/IL2070/IL2072/IL2075/
IL2087/IL3050/IL3051 promoted to errors) and ran self-verifying: correct source-generated JSON and
problem+json from the native binary. Publish recipe notes live in the example's csproj comment.

## Non-goals

- **Result filters / `IResultExecutor<T>` DI indirection** — see the decisions above; re-litigate
  only if request-time DI ever becomes structural (it should not).
- **View/HTML rendering results** — out of scope for the Web resource.
- **Ambient `JsonOptions`** — serializer configuration travels with the call site
  (`JsonTypeInfo<T>` argument), never through global state.
- **Content negotiation** — #149's, behind the same factories.
- **Range/precondition file semantics and filesystem-to-status mapping** — #777's.
- **Exception boundary / status-code pages / 404 terminal** — #881's, built *on* `Results.Problem`.
