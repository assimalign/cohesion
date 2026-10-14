# Assimalign.Cohesion.Http.ServerSentEvents — Overview

Server-Sent Events (`text/event-stream`) primitives layered over the HTTP
response-streaming seam.

## Purpose

Provide the value model and wire-format serializer for Server-Sent Events, plus an
ergonomic bridge onto `IHttpResponseStreamingFeature`, so a handler can push events
to a client over a long-lived HTTP response.

## Scope

- `ServerSentEvent` — the event value model (`event`, `id`, `data`, `retry`,
  comment) with `Message` / `KeepAlive` factories and the `text/event-stream`
  media-type constant.
- `ServerSentEventFieldValue` — checked single-line text for `EventType` and
  `Comment`; rejects CR and LF, while allowing NUL and an empty string.
- `ServerSentEventId` — checked single-line text for `Id`; rejects CR, LF, and
  NUL, while allowing an empty string to reset the client's last event id.
- `ServerSentEventFormatter` — WHATWG wire-format serializer (UTF-8 into an
  `IBufferWriter<byte>`, AOT-safe).
- `WriteEventAsync` / `WriteKeepAliveAsync` extension members on
  `IHttpResponseStreamingFeature`.

## Dependencies

- `Assimalign.Cohesion.Http` (the protocol contracts + `HttpHeaderKey`).
- `Assimalign.Cohesion.Http.Streaming` (`IHttpResponseStreamingFeature`).
- **Not** `Assimalign.Cohesion.Http.Connections` — SSE is transport-agnostic. The
  transports implement the streaming write path; an application composes SSE with
  a transport.

## Usage

```csharp
context.Response.Headers[HttpHeaderKey.ContentType] = ServerSentEvent.MediaType;
context.Response.Headers[HttpHeaderKey.CacheControl] = "no-cache";

IHttpResponseStreamingFeature stream = context.Response.Streaming;
await stream.WriteEventAsync(new ServerSentEvent("hello") { EventType = "greeting", Id = "1" });
await stream.WriteKeepAliveAsync();
```

String assignments to `EventType`, `Id`, and `Comment` validate immediately and
throw `ArgumentException` for CR or LF. `Id` also rejects NUL; event types,
comments, and `KeepAlive` comments accept and preserve NUL. To handle untrusted
text without throwing, use the appropriate type's generated `TryCreate` before
constructing the event:

```csharp
if (ServerSentEventId.TryCreate(candidateId, out var id))
{
    await stream.WriteEventAsync(new ServerSentEvent("first\nsecond") { Id = id });
}
```

`Data` remains multi-line text: the example emits two `data:` lines. An empty
string is a valid field value; `Id = ""` resets the client's last event id.
`EventType = null` and assignments from nullable strings still work. Null and
default field values are omitted; empty event types are also omitted.

Consumers upgrading from 10.0.0-preview.1 must recompile for the metadata property
type changes. Read their text with `@event.Id?.Value`, `@event.EventType?.Value`, or
`@event.Comment?.Value`. See [DESIGN.md](DESIGN.md#breaking-changes-from-1000-preview1-1475)
for the source migration and injection fix.

## Layering

L1 foundation library (HTTP feature layer). See `DESIGN.md` for the rationale
behind the separate package and the non-goals (response interceptor, Web result
type, client parsing).

Transport integration tests consume duplex byte pipes through `IDuplexConnection` and HTTP connections through `IHttpConnection`. Their listeners use `AcceptAsync`; HTTP/3 stream fixtures use `IMultiplexedConnectionStream`. These contract names do not alter this package's request/response behavior.
