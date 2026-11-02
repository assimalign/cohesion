# Assimalign.Cohesion.Web.Diagnostics — Overview

HTTP request/response logging and W3C access logs for Cohesion web applications, in one
diagnostics package:

- **`AddHttpLogging` / `UseHttpLogging`** — an `IWebApplicationMiddleware` that captures
  request/response metadata for each exchange and emits one structured `LoggerEntry` through the
  application's Cohesion logging pipeline when the downstream pipeline completes (or faults). The
  registration verb on `builder.Services` configures it (`HttpLoggingBuilder`), and the pipeline
  verb places it.
- **`W3CAccessLogProvider`** — an `ILoggerProvider` that renders those entries as W3C Extended
  or NCSA common/combined access-log files with buffered, size- and day-rolled output.

Emission deliberately **rides the repo's own Logging model** — providers, enrichers, and filter
rules all apply — rather than a parallel logging pipeline. The two halves meet at the stable
attribute-name contract in `HttpLoggingAttributes` (`http.request.method`,
`http.response.status`, `http.duration`, ...), so any provider, filter, or enricher can consume
access-log entries without referencing this package's middleware.

## Composition

```csharp
// Providers register on the logging pipeline like any other sink.
var accessLog = new W3CAccessLogProvider(new W3CAccessLogOptions
{
    Directory = "/var/log/myapp",
    Format = AccessLogFormat.W3CExtended,
});

builder.Logging.AddProvider(new ConsoleLoggerProvider());
builder.Logging.AddProvider(accessLog);

// The middleware is configured where it is registered, over the application's own logger factory.
// The provider overload resolves the factory once, when the pipeline is composed - never per request.
builder.Services.AddHttpLogging((services, logging) =>
{
    logging.UseLoggerFactory(services.GetRequiredService<ILoggerFactory>());
    logging.Options.Fields = HttpLoggingFields.Default | HttpLoggingFields.RequestQuery;
    logging.Options.AllowedRequestHeaders.Add("X-Correlation-Id");
});

WebApplication application = builder.Build();
application
    .UseHttpLogging()
    .Use(...);
```

Configuration lives on `AddHttpLogging` only ("Add configures, Use places"), and the logger has no
default: `logging.UseLoggerFactory(...)` (a logger for `Options.Category`) or `logging.UseLogger(...)`
(a composed logger as it is) is required. A registration without one, or with an invalid option, throws
inside the `AddHttpLogging` call (when the pipeline is composed, for the provider overload).
`UseHttpLogging()` takes no arguments and places the last registration; without one the application
fails to start with an error that names `AddHttpLogging`.

Place `UseHttpLogging()` **first** — ahead of authentication, CORS, and routing — so rejected
exchanges are logged too. Behind a proxy, `UseForwardedHeaders` follows it, after
`UseSecurityHeaders` when that is placed (the area's [middleware order](../../../../docs/resources/Web/MIDDLEWARE_ORDER.md)): the entry
records the effective (forwarded) client address, scheme, and host, and keeps the transport peer
beside them under `network.peer.address`.

## Field selection and redaction

`HttpLoggingFields` selects what is captured. The default set carries the request line, headers,
status, duration, client address, byte counts, and trace correlation; the **query string and
bodies are opt-in** because they routinely carry secrets. Header redaction is
**allowlist-based**: names always log, values log only for allowlisted headers, and
`Authorization`, `Proxy-Authorization`, `Cookie`, and `Set-Cookie` are never in the default
allowlists.

Request capture and byte counting work through request views, including decompression,
rewrite and request timeouts. Logging after decompression observes decoded bytes. Capture
is limited to the configured prefix and content types, while byte counts include all bytes
consumed downstream. Logging leaves the original request body in place.

Per-endpoint overrides attach an `HttpLoggingMetadata` to the route's metadata bag (last-wins),
usually through the convention verb on a mapped route or a route group:
`app.MapGroup("/probes").WithHttpLogging(HttpLoggingFields.None)`. `HttpLoggingFields.None`
silences an endpoint entirely — the usual choice for health probes. The
override is read from the endpoint `UseRouting` published when the pipeline unwinds, so it applies
with `UseHttpLogging` placed first. It covers the exchanges the endpoint handles, not a CORS
preflight that names it, which is logged with the configured fields.

## Dependencies

| Reference | Why |
| --- | --- |
| `Assimalign.Cohesion.Web` | the middleware/pipeline abstractions |
| `Assimalign.Cohesion.Web.Routing` | reads the endpoint metadata bag for per-endpoint overrides |
| `Assimalign.Cohesion.Http.Forwarded` | the effective scheme, host, and client address (`Effective*`), resolved by `UseForwardedHeaders` behind a trusted proxy |
| `Assimalign.Cohesion.Logging` | the emission model (`ILogger`, `LoggerEntry`, `LoggerProvider`) |
| `Assimalign.Cohesion.Web.Server` | request-id and server-activity features for trace correlation (#1297); `IWebClientFaultFeature` for logging malformed or over-limit bodies at the configured level with the transport's status (#1340, #1378) |
| `Assimalign.Cohesion.Http.Streaming` | whether the response had started, so a client fault logs the status that reached the wire |

With `HttpLoggingFields.TraceContext` enabled, `trace.id` comes from
`IWebRequestIdFeature.RequestId`, including a request that arrived without `traceparent`.
`span.id` identifies this exchange's server span through `IWebRequestActivityFeature`; it is
absent when no server span exists, including when nobody listens for spans. An unrelated
`Activity.Current` is never used. Custom servers may omit the features, in which case the
corresponding attributes are absent. This changes preview.1's behavior, which logged the
inbound parent span id; consumers must now interpret `span.id` as the server's span.

An exchange whose downstream pipeline throws is logged at `Error` with the exception, except a client
fault the default Web server reports, on any version: an entry marked `http.client_fault = true`, at the
configured level, without the exception, whose `http.response.status` is the `400`, `413`, `408` or
`431` the transport answered with. On HTTP/2 and HTTP/3 a malformed body resets the stream instead of
drawing a status, and is logged as the `400` the transport reports for it (#1378).

An `OperationCanceledException` after the exchange's cancellation token fires also stays at the
configured level without an exception; cancellation alone adds no client-fault marker. A peer
disconnect before the response starts sends no response, even when the entry still shows the
staged default status of 200. An unrelated cancellation exception on a live exchange remains Error.

## Sink diagnostics

Observe `Assimalign.Cohesion.Web.Diagnostics` through `dotnet-trace`, `dotnet-counters`, or the
event-source logging forwarder for file-open events and throttled sink, retention, entry-building
and client-address-resolver failures. `failed-access-log-writes` counts failed Write calls,
not lost buffered entries; `dropped-http-log-entries` counts failed entry emission.

The source category is a prefix of the HTTP log's default category,
`Assimalign.Cohesion.Web.Diagnostics.HttpLogging`, so a category filter on the source also
affects HTTP entries. Forwarded diagnostics are excluded from the W3C file by its exchange-only
filter. See [Diagnostics](DESIGN.md#diagnostics) for payload and failure semantics.

See [DESIGN.md](DESIGN.md) for the architecture and the decisions behind it.

Transport integration tests consume duplex byte pipes through `IDuplexConnection` and HTTP connections through `IHttpConnection`. Their listeners use `AcceptAsync`; HTTP/3 stream fixtures use `IMultiplexedConnectionStream`. These contract names do not alter this package's request/response behavior.
