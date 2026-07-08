# Assimalign.Cohesion.Http

The HTTP protocol contract surface for the Cohesion family. Defines
the wire-level model &mdash; request, response, headers, methods,
status, targets, body streams &mdash; in a shape usable by every
consumer that wants raw HTTP contracts: clients, proxies, edge
caches, reverse proxies, observability layers, and any
DNS-over-HTTPS-style transport.

## Family layout

| Package | Role |
|---------|------|
| `Assimalign.Cohesion.Http` | Protocol contracts only &mdash; this package. |
| `Assimalign.Cohesion.Http.Sessions` | Per-exchange session state (application layer). |
| `Assimalign.Cohesion.Http.Forms` | Form-body model (server-side application layer). |
| `Assimalign.Cohesion.Http.ClientFactory` | Lifecycle wrapper around `System.Net.Http.HttpClient`. |
| `Assimalign.Cohesion.Http.Connections` | HTTP/1.1, HTTP/2, HTTP/3 server transports. |

The protocol core deliberately omits application-layer features so
non-server consumers (clients, the DoH DNS transport, proxies) can
reference it without dragging in session / form / identity types
they never use. The dependency direction is enforced by contract
tests in `tests/Abstractions/HttpProtocolCoreBoundaryTests.cs`.

## Standards

Targets RFC 9110 (HTTP semantics), RFC 9111 (HTTP caching),
RFC 9112 (HTTP/1.1 messaging), RFC 9113 (HTTP/2), RFC 9114 (HTTP/3).
The value types (`HttpMethod`, `HttpPath`, `HttpStatusCode`,
`HttpVersion`, `HttpScheme`, `HttpHost`, `HttpHeaderKey`,
`HttpHeaderValue`, `HttpMediaType`), the caching primitives
(`HttpCacheControl`, `HttpEntityTag`, `HttpEntityTagCondition`,
`HttpDate`, `HttpFreshness`, `HttpConditionalRequest`), and
abstractions (`IHttpRequest`, `IHttpResponse`, `IHttpContext`,
`IHttpHeaderCollection`, `IHttpQueryCollection`,
`IHttpCookieCollection`, `IHttpConnectionInfo`) are AOT-friendly
and trim-safe.

These are field-value primitives, not a cache: they parse and evaluate
the caching and validator fields (`Cache-Control`, `ETag`, `If-Match`,
`If-None-Match`, `If-Modified-Since`, `If-Unmodified-Since`) so a
response-caching layer and range/precondition evaluation share one
implementation. `HttpFieldRules` remains name-classification only.

## See also

- [`Assimalign.Cohesion.Http.Sessions`](../Assimalign.Cohesion.Http.Sessions/README.md) &mdash; session state via `IHttpContext.Items`.
- [`Assimalign.Cohesion.Http.Forms`](../Assimalign.Cohesion.Http.Forms/README.md) &mdash; parsed form bodies via `IHttpRequest` extensions.
