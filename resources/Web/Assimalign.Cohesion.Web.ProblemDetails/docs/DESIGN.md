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
  in the framework. Consumers that need the payload (the future `OnError` default handler,
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
  status-phrase title for the default type.
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
