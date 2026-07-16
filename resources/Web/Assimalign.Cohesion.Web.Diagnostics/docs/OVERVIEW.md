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
exchanges are logged too.

## Field selection and redaction

`HttpLoggingFields` selects what is captured. The default set carries the request line, headers,
status, duration, client address, byte counts, and trace correlation; the **query string and
bodies are opt-in** because they routinely carry secrets. Header redaction is
**allowlist-based**: names always log, values log only for allowlisted headers, and
`Authorization`, `Proxy-Authorization`, `Cookie`, and `Set-Cookie` are never in the default
allowlists.

Per-endpoint overrides attach an `HttpLoggingMetadata` to the route's metadata bag (last-wins);
`HttpLoggingFields.None` silences an endpoint entirely — the usual choice for health probes.

## Dependencies

| Reference | Why |
| --- | --- |
| `Assimalign.Cohesion.Web` | the middleware/pipeline abstractions |
| `Assimalign.Cohesion.Web.Routing` | reads the endpoint metadata bag for per-endpoint overrides |
| `Assimalign.Cohesion.Logging` | the emission model (`ILogger`, `LoggerEntry`, `LoggerProvider`) |

See [DESIGN.md](DESIGN.md) for the architecture and the decisions behind it.
