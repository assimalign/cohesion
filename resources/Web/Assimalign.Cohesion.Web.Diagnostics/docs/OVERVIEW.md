# Assimalign.Cohesion.Web.Diagnostics — Overview

HTTP request/response logging and W3C access logs for Cohesion web applications, in one
diagnostics package:

- **`UseHttpLogging`** — an `IWebApplicationMiddleware` that captures request/response metadata
  for each exchange and emits one structured `LoggerEntry` through the application's Cohesion
  logging pipeline when the downstream pipeline completes (or faults).
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

// The middleware takes the composed logger explicitly - builder time only, no service location.
application
    .UseHttpLogging(loggerFactory, options =>
    {
        options.Fields = HttpLoggingFields.Default | HttpLoggingFields.RequestQuery;
        options.AllowedRequestHeaders.Add("X-Correlation-Id");
    })
    .Use(...);
```

Register `UseHttpLogging` **first** — ahead of authentication, CORS, and routing — so rejected
exchanges are logged too. Behind a proxy, `UseForwardedHeaders` follows it, after
`UseSecurityHeaders` when that is registered (the area's [middleware order](../../../../docs/resources/Web/MIDDLEWARE_ORDER.md)): the entry
records the effective (forwarded) client address, scheme, and host, and keeps the transport peer
beside them under `network.peer.address`.

## Field selection and redaction

`HttpLoggingFields` selects what is captured. The default set carries the request line, headers,
status, duration, client address, byte counts, and trace correlation; the **query string and
bodies are opt-in** because they routinely carry secrets. Header redaction is
**allowlist-based**: names always log, values log only for allowlisted headers, and
`Authorization`, `Proxy-Authorization`, `Cookie`, and `Set-Cookie` are never in the default
allowlists.

Per-endpoint overrides attach an `HttpLoggingMetadata` to the route's metadata bag (last-wins),
usually through the convention verb on a mapped route or a route group:
`app.MapGroup("/probes").WithHttpLogging(HttpLoggingFields.None)`. `HttpLoggingFields.None`
silences an endpoint entirely — the usual choice for health probes. The
override is read from the endpoint `UseRouting` published when the pipeline unwinds, so it applies
with `UseHttpLogging` registered first. It covers the exchanges the endpoint handles, not a CORS
preflight that names it, which is logged with the configured fields.

## Dependencies

| Reference | Why |
| --- | --- |
| `Assimalign.Cohesion.Web` | the middleware/pipeline abstractions |
| `Assimalign.Cohesion.Web.Routing` | reads the endpoint metadata bag for per-endpoint overrides |
| `Assimalign.Cohesion.Http.Forwarded` | the effective scheme, host, and client address (`Effective*`), resolved by `UseForwardedHeaders` behind a trusted proxy |
| `Assimalign.Cohesion.Logging` | the emission model (`ILogger`, `LoggerEntry`, `LoggerProvider`) |
| `Assimalign.Cohesion.Web.Server` | `IWebClientFaultFeature`: on an HTTP/1.1 request with a body, a request body that broke its framing or a limit, or that the client cut short, is logged as a client fault at the configured level with the status sent, not escalated to `Error` (#1340); HTTP/2 and HTTP/3 follow with #1378 |
| `Assimalign.Cohesion.Http.Streaming` | whether the response had started, so a client fault logs the status that reached the wire |

Trace correlation comes from the request's `traceparent` header. The package does not read the
server's request id (`IWebRequestIdFeature`, in `Web.Server` since #1379).

An exchange whose downstream pipeline throws is logged at `Error` with the exception, except a client
fault on an HTTP/1.1 request with a body: an entry marked `http.client_fault = true`, at the
configured level, without the exception, whose `http.response.status` is the `400`, `413`, `408` or
`431` the transport answered with. The default Web server reports client faults on HTTP/1.1 only
until #1378, so on HTTP/2 and HTTP/3 a body fault that throws through the middleware is logged at
`Error` like any other exception.

See [DESIGN.md](DESIGN.md) for the architecture and the decisions behind it.
