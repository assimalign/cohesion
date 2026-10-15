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

`EventType` and `Comment` are nullable `ServerSentEventFieldValue` values. This
generated string value object rejects CR and LF on construction and implicit
string conversion; NUL is valid in both fields. `Id` is a nullable
`ServerSentEventId`, a second generated string value object that rejects CR, LF,
and NUL. These types carry the validation rules; property accessors do not
perform validation. Only `Data` may contain line breaks.

The [WHATWG parsing algorithm](https://html.spec.whatwg.org/multipage/server-sent-events.html#parsing-an-event-stream)
ends a line at CR, LF, or CRLF. Its `id` processing leaves the last event ID buffer
unchanged when the field contains NUL, so the stricter ID type prevents callers
from assigning an id the client would ignore. Event types and comments preserve
NUL, including comments constructed through `KeepAlive`.

Both types allow an empty string: an empty `Id` resets the client's last event
id, an empty `Comment` emits a comment line, and an empty `EventType` is omitted
as before. Their checked-in nullable conversions preserve `null`, including
assignment from a nullable string. `default(ServerSentEventFieldValue)` and
`default(ServerSentEventId)` contain no string; the formatter omits them, even
when wrapped in non-null nullable values. It never turns an absent id into an
id reset.

`IsValid` and `TryCreate` allow callers to validate without exceptions. Invalid
construction throws `ArgumentException` with a message from `ErrorMessages` that
does not echo the input. `KeepAlive(string)` and `WriteKeepAliveAsync(string, ...)`
retain their existing signatures and default comment, and check the comment
through the same conversion before any write or flush.

### Breaking changes from 10.0.0-preview.1 (#1475)

`EventType` and `Comment` change from `string?` to
`ServerSentEventFieldValue?`; `Id` changes from `string?` to `ServerSentEventId?`.
These are binary breaks requiring consumers to recompile. Valid string and null
assignments still compile through implicit conversions; readers that need text
should use `@event.Id?.Value` (and likewise for `EventType` and `Comment`). Code
using string members, string-specific generic APIs, or the old property
signatures must migrate. CR and LF now throw at conversion for all three fields;
NUL throws only for `Id`. NUL in event types, comments, and keep-alive comments
retains its exact UTF-8 format from preview.1. Other valid events retain their
exact format too, and multi-line `Data` still splits into `data:` lines.

Preview.1 documented a single-line contract but wrote these fields verbatim.
When an application placed untrusted text in them, it could inject fields or
whole events (CWE-93). Reachability depends on that application input path; this
package does not itself bind remote input to event metadata. The affected package
is `Assimalign.Cohesion.Http.ServerSentEvents`; advisory severity is assessed as
moderate because client-side impact depends on the application's event handlers.

The regression suite exercises CR, LF, and CRLF rejection in each metadata
field, NUL rejection only in `Id`, extra-field and extra-event payloads, and both
keep-alive helpers. Compatibility tests pin exact bytes for NUL in event types,
comments, and keep-alive comments, plus valid fields, null/empty fields, and
multi-line data. Value-object tests cover validation, nullable conversion, and
default-value omission; the streaming tests include an HTTP/1.1 round trip.

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
A host enables streaming by registering `new HttpResponseStreamingInterceptor()`
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

Transport integration tests consume duplex byte pipes through `IDuplexConnection` and HTTP connections through `IHttpConnection`. Their listeners use `AcceptAsync`; HTTP/3 stream fixtures use `IMultiplexedConnectionStream`. These contract names do not alter this package's request/response behavior.
