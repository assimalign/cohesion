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
- `ServerSentEventFormatter` — WHATWG wire-format serializer (UTF-8 into an
  `IBufferWriter<byte>`, AOT-safe).
- `WriteEventAsync` / `WriteKeepAliveAsync` extension members on
  `IHttpResponseStreamingFeature`.

## Dependencies

- `Assimalign.Cohesion.Http` (the streaming feature contract + `HttpHeaderKey`).
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

## Layering

L1 foundation library (HTTP feature layer). See `DESIGN.md` for the rationale
behind the separate package and the non-goals (response interceptor, Web result
type, client parsing).
