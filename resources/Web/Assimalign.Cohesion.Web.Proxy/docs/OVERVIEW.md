# Assimalign.Cohesion.Web.Proxy — Overview

Reverse and forward proxies for Cohesion web applications. A reverse proxy is a gateway in front
of other services: its routes decide which requests it forwards, and to where. A forward proxy is
an egress point for clients: its rules decide which destinations those clients may reach, by
absolute-form request or `CONNECT` tunnel. Both are policy over the forwarder and tunnel in
[`Assimalign.Cohesion.Http.Proxy`](../../../../libraries/Http/Assimalign.Cohesion.Http.Proxy/docs/OVERVIEW.md),
which rewrites, streams and relays the exchange.

## Scope

- **`AddReverseProxy` and `UseReverseProxy()`.** The registration verb on `builder.Services`
  validates the routes (`ReverseProxyBuilder.Options.AddRoute`) and registers the application's
  `reverse-proxy` setting. The pipeline verb places the middleware, which forwards each request
  the most specific route takes and passes every other request to the next middleware.
- **`ReverseProxyRoute`.** Host patterns (exact, `*.suffix`, `*`; none means any host), a path
  prefix matched on segment boundaries, one or more destinations taken in turn, prefix stripping,
  and whether the forwarded request keeps the client's `Host`.
- **`AddForwardProxy` and `UseForwardProxy()`.** The registration verb validates the rules
  (`ForwardProxyBuilder.Options.AddRule`) and registers the application's `forward-proxy`
  setting. The pipeline verb places the middleware, which is terminal: every request that reaches
  it is a proxy request.
- **`ForwardProxyRule`.** Host patterns and ports. No rule, no access (403). A destination
  reached only through a wildcard pattern never connects to a loopback, link-local or unspecified
  address, so a name pointed at the proxy's host or at a cloud metadata address cannot reach it.
- **Settings an operator can change.** Each setting's value is its route or rule list, which a
  whole-value `Set` replaces while the application runs, validated before anything changes. Each
  setting owns its connection pool, and the application disposes it.
- **Refusal events.** The `Assimalign.Cohesion.Web.Proxy` event source reports each destination
  the forward proxy refuses, with the reason, host and port, and counts them
  (`refused-requests`, `refused-connections`). Forwarding failures are Http.Proxy's events.

## Dependencies

`Assimalign.Cohesion.Web` (pipeline and setting seams), `Assimalign.Cohesion.Web.Server` (the
request id its refusal events carry), `Assimalign.Cohesion.Http.Proxy` (the forwarder and tunnel),
`Assimalign.Cohesion.Http.Forwarded` (the effective host a route matches),
`Assimalign.Cohesion.Http.ExtendedConnect` (to tell a WebSocket's extended `CONNECT` from a tunnel
request) and `Assimalign.Cohesion.Http`. No DI, configuration binding or logging: registration is
dependency-free, as the Web-area rules require.

## Usage

A gateway that forwards `/api` to two backends and everything for `shop.example.com` to a third:

```csharp
builder.Services.AddReverseProxy(proxy => proxy.Options
    .AddRoute(new ReverseProxyRoute
    {
        Name = "api",
        PathPrefix = "/api",
        StripPrefix = true,
        Destinations = [new Uri("http://orders-1.internal:8080/"), new Uri("http://orders-2.internal:8080/")],
    })
    .AddRoute(new ReverseProxyRoute
    {
        Name = "shop",
        Hosts = ["shop.example.com"],
        Destinations = [new Uri("http://shop.internal/")],
    }));

WebApplication app = builder.Build();
app.UseErrorHandling();
app.UseReverseProxy();
// ...a request no route takes goes on to the rest of the pipeline...
```

An egress proxy on its own port that lets clients reach one partner API and any host under a
domain, on the default ports:

```csharp
builder.Services.AddForwardProxy(proxy => proxy.Options
    .AddRule(new ForwardProxyRule { Hosts = ["api.partner.example"] })
    .AddRule(new ForwardProxyRule { Hosts = ["*.example.com"], Ports = [443] }));

WebApplication app = builder.Build();
app.UseHttpLogging();
app.UseWhen(context => context.ConnectionInfo.LocalPort == 3128, proxy => proxy.UseForwardProxy());
```

`UseReverseProxy` goes inside the exception boundary, after `UseForwardedHeaders` and
`UseHostFiltering`, and ahead of `UseStatusCodePages`; `UseForwardProxy` goes in a branch or on a
port of its own, right after `UseHttpLogging`. The area's
[middleware order](../../../../docs/resources/Web/MIDDLEWARE_ORDER.md) gives the reasons, and
[DESIGN.md](DESIGN.md) records the design.
