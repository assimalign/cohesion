# Assimalign.Cohesion.Web.Forms — Design

## Design intent

A single-purpose bridge that plugs HTTP form parsing
(`Assimalign.Cohesion.Http.Forms`) into the Web application middleware
pipeline (`Assimalign.Cohesion.Web`). It owns exactly one thing: the
`UseForms()` pipeline-builder extension. All of the parsing intelligence —
the streaming urlencoded/multipart readers, the limits, the spill-to-disk
buffering, the `IHttpFormFeature` seam — lives in `Http.Forms`. This project
is the resource-layer wiring, kept separate so that:

- the protocol-layer form model stays reusable outside the Web host, and
- a Web app opts into form parsing by adding this one package, without the
  Web runtime taking a hard dependency on a form model.

## What `UseForms()` does

`UseForms()` registers middleware that, per request:

1. Looks for an existing `IHttpFormFeature` in `context.Features`.
2. Installs a default `HttpFormFeature` over `context.Request` when none is
   present (so a middleware earlier in the pipeline can pre-install a custom
   feature and win).
3. Calls `ReadFormAsync(context.RequestCancelled)` to parse the body eagerly,
   then invokes the next middleware.

After it runs, downstream middleware reads `context.Request.Form`
synchronously — the parse has already happened and the result is cached on the
feature.

## Eager vs. lazy — a deliberate, revisitable choice

`UseForms()` parses **every** request that flows through it, regardless of
Content-Type. Non-form bodies (`application/json`, no body, etc.) yield an
empty collection cheaply because the feature short-circuits on an unrecognized
media type without draining the stream. The trade-off is that a request whose
form is never read still pays a small detection cost.

This is intentional for the common "forms app" shape where handlers expect
`request.Form` to be populated. Apps that only need forms on specific routes
should skip `UseForms()` and call `context.ReadFormAsync(...)` lazily inside
those handlers instead. A future `UseForms(options)` overload could add a
content-type predicate or a lazy mode; that is a additive API change, not a
redesign, and is deliberately not built until a consumer needs it.

## Boundaries

- **No parsing logic here.** If a form-parsing behavior needs changing, it
  changes in `Http.Forms`, not in this middleware.
- **No DI/logging/config coupling.** The middleware reads and mutates only the
  `IHttpContext` feature collection; it does not resolve services at request
  time. Extensibility is via installing a different `IHttpFormFeature`, not
  service location.

## Non-goals

- A model binder (form → typed object) — that belongs to the Web API /
  source-generation layer.
- Per-route form configuration — deferred until a real need appears (see the
  eager/lazy note above).
