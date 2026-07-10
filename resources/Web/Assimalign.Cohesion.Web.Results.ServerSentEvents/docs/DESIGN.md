# Assimalign.Cohesion.Web.Results.ServerSentEvents — Design

The Server-Sent Events result adapter for the Cohesion Web pipeline: one result type
(`ServerSentEventsHttpResult`) plus the `Results.ServerSentEvents(...)` /
`TypedResults.ServerSentEvents(...)` factory grafts. Part of the `Web.Results` family — read
`resources/Web/Assimalign.Cohesion.Web.Results/docs/DESIGN.md` first; this document records only
what is specific to the adapter.

## Design intent

Issue #864 homes the SSE result "next to `Http.ServerSentEvents`" so the core `Web.Results`
package stays lean: only applications that actually stream events should pull in the SSE
primitives. Layering fixes the physical placement — an L1 `libraries/Http` package can never
reference an L3 `resources/Web` package, so the adapter cannot live inside
`Assimalign.Cohesion.Http.ServerSentEvents` itself. It therefore lives on the Web side as a small
bridge package referencing both.

## Why-this-not-that decisions

- **Static extension members instead of a package-local factory class.** The adapter grafts its
  factory onto the core `Results`/`TypedResults` types with C# 14
  `extension(Results) { public static IResult ServerSentEvents(...) }` members, so call sites read
  identically to the built-ins (`Results.ServerSentEvents(events)`), and the factory vocabulary
  stays singular across the family. A `ServerSentEventResults` side-class was rejected: it would
  fork the discovery surface the factories exist to unify. The `Results` and `TypedResults` grafts
  live in **separate container classes** because both lower to identically-shaped static methods
  and would collide (CS0111) in one container.
- **`RootNamespace` deviation.** Types surface in `Assimalign.Cohesion.Web.Results` (not the
  assembly-matching namespace) so one `using` brings the core factories *and* the graft into
  scope — the same documented deviation the `Assimalign.Cohesion.Http.*` feature packages use.
  Recorded in the csproj.
- **`IAsyncEnumerable<ServerSentEvent>` as the one input shape.** Events are inherently a pushed,
  async sequence; a synchronous `IEnumerable` overload adds surface without a scenario (adapt with
  an async iterator). Conservative v1 — widen only on demonstrated need.

## Execution semantics

Thin adapter over `IHttpResponseStreamingFeature` (#769), holding the family's streaming rules:
resolve the feature from `context.Features` and **fail loudly** (`NotSupportedException`) when
streaming is not enabled — never buffer silently; set `Content-Type: text/event-stream` **and**
`Cache-Control: no-cache` before the first write (the head locks when the response starts; an
event stream must not be cached); **never set `Content-Length`**; write each event through the
shipped `WriteEventAsync` extension (which serializes via `ServerSentEventFormatter` and flushes
per event so the client observes it immediately); `CompleteAsync` when the sequence ends. The
status code is left untouched (SSE responds on the current status, normally 200).

The carrier is immutable, but reuse is bounded by the event sequence: an execution enumerates it
once, so the result is re-executable only over a re-enumerable `IAsyncEnumerable`.

## Non-goals

- **Keep-alive scheduling, reconnect/`Last-Event-ID` resume protocols** — application concerns
  over the `ServerSentEvent` model (`KeepAlive`, `Id`), not result concerns.
- **A negotiated SSE result** — if #149 ever needs `Accept: text/event-stream` awareness, it
  composes at the formatter registry, not here.
