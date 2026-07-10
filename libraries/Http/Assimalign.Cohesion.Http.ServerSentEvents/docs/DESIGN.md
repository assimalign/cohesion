# Assimalign.Cohesion.Http.ServerSentEvents — Design

Server-Sent Events (SSE) is a feature-specific formatting layer over incremental
response streaming, so it ships as its own package. It sits on top of
`Assimalign.Cohesion.Http.Streaming` (the streaming feature) and the protocol core,
and — like the other HTTP feature packages (`Http.ExtendedConnect`, `Http.Forms`,
`Http.Cookies`, …) — surfaces its types in the `Assimalign.Cohesion.Http` namespace
for discoverability.

## Why a separate package

- **SSE is not protocol machinery.** The `text/event-stream` framing is one
  WHATWG-defined wire format over a streaming response body.
  `Assimalign.Cohesion.Http.Streaming` owns the streaming feature
  (`IHttpResponseStreamingFeature`); SSE is one consumer of it. Keeping SSE (and
  streaming) out of the core keeps the core's surface to wire-level concerns.
- **No transport coupling.** This package depends on `Assimalign.Cohesion.Http` and
  `Assimalign.Cohesion.Http.Streaming` — never on `Assimalign.Cohesion.Http.Connections`.
  The transport implements the raw framing behind the response-interceptor seam;
  the streaming package taps it; SSE is pure formatting over the streaming feature
  and has no knowledge of h1/h2/h3 framing. A host composes the three, but the SSE
  library never references the transport.
- **Reuse.** The same primitives back both a hand-rolled handler (get
  `context.Response.Streaming`, call `WriteEventAsync`) and the future Web-facing
  `ServerSentEventsResult` over `IAsyncEnumerable` (#149 / #28).

## The event model

`ServerSentEvent` is the value model for one SSE message — the `SseItem`
equivalent. It carries the four data-bearing fields plus a comment:

| Property | Field | Notes |
|---|---|---|
| `EventType` | `event` | Single-line; empty → generic `message` event. |
| `Id` | `id` | Single-line; empty (non-null) resets the client's stored id. |
| `Data` | `data` | May be multi-line — each line is emitted as its own `data:` field. |
| `Retry` | `retry` | Serialized as whole milliseconds. |
| `Comment` | (leading `:`) | Ignored by clients; the keep-alive mechanism. |

`Message(data)` and `KeepAlive(comment)` are the two common factories.
`MediaType` is the `text/event-stream` constant. The type is a `readonly struct`
with `init`-only properties — allocation-free to construct and pass by value.

`EventType`, `Id`, and `Comment` MUST be single-line (the wire format terminates a
field at the first newline); only `Data` may contain line breaks. This is a
documented caller contract, not enforced, to keep the formatter allocation-light.

## The formatter

`ServerSentEventFormatter` renders a `ServerSentEvent` to the WHATWG wire format:
an optional comment line, the `event` / `id` / `retry` fields when present, one
`data:` line per line of the payload (splitting on CRLF, CR, or LF), and the
terminating blank line that dispatches the event. Field values are UTF-8 encoded
straight into an `IBufferWriter<byte>`; the fixed field names and separators are
ASCII. There is no reflection and no runtime code generation, so the type is trim-
and NativeAOT-safe. `Format(event)` is the convenience overload returning a
`byte[]`.

## The bridge onto the streaming seam

`ServerSentEventStreamingExtensions` adds `WriteEventAsync` and
`WriteKeepAliveAsync` to `IHttpResponseStreamingFeature` (from
`Assimalign.Cohesion.Http.Streaming`). Each serializes the event and does
`WriteAsync` + `FlushAsync` so the peer observes it immediately. This is where SSE
meets the streaming write path — no transport dependency, just the feature contract.
A host enables streaming by registering `HttpResponseStreaming.CreateInterceptor()`
on the transport; a handler then resolves the feature via `context.Response.Streaming`.

### Header-commit and content type

Headers lock once a streaming response starts (the streaming feature's contract),
so the caller sets `Content-Type: text/event-stream`
(`ServerSentEvent.MediaType`) — and any other SSE conventions such as
`Cache-Control: no-cache` — **before** the first `WriteEventAsync`. The
`Last-Event-ID` request header a reconnecting client replays is
`HttpHeaderKey.LastEventId` in the core registry (alongside the other well-known
header-name constants); a handler reads it to resume a stream from the last
delivered id.

## Non-goals

- **The streaming write path itself.** Incremental write/flush, the transport
  response-interceptor seam, and the raw framing live in
  `Assimalign.Cohesion.Http.Streaming` and `Assimalign.Cohesion.Http.Connections`;
  SSE is the formatting layer on top and adds no transport hook of its own.
- **The Web-facing result type.** `ServerSentEventsResult` over `IAsyncEnumerable`,
  content negotiation, and builder-time keep-alive/retry configuration land with
  the Web result-writer work (#149 / #28); this package is the primitive layer it
  will build on.
- **Client-side SSE parsing.** This package serializes; it does not parse an
  inbound event stream.

## AOT posture

No reflection, no runtime code generation. The event model is a value struct; the
formatter is span/`IBufferWriter<byte>` arithmetic over UTF-8.
