# Assimalign.Cohesion.Http.Connections — Overview

Adapts Cohesion connection listeners into HTTP/1.1, HTTP/2 and HTTP/3 server exchanges. A host
receives `IHttpContext` requests and sends responses through the same contracts on every protocol.
This is the L1 transport implementation used by the Web runtime.

## Scope

The package owns HTTP framing, HPACK/QPACK, multiplexed stream handling, request and response body
streaming, protocol limits, timeouts, and graceful connection shutdown. Listener options select
protocols and configure their limits. Generic exchange interceptors let HTTP feature packages add
behavior such as streaming, protocol upgrades and extended CONNECT.

TLS is composed onto the underlying listener before registration. The transport publishes the
negotiated handshake from the connection's `Tls` feature as an `ITlsConnectionInfo` facet on HTTP's
connection-info snapshot; `Http.Tls` provides the
application accessor. Routing, authentication, form parsing and session policy belong to consumers.

## Dependencies

- `Assimalign.Cohesion.Http` supplies the request, response, context and interception contracts.
- `Assimalign.Cohesion.Connections` supplies stream and multiplexed connection-listener contracts.

Concrete TCP, TLS and QUIC listeners are supplied by the composition root. This package references
neither their implementations nor Web hosting. See the [Http area map](../../README.md).

## Usage

Create a listener with `HttpConnectionListener.Create` and register the already-composed transport
through `UseHttp1`, `UseHttp2`, `UseHttp1AndHttp2` or `UseHttp3`. Stream protocols accept an
`IConnectionListener`; HTTP/3 accepts an `IMultiplexedConnectionListener`. The dual HTTP/1.1 and
HTTP/2 registration requires TLS and chooses the protocol through ALPN.

A custom host binds the listener, accepts HTTP connections, opens their contexts, receives
exchanges, and finalizes each exchange exactly once with `SendAsync`. HTTP/1.1 exchanges run
sequentially; HTTP/2 and HTTP/3 permit concurrent exchanges. Web applications normally let
`Web.Hosting` own this lifecycle. Per-version callbacks configure limits; shared policies and
interceptors live on `HttpConnectionListenerOptions`.

Applications may assign any status code from 100 through 599. An unregistered
final code such as 299 is sent with an empty HTTP/1.1 reason phrase; HTTP/2 and
HTTP/3 carry only its digits in `:status`. Codes from 100 through 199 belong to
the interim-response seam, and 101 remains the HTTP/1.1 upgrade path's status.

Cleartext HTTP/2 rejects `:scheme https` with 421 before dispatch. A trusted gateway
that terminates TLS may set `AllowAlternateSchemes = true` to preserve its forwarded
scheme. Connection security still reflects the backend connection.

Final responses include a cached IMF-fixdate `Date` by default. The timestamp is
available before dispatch and preserves an application override. Set
`AddDateHeader = false` behind a proxy that adds Date; 1xx responses omit it. The option
suppresses only the automatic header. StaticFiles keeps Last-Modified and date validators:
it uses a valid response Date, or one clock read when Date is absent, invalid, or repeated,
without adding or replacing the header.

See [DESIGN.md](DESIGN.md) for listener composition examples, the host contract, protocol behavior,
limits, interception and resource ownership.

## Diagnostics

Enable the EventSource and Meter named `Assimalign.Cohesion.Http.Connections` to observe transport
rejections, protocol error codes, slow-reader aborts and client-caused response aborts. Events are
throttled and omit request text; metrics count every observed decision. See [Diagnostics](DESIGN.md#diagnostics)
for event payloads, instrument names, units and tags. Application request spans and duration remain
with the Web host.
