# Assimalign.Cohesion.Http.Transports — Design

This document captures the design intent behind the shipped HTTP transport
surface. It is intentionally focused on the design decisions a future
reader (or future Claude session) would otherwise have to re-derive from
diffs. `DESIGN_SUGGESTION.md` in this same folder is a separate,
forward-looking proposal for a multiplex-aware refactor; this file
describes the surface as it ships today.

## Per-request feature injection

### What it is

`HttpConnectionListenerOptions.CreateFeatures` is the single injection
hook the transport layer exposes for application code to pre-populate the
feature collection on every `IHttpContext`:

```csharp
HttpConnectionListenerOptions options = new()
{
    CreateFeatures = () =>
    {
        HttpFeatureCollection features = new();
        features.Set(new SessionFeature(/* scoped per-request state */));
        features.Set(new TracingFeature(/* request-scoped tracer */));
        return features;
    }
};
```

The factory is invoked once per `IHttpContext` being constructed — once
per HTTP/1.1 request, once per HTTP/2 / HTTP/3 stream. The returned
collection becomes the defaults source of the per-request
`IHttpContext.Features` so the features it carries are immediately
visible to the first middleware to read them.

### Why per-request, not per-connection

A previous iteration of this surface bound features to the *connection*
lifetime via a `ConfigureConnectionFeatures` callback that fired once per
accepted connection. That shape was wrong:

- `IHttpContext` is `IAsyncDisposable` and tears down at the end of every
  request. Features whose state needs deterministic cleanup
  (cryptographic material, scoped service providers, file handles, span
  buffers) only get that cleanup if the framework can dispose them
  per-request. A connection-scoped collection has no equivalent dispose
  hook — connection teardown happens on a different timeline (keep-alive
  pooling, HTTP/2 GOAWAY, HTTP/3 idle timeout) and would force every
  feature author to invent their own cleanup signalling.
- HTTP/2 and HTTP/3 multiplex many requests over one connection. A
  connection-scoped feature with mutable state quickly becomes a shared
  data race waiting to happen. Per-request scoping eliminates that
  category of bug by construction.
- The connection lifetime is invisible to most consumers. Application
  code reasons in terms of requests; binding feature lifetime to the
  request matches what middleware actually expects.

The `CreateFeatures` factory therefore fires per-request, the returned
collection lives only as long as the `IHttpContext` does, and disposal
is the framework's contract — not the feature author's.

### Disposal contract

When `IHttpContext.DisposeAsync` runs, the transport walks the effective
feature collection (the local layer plus the factory-supplied defaults)
and disposes every feature that implements `IAsyncDisposable` or
`IDisposable`:

- `IAsyncDisposable.DisposeAsync` is preferred over `IDisposable.Dispose`
  when the feature implements both.
- A single feature throwing during disposal does **not** abort the
  disposal walk. The exception is swallowed and the remaining features
  (and the request / response body streams) are disposed normally. This
  prevents one faulty feature from leaking the rest of the request's
  resources.
- Replaced features (a `Set` call that overwrites a same-named feature)
  are no longer reachable through enumeration and therefore are not
  disposed by the framework. Middleware that replaces a feature is
  responsible for disposing the old instance explicitly.
- Snapshot semantics: the feature list is enumerated once into an array
  before disposal begins so a feature whose `DisposeAsync` mutates the
  collection cannot break iteration.

### Why a factory rather than a callback

`CreateFeatures` returns a new `IHttpFeatureCollection` rather than
populating one the framework hands in. The factory shape is the
right primitive for two reasons:

1. **Disposal scope is unambiguous.** The framework owns the returned
   collection's lifetime: it wraps it as defaults for the request
   collection and disposes its features when the context disposes. With
   a populate-callback the caller would need to coordinate disposal of
   the framework-owned collection, which inverts the ownership model.
2. **Per-request state stays in the closure.** Many useful features
   capture per-request constructor arguments (a fresh tracer, a fresh
   scoped service provider). A factory naturally allocates fresh
   per-request state on each call; a populate callback would force the
   caller to manage per-request allocation themselves.

### Wrapping semantics

`HttpContext.Features` is typed as the concrete `HttpFeatureCollection`,
but `CreateFeatures` returns the `IHttpFeatureCollection` interface so
the user can supply any implementation. The transport reconciles this by
wrapping the factory result with
`new HttpFeatureCollection(factoryResult)`, which uses the factory
result as a read-through defaults source:

- Reads (`Get`, enumeration) pass through to the factory's collection
  when no local override exists.
- Mutations (`Set`, `Remove`) on `IHttpContext.Features` land on the
  local layer only — the factory's collection is not modified by
  middleware. This is the safer default: factory-attached features
  cannot be silently replaced or removed by downstream middleware.
- Disposal walks the effective collection (local + defaults) so
  features attached by either layer get cleaned up uniformly.

### Synchronous, no transport-context input

The factory is `Func<IHttpFeatureCollection>?` — no parameters, no
async. This is deliberate:

- Async would mean the receive loop has to await on the request-build
  path, which would interleave with the protocol parser and complicate
  reasoning about flow-control and back-pressure. The factory's job is
  to allocate per-request state, not to do I/O.
- Connection metadata (endpoints, IsSecure) is not passed in because the
  factory's caller typically does not need it; what they need
  (per-request service provider, per-request diagnostics scope) is
  captured in the closure. Pushing transport state into the factory
  signature would invite features that depend on connection lifetime,
  which is exactly the lifetime mismatch we are walking away from.

### AOT posture

No reflection, no runtime code generation. The factory is a plain
delegate invocation and the feature lookup chains through
`HttpFeatureCollection`'s dictionary reads.

### Non-goals

- **Connection-scoped state.** If a feature genuinely needs to live for
  the lifetime of the connection (a TLS handshake projection, for
  example), it belongs in the application layer's own connection map
  keyed on `IHttpConnectionContext.Items` or a sidecar dictionary —
  *not* on the request's feature collection. This package does not
  surface a connection-feature collection at all.
- **Multiple registered factories.** A pipeline of feature contributors
  can be composed by the caller — the single `CreateFeatures` slot can
  invoke any number of internal helpers.
- **Async initialization.** See above. If a concrete use case for
  per-request async feature setup emerges, the factory signature can be
  widened without breaking existing callers.

## Receive-loop failure isolation

Each protocol's receive loop (`Http1ConnectionContext.ReceiveAsync`,
`Http2ConnectionContext.ReceiveAsync`, `Http3ConnectionContext.ReceiveAsync`)
classifies failures into two scopes:

- **Per-connection wire-level failures** — truncated frames, malformed
  request lines, peer reset, socket I/O errors. The receive enumerable
  yields no more values and exits cleanly; the surrounding
  `await using` disposes the connection; the listener keeps accepting.
  Protocol-required wire frames (`GOAWAY` on HTTP/2 connection errors,
  `RST_STREAM` on HTTP/2 stream errors) are emitted before exit.
- **Per-stream failures** (HTTP/2, HTTP/3) — malformed headers on one
  stream, QPACK errors on one HTTP/3 stream. The loop emits `RST_STREAM`
  (HTTP/2) or drops the offending stream (HTTP/3) and continues
  accepting subsequent streams on the same connection.

The design intent is *failure isolation*: a single malformed peer must
never bring down the listener. Cancellation propagates normally so
cooperative shutdown is unaffected.

## Open questions / future work

- A full design write-up covering the protocol context hierarchy
  (`HttpConnection` / `HttpConnectionContext` / per-protocol
  implementations) is still owed. See `DESIGN_SUGGESTION.md` for the
  in-flight multiplex-aware refactor proposal.
- Async feature initialization (see "Non-goals" above) is worth
  revisiting once a concrete consumer appears that genuinely needs it.
