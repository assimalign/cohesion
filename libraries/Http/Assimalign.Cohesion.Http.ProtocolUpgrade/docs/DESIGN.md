# Assimalign.Cohesion.Http.ProtocolUpgrade — Design

## Purpose

Model the HTTP/1.1 *connection transition* mechanisms — an RFC 9110 §7.8 protocol
**upgrade** (`Connection: upgrade` + `Upgrade`, answered with `101 Switching Protocols`) and an
RFC 9110 §9.3.6 **CONNECT** tunnel (answered with `200 OK`) — as an explicit, opt-in capability
on `IHttpContext`. An application detects the transition through `context.Upgrade`, accepts it,
and receives the raw duplex transport stream to drive the negotiated protocol (for example the
WebSocket handshake, #765) or the tunnel.

This is the HTTP/1.1 counterpart to the sibling `Assimalign.Cohesion.Http.ExtendedConnect`
package, which models the HTTP/2 / HTTP/3 extended-CONNECT (`:protocol`) bootstrap. HTTP/2 and
HTTP/3 removed the `Upgrade` mechanism (RFC 9113 §8.6, RFC 9114 §4.2), so this package is
HTTP/1.1 only.

## The defining constraint, and the design it produced (#751)

**The transport (`Assimalign.Cohesion.Http.Connections`) must not reference this package** — it
deliberately dropped that dependency in commit `4c21d75`. The bridge back was rebuilt so that the
*entire* upgrade functionality lives here, wired through the transport's two generic interceptor
seams (the same seams `Http.RequestLimits` and `Http.Streaming` consume):

1. **Detection** — an `IHttpRequestInterceptor`. `AfterRequestHead` sees the parsed head
   (`Version`, `Method`, read-only `Headers`) before dispatch and records a matched transition as
   an internal `HttpProtocolUpgradeCandidate` feature. HTTP/1.1 only — the request-parse seam
   runs on every protocol version, so the hook checks `Version` itself; CONNECT is `Method ==
   CONNECT` (the transport's `HttpRequestTarget` parser already enforces CONNECT ⇒
   authority-form); an upgrade requires **both** a `Connection: upgrade` token and a non-empty
   `Upgrade` header (a bare `Upgrade` header is not actionable, per §7.8). CONNECT takes
   precedence — §7.8 requires ignoring `Upgrade` on CONNECT.
2. **Materialization** — an `IHttpResponseInterceptor`. `BeforeResponse` runs per exchange at
   exchange setup — after the head is parsed, before the application handler — sharing the same
   feature collection. It consumes the candidate and, when the transport's **exchange control**
   (`HttpResponseInterceptorContext.Control`, the generic core `IHttpExchangeControl` surface)
   can surrender the connection (`CanTakeOver`), installs the public
   `IHttpProtocolUpgradeFeature` wrapping an `Http1ProtocolUpgrade`. A missing control, or a
   control whose `CanTakeOver` reads `false` (an exchange that cannot surrender its connection —
   HTTP/2 / HTTP/3 multiplexed streams), degrades to *no feature* — `context.Upgrade` reads
   `null` rather than surfacing an upgrade whose accept could never work.

One stateless class (`HttpProtocolUpgradeInterceptor`) implements both hooks; per-exchange state
crosses seams only through the feature collection, per the interceptor contract. A host enables
the whole capability by registering the pair:

```csharp
options.RequestInterceptors.Add(HttpProtocolUpgrade.CreateRequestInterceptor());
options.ResponseInterceptors.Add(HttpProtocolUpgrade.CreateResponseInterceptor());
```

Nothing is default-installed: like streaming, upgrades are opt-in per listener. Neither the core
nor the transport carries an upgrade-specific type — the core owns the generic seam
(`IHttpExchangeControl`, the single per-exchange control surface, of which takeover is one
capability), the transport implements it per protocol version and owns the raw-stream handover
mechanics, and this package owns every upgrade semantic.

### Why detection is not the transport's job

An earlier design had the transport detect the §7.8 signal and install a bridge feature through
a core contract. The interceptor seams make that unnecessary: detection is pure token scanning
over the already-parsed head — exactly what `AfterRequestHead` exists for — so moving it here keeps
the transport's parser free of feature policy and keeps every RFC-semantics decision (what counts
as a transition, which token wins, what the response looks like) in one reviewable place. The
transport keeps only what is physically transport's: CONNECT body-framing at parse time
(RFC-mandated wire behavior) and the raw-stream surrender machinery.

## Shape

- `IHttpProtocolUpgrade` — the public contract for an available transition: `Kind`
  (`Upgrade` / `Connect`), `Protocol` (the requested `Upgrade` token, `null` for CONNECT), and
  `AcceptAsync`.
- `HttpProtocolUpgradeKind` — the transition discriminator (`None` / `Upgrade` / `Connect`).
- `IHttpProtocolUpgradeFeature` — the feature slot carrying the exchange's upgrade.
- `HttpContextProtocolUpgradeExtensions` — `context.Upgrade`, a plain nullable feature read (the
  interceptors install the feature eagerly, so the accessor allocates nothing and never throws
  for ordinary exchanges).
- `HttpProtocolUpgrade` — the public entry point: `CreateRequestInterceptor()` /
  `CreateResponseInterceptor()` (mirrors `HttpResponseStreaming.CreateInterceptor()`).
- Internal: `HttpProtocolUpgradeInterceptor` (both hooks), `HttpProtocolUpgradeCandidate`
  (parse-time marker), `Http1ProtocolUpgrade` (accept path), `HttpProtocolUpgradeFeature`
  (feature holder). Interface-first: all implementations are internal.

The package surfaces its types under the `Assimalign.Cohesion.Http` namespace (not the assembly
name) so the `IHttpContext` extension members are discoverable without an extra `using` —
recorded as a deliberate deviation in the csproj, matching `Http.ExtendedConnect`.

## The accept path

`AcceptAsync` is single-shot (an `Interlocked` guard throws `InvalidOperationException` on a
second call **before any byte is written**, so a second response can never reach the wire). It:

1. Resolves the status line (101 for Upgrade, 200 for CONNECT) before side effects.
2. **Claims the connection first** — `IHttpExchangeControl.TakeOver()`. From that instant the
   transport suppresses its own response for the exchange and ends keep-alive, so even a
   cancelled or failed head write cannot be followed by a second HTTP response on a
   desynchronized stream. The takeover is itself one-shot, so two features can never both claim
   a connection.
3. Scrubs `Content-Length` / `Transfer-Encoding` unconditionally — RFC 9112 §9.9 (a 101 carries
   no body framing) and RFC 9110 §9.3.6 (a successful CONNECT response must not include them).
4. Writes the head to the surrendered raw stream: `Connection: Upgrade` +
   `Upgrade: <protocol>` for an upgrade; the `Connection` header removed for a CONNECT (the
   tunnel persists — `close` applies to HTTP framing, not the tunnel). Response headers and
   cookies the application set before accepting ride along (e.g. `Sec-WebSocket-Accept`); the
   status is always the RFC-standard one (the interceptor materials do not expose the
   application's status code, and 101/200 are what §7.8 / §9.3.6 prescribe).
5. Returns the raw stream. The caller owns I/O on it; the transport still owns the underlying
   connection's disposal when the server's connection scope ends.

### Why handing over the raw stream is safe

The HTTP/1.1 parser reads the request line and headers byte-by-byte, skips body framing for
CONNECT, and reads no body for a bodyless upgrade `GET` — no post-transition octet is ever
buffered. The surrendered stream therefore starts exactly at the tunnel / negotiated-protocol
boundary: octets the client pipelined behind the handshake are readable from the returned
stream, never consumed by the parser. (This invariant is documented as load-bearing in the
transport's DESIGN.md.)

## Non-goals

- **No WebSocket framing surface.** This package surrenders the stream after the handshake; the
  RFC 6455 framing/codec layer is the WebSocket feature's concern (#765), which builds its
  HTTP/1.1 handshake on `context.Upgrade`.
- **No client-side initiation.** Server-side accept surface only.
- **No HTTP/2 / HTTP/3 upgrade.** Those versions removed `Upgrade`; their bootstrap is extended
  CONNECT (`Assimalign.Cohesion.Http.ExtendedConnect`).
- **No default installation.** Hosts opt in per listener by registering the interceptor pair.

## AOT posture

Pure managed code — no reflection, no dynamic code generation, no runtime type inspection.
Detection is token scanning over parsed headers; the accept path is string and buffer work over
the surrendered stream. Trimming- and NativeAOT-safe.
