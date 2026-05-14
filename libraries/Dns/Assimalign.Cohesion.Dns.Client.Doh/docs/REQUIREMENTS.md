# Assimalign.Cohesion.Dns.Client.Doh &mdash; Implementation requirements

> **Status: deferred.** This package ships as an empty placeholder
> until `Assimalign.Cohesion.Http.ClientFactory` is feature-complete.
> This document is the contract the implementing PR must satisfy.

## Standards

Primary: **[RFC 8484](https://www.rfc-editor.org/rfc/rfc8484)** &mdash;
"DNS Queries over HTTPS (DoH)".

Supplementary:
- **RFC 7540 / RFC 9113** &mdash; HTTP/2 (the default DoH binding).
- **RFC 9114** &mdash; HTTP/3 (optional, encouraged if the host
  `HttpClient` negotiates it).
- **RFC 8467** &mdash; padding for encrypted DNS (recommended for
  DoH, optional behind a transport flag).

## Public surface contract

The implementing PR MUST deliver:

```csharp
namespace Assimalign.Cohesion.Dns;

public sealed class DohDnsTransport : DnsTransport
{
    public DohDnsTransport(DohDnsTransportOptions options);

    public override EndPoint Endpoint { get; }
        // Surfaced as a DnsHttpEndPoint wrapping the Uri so the
        // DnsTransport base contract holds without forcing callers
        // to think about IP/Port for a URL-addressed transport.

    public override Task<ReadOnlyMemory<byte>> ExchangeAsync(
        ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken = default);

    protected override ValueTask DisposeAsyncCore();
}

public sealed class DohDnsTransportOptions
{
    // Required. RFC 8484 §3 URI template; query parameter handling
    // applies only when Method = HttpMethod.Get.
    public Uri Endpoint { get; set; }

    // Required. Resolves an HttpClient by logical name from the
    // Cohesion HTTP client factory. The host configures auth,
    // retries, proxies, HTTP/3 negotiation, etc. on that named
    // client &mdash; DoH itself only takes the resulting pipeline.
    public IHttpClientFactory HttpClientFactory { get; set; }
    public string HttpClientName { get; set; } = "DnsDoh";

    // GET (RFC 8484 §4.1.1, base64url-encoded ?dns= parameter, more
    // cacheable) vs POST (§4.1.2, application/dns-message body).
    // POST is the default for size headroom and cacheability
    // neutrality at the transport layer.
    public HttpMethod Method { get; set; } = HttpMethod.Post;

    public TimeSpan QueryTimeout { get; set; } = TimeSpan.FromSeconds(5);

    // RFC 8467 padding. Off by default to keep wire size minimal;
    // turn on when running over a network where size-correlated
    // traffic analysis matters.
    public bool PadRequests { get; set; }
}
```

The `Endpoint` property exposed as `EndPoint` requires either a
small `DnsHttpEndPoint : EndPoint` shim or a documented decision
that DoH returns `Endpoint = null` for the base contract and the
URL lives only on the options. Recommend the shim.

Plus a factory-builder extension on whatever shape the resolver
builder ends up with:

```csharp
public static IDnsResolverBuilder AddDohDnsTransport(
    this IDnsResolverBuilder builder,
    Action<DohDnsTransportOptions> configure);
```

## Behavior

The transport MUST:

1. Resolve an `HttpClient` from `HttpClientFactory` keyed by
   `HttpClientName` for every exchange. The factory is responsible
   for lifecycle &mdash; do **not** cache the `HttpClient` in
   `DohDnsTransport` (Cohesion's factory may rotate handlers).
2. Build the request per the configured `Method`:
   - **POST**: `Content-Type: application/dns-message`, body = the
     raw DNS wire message.
   - **GET**: append `?dns=<base64url(message)>`. Strip any
     pre-existing `?dns=` segment from `Endpoint.Query` before
     appending.
3. Set `Accept: application/dns-message`.
4. Set `Cache-Control: no-cache` for queries the caller has marked
   transient (see resolver design); otherwise let the response
   `Cache-Control` flow through to the DNS cache TTL adjustment
   logic.
5. Validate the response:
   - HTTP status 200 &rarr; parse body as DNS wire.
   - HTTP status 4xx / 5xx &rarr; `DnsException` with
     `DnsErrorCode.Transport` (carry the status code in the
     exception data).
   - Any `Content-Type` other than `application/dns-message`
     &rarr; `DnsErrorCode.Malformed`.
6. Honour the cancellation token at every async wait.
7. Apply RFC 8467 padding when `PadRequests = true`. Pad to the
   nearest 128-octet boundary using the EDNS Padding option
   (option code 12, RFC 7830) and let the host pipeline pad the
   response on its own.
8. Map every failure to `DnsException`:
   - HTTP error status &rarr; `DnsErrorCode.Transport`
   - Wrong content type / empty body &rarr; `DnsErrorCode.Malformed`
   - Timeout &rarr; `DnsErrorCode.Timeout`
   - TLS handshake failure &rarr; `DnsErrorCode.Transport`
     (the host `HttpClient` raised it; we just wrap)

## Dependencies

The package MUST depend on:

- `Assimalign.Cohesion.Dns.Client` (for the `DnsTransport` base and
  the resolver-builder integration).
- `Assimalign.Cohesion.Http.ClientFactory` (for
  `IHttpClientFactory` &mdash; this is the entire reason the DoH
  package is separate from `Dns.Client`).

The package MUST NOT depend on `Microsoft.Extensions.Http`. The
Cohesion HTTP client factory is the contract surface; consumers
who want `Microsoft.Extensions.Http` semantics can adapt via the
factory.

## Test plan

1. **Round-trip against a real DoH resolver**
   (`https://cloudflare-dns.com/dns-query`,
   `https://dns.google/dns-query`): opt-in integration test, gated
   on a `DnsLiveTests=true` env var. Tests both GET and POST.
2. **Stub `HttpMessageHandler` returns a canned 200 response**:
   asserts request `Method`, `Content-Type`, `Accept`, and body
   bytes match the expected query encoding for both GET and POST
   modes.
3. **Server returns 4xx &rarr; `DnsErrorCode.Transport`**.
4. **Server returns 200 + wrong content type &rarr;
   `DnsErrorCode.Malformed`**.
5. **Server returns 200 + truncated body &rarr;
   `DnsErrorCode.Malformed`** (the resolver should not retry over
   TCP &mdash; DoH already runs on a reliable transport).
6. **Cancellation mid-request**: asserts
   `OperationCanceledException` (not `DnsException`).
7. **RFC 8467 padding**: when `PadRequests = true`, asserts the
   serialized request includes an OPT record with a Padding option
   bringing the total to a 128-octet multiple.
8. **AOT analyzer clean** under
   `<IsAotCompatible>true</IsAotCompatible>`.

## Open questions for the implementer

- **Endpoint surfacing**: introduce `DnsHttpEndPoint : EndPoint`
  for the base-class `Endpoint` property, or accept that DoH
  doesn't fit the IP/Port mental model and document the override
  semantics. Recommendation: introduce the shim &mdash; it keeps
  `DnsTransport` symmetric across all four transports.
- **HTTP/3 negotiation**: leave entirely to the host
  `HttpClient` (it'll negotiate via ALPN) or expose a transport
  option that forces `HttpVersion = 3.0`? Default to letting the
  host pipeline decide.
- **Response caching**: DoH responses carry HTTP `Cache-Control`
  headers; the DNS layer carries TTL. RFC 8484 §5.1 says the lower
  of the two wins. Decide alongside the resolver cache design
  (Story L01.01.08.06.01).
- **Padding strategy**: RFC 8467 recommends padding strategies
  (block-length 128). The first implementation should ship with
  the recommended strategy and expose a hook for callers who want
  to override it.

## Non-goals

- Acting as a DoH *server*. Server-side resolution lives under the
  future `Assimalign.Cohesion.Dns.Authority` package (Feature
  `.07` of the L01.01.08 epic, currently deferred).
- Bringing its own HTTP stack. The transport composes on top of
  whatever pipeline `IHttpClientFactory` resolves &mdash; auth,
  retries, proxy resolution, telemetry are the host's
  responsibility.
- Implementing JSON-flavored DoH (Cloudflare/Google's
  `application/dns-json` extension). RFC 8484 wire format only.
