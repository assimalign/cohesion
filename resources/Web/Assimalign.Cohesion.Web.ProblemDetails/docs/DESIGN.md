# Assimalign.Cohesion.Web.ProblemDetails — Design

`Assimalign.Cohesion.Web.ProblemDetails` is the RFC 9457 *Problem Details for HTTP APIs* payload
for the Cohesion Web pipeline: the `ProblemDetails` model, the AOT-safe
`application/problem+json` writer built directly on `Utf8JsonWriter`, and the
`WriteProblemDetailsAsync` response extension. It is deliberately a **payload library** — it
defines what an error response looks like on the wire, not when or whether one is produced.

> **History.** The model and writer were first reviewed on the abandoned #844 branch
> (issue #776), then shipped inside the `Web.Results` IResult foundation on PR #887 — which was
> subsequently re-scoped: the IResult contract, carriers, and `Results`/`TypedResults` factories
> were withdrawn before merge (2026-07-10) in favor of the middleware-first composition model
> (fluent `.Use(...)` / `IWebApplicationMiddleware`). The payload survived the re-scope and was
> renamed into this dedicated library. Issue #864 now owns the successor design: a
> content-serialization registry plus an `OnError` hook through which applications decide how
> faults become responses — with a default handler expected to render *this* payload.

## Design intent

Error payloads are framework vocabulary; error *policy* is application vocabulary. This library
owns only the former:

- `ProblemDetails` — the plain, mutable RFC 9457 model. No serialization attributes, no
  serializer coupling; the five standard members plus a constrained extensions bag.
- `ProblemDetailsWriter.Default` (`IProblemDetailsWriter`) — the single problem+json serializer
  in the framework. Consumers that need the payload (the `OnError` default handler in `Web.ErrorHandling`,
  status-code pages, any feature library shaping an error response) all flow through it.
- `response.WriteProblemDetailsAsync(problem)` — the imperative write path, consistent with the
  pipeline's middleware-first idiom: sets status (when the payload carries one), `Content-Type`,
  `Content-Length`, and writes the body.

## Why-this-not-that decisions

- **A hand-rolled `Utf8JsonWriter` walk, not a serializer.** The payload shape is closed and
  framework-owned, so an explicit member walk (following the `OpenApiJsonWriter` precedent) is
  trivially AOT- and trim-safe, needs no generated metadata, and must never fault — it is
  destined to run inside last-chance error handling. The extensions bag is rendered by a closed
  allow-list type switch (scalars, strings, string-keyed maps, sequences — recursively); unknown
  values degrade to `ToString()` rather than throwing, and keys colliding with the five standard
  members are skipped so a stray extension can never emit a duplicate property.
- **`type` is always emitted**, defaulting to the reserved `about:blank` (RFC 9457 §4.2); the
  other standard members are omitted when null. `ProblemDetails.FromStatus` fills the
  status-phrase title for the default type: `HttpStatusCode.ReasonPhrase`, the RFC 9110 phrase
  (`Content Too Large`, `Unprocessable Content`) since #1204. Since #1414, core accepts every
  code from 100 through 599 and gives an unregistered code an empty reason phrase. This library
  retains its generic title fallback: `Internal Server Error` for 500–599, and `Error` for
  other codes without a phrase. `IsValid` tests the numeric range, so title resolution separately
  checks whether a phrase exists. Explicit application titles are serialized unchanged.
- **No result carriers, no factories, no middleware.** Earlier iterations wrapped this payload in
  a `ProblemHttpResult` (IResult) or an exception-boundary middleware. Both were deliberately
  removed: how an application responds to a fault is the application's decision, made through the
  #864 `OnError` hook design — this library must stay consumable by *any* error policy without
  imposing one.
- **Namespace deviation (documented in the csproj).** Types surface in `Assimalign.Cohesion.Web`
  rather than the assembly-matching namespace, because a namespace named `…Web.ProblemDetails`
  would collide with the `ProblemDetails` type itself (CS0434-class ambiguity), and the write
  extension should be discoverable with the pipeline's `using`. Matches the
  `Assimalign.Cohesion.Http.*` feature-package precedent. The test project flattens its namespace
  segment (`…Web.ProblemDetailsTests`) for the same reason.

## Error model

The library throws only argument-validation exceptions. `WriteProblemDetailsAsync` sets the
response status from `ProblemDetails.Status` when present and otherwise leaves the status
untouched — payload and status stay the caller's decision to reconcile; the writer never invents
one.

### Unregistered status codes (#1414)

`ProblemDetails.FromStatus(new HttpStatusCode(499))` produces `status: 499` with title `Error`;
599 produces `status: 599` with title `Internal Server Error`. `WriteProblemDetailsAsync` writes
the same numeric response status and JSON status. `ProblemDetails.Status` checks the canonical
`HttpStatusCode.IsValid` range when assigned, before any serializer can observe an invalid code.
The serializer remains a plain model writer; it does not replace the caller's `Status` or `Title`.

**Breaking behavior from 10.0.0-preview.1 (decision 15):** constructing an unregistered
`HttpStatusCode`, or writing a problem with that integer status onto a response, used to throw.
Those operations now succeed within 100–599. Registered titles and explicit application titles
retain their behavior; the generic fallback prevents the newly accepted codes from acquiring an
empty default title.

### Composition validation (#1477)

The mutable model preserves its `int? Status` and `string? Type` / `Instance` signatures. A non-null
status outside 100–599 throws `ArgumentOutOfRangeException` from its setter, using the existing
`HttpStatusCode.IsValid` rule. `FromStatus(default)` therefore throws `ArgumentOutOfRangeException`
while constructing the payload; its public XML documentation records this exception contract.
The response helper cannot serialize an invalid status and then fail converting it for the response.

`Type` and `Instance` reject malformed RFC 3986 URI references with `ArgumentException` in their
setters. Absolute URIs, relative paths, query-only and fragment-only references, and the empty
reference are accepted without changing their spelling. Bare non-ASCII IRI text, controls,
whitespace, backslashes, malformed escapes and malformed authorities are refused. An internal
span-based grammar check validates the original text without resolution or scheme-specific DNS and
port restrictions. It accepts generic registered-name characters, IPv6 and IPvFuture literals, and
ports consisting of arbitrary digits as RFC 3986 defines them. Error text comes from this
package's generated `ErrorMessages` resources. No public type or member is added.

**Breaking behavior from 10.0.0-preview.1 (decision 15):** assignments of invalid status codes or
malformed type/instance references now throw immediately. The released model accepted all three
unchecked; its response helper serialized an invalid status before conversion failed. Null values
remain optional and all valid status codes and URI references retain their existing representation.

## AOT posture

`IsAotCompatible=true` with zero concessions: no reflection, no runtime code generation, no
serializer registries. The writer's type switch is a closed allow-list.

## Non-goals

- **Error policy** — when a fault becomes a response, and in what shape, belongs to the #864
  `OnError` hook design and its overridable default handler.
- **Exception boundaries / status-code pages / 404 terminals** — #881's re-scoped concern,
  composed by the application over the hook.
- **General response serialization** — open-DTO formatting belongs to the #864
  content-serialization registry, not here.
- **RFC 9457 `application/problem+xml`** — JSON only until a consumer demands otherwise.
