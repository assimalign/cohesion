# Assimalign.Cohesion.Web.Hosting — Design

The Web resource's composition root: the `WebApplicationBuilder` /
`WebApplication` surface that wires the `Assimalign.Cohesion.Http.Connections`
transport, the request pipeline, DI, logging, and configuration into a runnable
host. Per the repo's hosting philosophy, **this is the one place DI / Logging /
Config integration happens** — the transport and protocol libraries stay free of
those concerns.

This document grows as areas are touched rather than re-documenting the whole
surface at once. The broader server/runtime shape (per-connection dispatch,
error isolation, graceful stop) is being reworked under issue #762; this file
currently captures only the design decisions that are settled.

## Configuration-bound server limits and endpoints

### What it is

`WebApplicationServerBuilder.UseConfiguration(IConfiguration, sectionKey = "Http")`
(an extension member in `WebHostingExtensions`) binds the server's listener
**endpoints** and **server limits** from a Cohesion `IConfiguration` section at
builder time, giving `appsettings`-style Kestrel-section parity:

```json
"Http": {
  "Endpoints": {
    "Primary": { "Protocol": "Http1", "Host": "localhost", "Port": 8080 }
  },
  "Limits": {
    "MaxRequestLineSize": 8192,
    "MaxRequestBodySize": 30000000,
    "KeepAliveTimeout": "00:02:10",
    "RequestHeadersTimeout": "00:00:30"
  }
}
```

The actual binding lives in the internal `HttpServerConfiguration.Bind`, invoked
from inside the `UseServer((serviceProvider, options) => …)` callback so it runs
when the `HttpConnectionListener` is composed. Limits land on
`HttpConnectionListenerOptions.Limits` (`HttpServerLimits`); each endpoint maps
to a TCP listener via the existing `UseHttp1` / `UseHttp2` convenience overloads.

### Why explicit, hand-rolled binding

The binding is deliberately **not** reflection-based:

- **AOT is a hard repo requirement.** A reflection binder (`Bind<T>` over
  property setters) is exactly the pattern trimming and NativeAOT break. Every
  value is instead read by its known path (`GetValue`) and parsed with the
  invariant culture. There is no dynamic member discovery and no
  `Microsoft.Extensions.*` dependency.
- **Security config must fail loudly.** A mistyped limit is a security
  regression waiting to happen (`MaxRequestBodySize: "1O485760"` silently
  falling back to a default would be dangerous). A value that is *present but
  unparseable* throws; a value that is *absent* leaves the built-in
  Kestrel-parity default in place. The two cases are never conflated.

### Binding specifics

- **Body size.** `"unbounded"` / `"none"` bind to `null` (no cap); otherwise a
  non-negative integer.
- **Timeouts.** A `TimeSpan` string (`"00:00:30"`), a bare whole number of
  seconds, or `"infinite"` / `"-1"` for `Timeout.InfiniteTimeSpan`.
- **Endpoint host.** Literal IP addresses, `localhost` (→ loopback), and the
  wildcards `*` / `+` / `0.0.0.0` (→ any) / `[::]` / `::` (→ IPv6 any). DNS is
  **not** resolved at bind time — a hostname that is not one of those is an
  error, because binding-time DNS is an I/O surprise the composition root should
  not hide.
- **Endpoint protocol.** `Http1` (default) or `Http2`; anything else throws.

### Scope boundary

`UseConfiguration` binds endpoints and the HTTP/1.1 server limits only. TLS
composition, HTTP/3 registration, and the connection-dispatch rewrite are
separate concerns (the latter under #762). Data-rate limits are deferred with
the transport's streaming-body rework.

### AOT posture

No reflection, no codegen, no dynamic activation. The binder is straight-line
`GetValue` / `TryParse` calls; endpoints are wired through the already-AOT-safe
TCP convenience overloads.

## Default request-parse interceptors

### What it is

When the web host composes the `HttpConnectionListener`, it installs the
default request-parse interceptors **before** any user `UseServer`
configuration runs (`WebApplicationServerBuilder.ApplyDefaultInterceptors`).
Today that is one interceptor: `Http.RequestLimits`'
max-request-body-size interceptor, which occupies slot 0 of the interceptor
order so every HTTP/1.1 request carries the typed
`IHttpMaxRequestBodySizeFeature` and user-registered interceptors' head hooks
can observe it. HTTP/1.1 is currently the only protocol whose parse path
invokes interceptors — HTTP/2 and HTTP/3 exchanges do not carry the feature
yet (see the transport's protocol-coverage notes); wiring those paths is
tracked follow-up work.

### Why default-on, and why here

The transport itself stays lean — with zero interceptors it allocates no
per-request interception state at all — so the "h1 requests always have the
typed feature" guarantee is a *hosting* policy, not a transport one. It lives
here because this is the composition root: apps that want a leaner pipeline can
inspect or clear `HttpConnectionListenerOptions.Interceptors` in their own
`UseServer` callback (user configurations run after the defaults), which keeps
the default overridable without a dedicated opt-out knob.

### Non-goals

No other interceptor ships by default. Parse-time features under design
(digest fields, request decompression) register through the same seam when
their packages land, but each is an explicit opt-in.
