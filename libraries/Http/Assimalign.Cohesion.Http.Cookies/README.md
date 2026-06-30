# Assimalign.Cohesion.Http.Cookies

HTTP cookie model and per-exchange cookie state for the Cohesion HTTP
family.

## Why a separate package

The Cohesion HTTP protocol core (`Assimalign.Cohesion.Http`) defines
the wire model &mdash; status, headers, methods, target, raw body
stream. The `Cookie` request header and the `Set-Cookie` response
header are part of that wire model; they live on
`IHttpRequest.Headers` and `IHttpResponse.Headers` like any other
header.

What the core deliberately omits is the **typed cookie model** &mdash;
`HttpCookie`, `IHttpCookieCollection`, the RFC 6265 §4.2.1 / §5.2
parsing grammar, the attribute serialization for `Set-Cookie`, and
the per-exchange feature wire-up. Those are application convenience
built on top of the headers, and a protocol-only consumer (an HTTP
client, a reverse proxy, an edge cache, the DNS-over-HTTPS transport)
has no use for them. Keeping the typed cookie model out of the core
lets the core stay small and AOT-friendly while making cookie support
an explicit opt-in.

The familiar property-style `request.Cookies` and `response.Cookies`
access is restored here through .NET 10 extension properties, both
backed by features stored in `IHttpContext.Features`.

## Surface

| Type | Role |
|------|------|
| `HttpCookie` | Single cookie with name, value, and `HttpCookieOptions` |
| `HttpCookieOptions` | RFC 6265 §5.2 attributes &mdash; Domain, Path, Expires, MaxAge, Secure, HttpOnly, SameSite |
| `HttpCookieSameSiteMode` | Enum: Unspecified / None / Lax / Strict |
| `IHttpCookieCollection` | Mutable collection of `HttpCookie`; used on both request and response sides |
| `HttpCookieCollection` | Default in-memory implementation |
| `IHttpRequestCookieFeature` | Per-exchange request-cookie state (parsed snapshot) stored in `IHttpContext.Features` |
| `IHttpResponseCookieFeature` | Per-exchange response-cookie state (mutable, drained to `Set-Cookie` at flush) stored in `IHttpContext.Features` |
| `HttpRequestCookieFeature` | Default request-feature implementation (internal) |
| `HttpResponseCookieFeature` | Default response-feature implementation (internal) |
| `HttpRequestCookieExtensions` | `request.Cookies` extension property on `IHttpRequest` |
| `HttpResponseCookieExtensions` | `response.Cookies` extension property on `IHttpResponse` |

## Usage

```csharp
using Assimalign.Cohesion.Http;

// Request side: lazy-parse from the Cookie header.
IHttpCookieCollection inbound = context.Request.Cookies;
HttpCookie? session = inbound.FirstOrDefault(c => c.Name == "session");

// Response side: append cookies; the transport drains them into
// Set-Cookie headers when flushing the response.
context.Response.Cookies.Add(new HttpCookie("trace", "abc123"));
context.Response.Cookies.Add(new HttpCookie("session", "xyz", new HttpCookieOptions
{
    Path = "/",
    HttpOnly = true,
    Secure = true,
    SameSite = HttpCookieSameSiteMode.Strict,
}));
```

When no cookie middleware has touched the exchange, `context.Request.Cookies`
returns an empty collection (and installs the feature so subsequent reads
are cheap) and `context.Response.Cookies` returns a fresh empty mutable
collection (also installing the feature). The presence of the feature
signals that cookie handling has been observed for this exchange.

## How the transports plug in

The wire-level transports
(`Assimalign.Cohesion.Http.Connections.Http1MessageReader`,
`Http2Stream`, `Http3HeaderCodec`) never touch cookie types on the
read path &mdash; they hand the request over with the raw `Cookie`
header on `request.Headers`, and the extension property tokenizes on
demand the first time `request.Cookies` is read.

On the write path, the transports' response serializers
(`Http1MessageWriter`, `HPackEncoder` via `Http2ConnectionContext`,
`Http3HeaderCodec.EncodeResponseHeaders`, `Http1ProtocolUpgrade`) look
up `IHttpResponseCookieFeature` from the feature collection and, when
present, emit each cookie as a separate `Set-Cookie` header line
(RFC 6265 §3 forbids comma-folding for `Set-Cookie`). When no feature
is attached, the response simply has no `Set-Cookie` headers &mdash;
no allocations, no enumeration cost.

## Implementing a custom feature

`HttpRequestCookieFeature` and `HttpResponseCookieFeature` are internal.
Middleware that needs richer cookie handling (signed cookies, encrypted
cookies, alternate parsing rules, prefixed cookies) should implement
`IHttpRequestCookieFeature` and/or `IHttpResponseCookieFeature`
directly and attach instances via
`context.Features.Set<IHttpRequestCookieFeature>(...)` /
`context.Features.Set<IHttpResponseCookieFeature>(...)`. The
`request.Cookies` and `response.Cookies` extension properties consult
the feature collection for any implementation, not just the package's
defaults.
