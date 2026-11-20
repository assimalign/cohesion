# Assimalign.Cohesion.Http.Proxy — Overview

The forwarding and tunneling mechanism a proxy is built from: send one exchange on to another
server and relay what comes back, or open a `CONNECT` tunnel and relay bytes.

## Purpose

A reverse proxy and a forward proxy differ in *policy*: which requests they accept and where they
send them. They share the *mechanism*: rewriting the request for the next hop, streaming the body,
relaying the response, passing a protocol upgrade through, and answering failures with the right
status. This package is that mechanism, with no routing and no policy, so both proxies in
`Assimalign.Cohesion.Web.Proxy` (and any other server built on the HTTP family) use one
implementation.

## Scope

- `IHttpForwarder` / `HttpForwarder.Create(invoker, options)` forwards an exchange to an absolute
  `http` or `https` URI through a borrowed `HttpMessageInvoker`:
  - drops the fields that belong to one connection (RFC 9110 §7.6.1), including the fields the
    `Connection` header names, and joins HTTP/2 cookie crumbs;
  - adds `X-Forwarded-*` or `Forwarded` (RFC 7239) as `HttpForwarderOptions` selects, and a `Via`
    entry under a pseudonym that also detects loops (508);
  - streams the request body and, when the exchange carries `IHttpResponseStreamingFeature`, the
    response body; otherwise it buffers the response up to a limit;
  - relays trailers where the client can receive them;
  - passes an HTTP/1.1 upgrade through, and carries a WebSocket over HTTP/2 or HTTP/3 (extended
    `CONNECT`) to the destination as an HTTP/1.1 upgrade;
  - answers each failure as a gateway should (502, 504, 400, 501) and reports it as an
    `HttpProxyResult`.
- `HttpForwarder.TryNormalizePath` and `HttpForwarder.CreateDestination` build the destination URI
  without letting a dot segment or an encoded slash route a request somewhere its route did not
  allow; `HttpForwarder.IsValidDestinationPrefix` checks a configured destination ahead of time.
- `IHttpTunnel` / `HttpTunnel.Create(options)` serves an HTTP/1.1 `CONNECT`: connects first (so a
  failure is a 502 or 504, not a tunnel that closes at once), accepts through the exchange's
  protocol upgrade, and relays with half-close.

## Dependencies

- `Assimalign.Cohesion.Http` — the exchange abstractions.
- `Assimalign.Cohesion.Http.ProtocolUpgrade` and `Assimalign.Cohesion.Http.ExtendedConnect` — the
  features that hand over the client's connection for upgrades and tunnels.
- `Assimalign.Cohesion.Http.Forwarded` — the effective client identity a trusted proxy in front of
  this one reported.
- `Assimalign.Cohesion.Http.Streaming` — relaying a response as it arrives.
- `System.Net.Http` (in the shared framework) for the outbound connection.

## Usage

```csharp
// Created once, shared by every exchange; the application owns and disposes the invoker.
HttpMessageInvoker invoker = new(new SocketsHttpHandler
{
    UseProxy = false,
    AllowAutoRedirect = false,
    UseCookies = false,
    AutomaticDecompression = DecompressionMethods.None,
    ActivityHeadersPropagator = null,
});
HttpForwarder forwarder = HttpForwarder.Create(invoker);

// Per exchange, inside a handler:
if (!HttpForwarder.TryNormalizePath(context.Request.Path, out HttpPath path))
{
    context.Response.StatusCode = HttpStatusCode.BadRequest;
    return;
}

Uri destination = HttpForwarder.CreateDestination(new Uri("http://orders.internal:8080"), path, context.Request.Query);
HttpProxyResult result = await forwarder.ForwardAsync(context, destination, context.RequestCancelled);
```

The result says how the exchange ended; the client has already been answered.

## Layering

L1 foundation library in the HTTP area: it references no hosting library and no resource area.
The routes, the forward proxy's access rules and the `Add`/`Use` verbs are Web concerns and ship in
`resources/Web/Assimalign.Cohesion.Web.Proxy`. See [DESIGN.md](DESIGN.md) for the request rewrite,
the failure model, the upgrade bridge and the diagnostics.
