# Assimalign.Cohesion.Http.Streaming — Overview

Incremental HTTP response streaming as an opt-in feature package that plugs into
the transport via the response-interceptor seam.

## Purpose

Let a handler start a response and write its body incrementally — flushing bytes to
the client as they are produced — over any transport that exposes the
`IHttpExchangeInterceptor` seam, without the transport (or the protocol core)
depending on this package.

## Scope

- `IHttpResponseStreamingFeature` — the typed streaming API (`StartAsync`,
  `WriteAsync`, `FlushAsync`, `CompleteAsync`, `HasStarted`).
- `new HttpResponseStreamingInterceptor()` — the `IHttpExchangeInterceptor` a
  host registers to make streaming available on every exchange.
- `Response.Streaming` / `Response.SupportsStreaming` — ergonomic accessors on
  `IHttpResponse`.

## Dependencies

- `Assimalign.Cohesion.Http` (the response-interceptor seam + feature collection).
- **Not** `Assimalign.Cohesion.Http.Connections` — streaming is transport-agnostic.

## Usage

```csharp
// Host / composition root — opt into streaming:
options.Interceptors.Add(new HttpResponseStreamingInterceptor());

// Handler:
context.Response.Headers[HttpHeaderKey.ContentType] = "text/plain";
IHttpResponseStreamingFeature stream = context.Response.Streaming;
await stream.WriteAsync(chunk1);
await stream.FlushAsync();      // the client observes chunk1 now
await stream.WriteAsync(chunk2);
```

## Recovering a refused head

If a streaming call throws `HttpException` with `Code == HttpErrorCode.InvalidResponseField`,
the transport refused the head before writing bytes. `HasStarted` stays false. Replace the invalid
fields, then retry `StartAsync` or `CompleteAsync`, or write a buffered replacement. Completion
becomes final only after its flush succeeds.

When the transport supplies exchange control, `HasStarted` follows its existing commitment state;
a later I/O failure after commitment still reports true and cannot be answered with another head.
Hand-built interceptor contexts without control use a fallback that advances only after a sink
call succeeds. Response-starting callbacks remain once per exchange even when a refused head is
retried. See `DESIGN.md` for the preview.1 behavior change and fallback limitation.

## Layering

L1 foundation library (HTTP feature layer). See `DESIGN.md` for why streaming is a
separate package and how it plugs into the transport. Server-Sent Events
(`Assimalign.Cohesion.Http.ServerSentEvents`) builds on this package.
