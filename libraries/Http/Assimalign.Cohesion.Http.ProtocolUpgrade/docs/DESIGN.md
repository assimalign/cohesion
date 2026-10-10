# Assimalign.Cohesion.Http.ProtocolUpgrade — Design

## Purpose

Model the HTTP/1.1 *connection transition* mechanisms — an RFC 9110 §7.8 protocol
**upgrade** (`Connection: upgrade` + `Upgrade`, answered with `101 Switching Protocols`) and an
RFC 9110 §9.3.6 **CONNECT** tunnel (answered with `200 OK`) — as an explicit, opt-in capability
on `IHttpContext`. An application detects the transition through `context.Upgrade`, accepts it,
and receives the raw duplex transport stream to drive the negotiated protocol or the tunnel. The
WebSocket handshake of `Assimalign.Cohesion.Http.WebSockets` is the main consumer: it accepts the
upgrade and hands the stream to the BCL's RFC 6455 framing.

This is the HTTP/1.1 counterpart to the sibling `Assimalign.Cohesion.Http.ExtendedConnect`
package, which models the HTTP/2 / HTTP/3 extended-CONNECT (`:protocol`) bootstrap. HTTP/2 and
HTTP/3 removed the `Upgrade` mechanism (RFC 9113 §8.6, RFC 9114 §4.2), so this package is
HTTP/1.1 only.

## The defining constraint, and the design it produced (#751)

**The transport (`Assimalign.Cohesion.Http.Connections`) must not reference this package** — it
deliberately dropped that dependency in commit `4c21d75`. The bridge back was rebuilt so that the
*entire* upgrade functionality lives here, wired through the transport's two generic interceptor
seams (the same seams `Http.RequestLimits` and `Http.Streaming` consume):

1. **Detection** — an `IHttpExchangeInterceptor`. `AfterRequestHead` sees the parsed head
   (`Version`, `Method`, read-only `Headers`) before dispatch and records a matched transition as
   an internal `HttpProtocolUpgradeCandidate` feature. HTTP/1.1 only — the request-parse seam
   runs on every protocol version, so the hook checks `Version` itself; CONNECT is `Method ==
   CONNECT` (the transport's `HttpRequestTarget` parser already enforces CONNECT ⇒
   authority-form); an upgrade requires **both** a `Connection: upgrade` token and a non-empty
   `Upgrade` header (a bare `Upgrade` header is not actionable, per §7.8). CONNECT takes
   precedence — §7.8 requires ignoring `Upgrade` on CONNECT. Both headers are comma lists whose
   elements lose SP and HTAB only (RFC 9110 §5.6.1, §5.6.3). The transport decodes field values as
   Latin-1, so a no-break space (`0xA0`) or a next-line octet (`0x85`) can end a token, and a
   Unicode trim read `Upgrade: websocket\xA0` as `websocket` and `Connection: upgrade\xA0` as the
   upgrade option, while a hop comparing exactly sees neither (#1341). A matched transition also
   adds the interceptor to that exchange's response phase
   (`HttpExchangeInterceptorRequestContext.AddResponseInterceptor`), which is what step 2 runs in.
2. **Materialization** — an `IHttpExchangeInterceptor`. `BeforeResponse` runs at the setup of
   each exchange step 1 joined — after the head is parsed, before the application handler —
   sharing the same feature collection. It consumes the candidate and, when the transport's **exchange control**
   (`HttpExchangeInterceptorResponseContext.Control`, the generic core `IHttpExchangeControl` surface)
   can surrender the connection (`CanTakeOver`), installs the public
   `IHttpProtocolUpgradeFeature` wrapping an `Http1ProtocolUpgrade`. A missing control, or a
   control whose `CanTakeOver` reads `false` (an exchange that cannot surrender its connection —
   HTTP/2 / HTTP/3 multiplexed streams), degrades to *no feature* — `context.Upgrade` reads
   `null` rather than surfacing an upgrade whose accept could never work.

One stateless class (`HttpProtocolUpgradeInterceptor`) implements both hooks; per-exchange state
crosses seams only through the feature collection, per the interceptor contract. A host enables
the whole capability by registering the pair:

```csharp
options.Interceptors.Add(HttpProtocolUpgrade.CreateInterceptor());
```

The transport installs nothing: a host registers the interceptor on each listener it wants
upgrades on. The Web host (`Web.Hosting`) registers it on every listener by default, after the
request-size interceptor, so a WebSocket handshake works with no listener configuration
(decision 16, [Http ADR 1](../../../../docs/libraries/Http/DECISIONS.md#adr-1-server-websockets));
a request that no application accepts is served exactly as before. Neither the core
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

### The cost of a default-on interceptor

Step 2 needs the exchange control, and the transport builds an exchange's response sink and
exchange control only when some interceptor takes part in its response phase. Declaring
`HttpInterceptorScopes.All` would make that every exchange, on every protocol version, because
the Web host registers this interceptor on every listener. So the interceptor declares
`HttpInterceptorScopes.Request` and joins the response phase only of the exchange whose head asked
for a transition (step 1). An ordinary HTTP/1.1 request, and every HTTP/2 and HTTP/3 request
(including an extended CONNECT WebSocket, which `Http.ExtendedConnect`'s interceptor carries the
same way, over the control's `AcceptTunnelAsync`), costs one
version check and, on HTTP/1.1, a method check and a `Connection` header lookup. The transport's
`HttpExchangeResponseInterceptorTests` pin the fast path on all three versions and the takeover
on an upgrade; the Web.Hosting DESIGN records the measured allocations.

## Shape

- `IHttpProtocolUpgrade` — the public contract for an available transition: `Kind`
  (`Upgrade` / `Connect`), `Protocol` (the requested `Upgrade` token, `null` for CONNECT), and
  `AcceptAsync`.
- `HttpProtocolUpgradeKind` — the transition discriminator (`None` / `Upgrade` / `Connect`).
- `IHttpProtocolUpgradeFeature` — the feature slot carrying the exchange's upgrade.
- `HttpContextProtocolUpgradeExtensions` — `context.Upgrade`, a plain nullable feature read (the
  interceptors install the feature eagerly, so the accessor allocates nothing and never throws
  for ordinary exchanges).
- `HttpProtocolUpgrade` — the public entry point: `CreateInterceptor()` (mirrors
  `HttpResponseStreaming.CreateInterceptor()`); the one instance declares the request
  scope (`HttpInterceptorScopes.Request`) and joins the response phase of a transition's
  exchange only (see "The cost of a default-on interceptor").
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
2. **Encodes the head before anything else changes (#1183)**: the status line, the response
   headers and cookies the application set before accepting (e.g. `Sec-WebSocket-Accept`), less
   `Content-Length` and `Transfer-Encoding` — RFC 9112 §6.3, RFC 9110 §15.2.2 (a 101 carries no body framing) and
   RFC 9110 §9.3.6 (a successful CONNECT response must not include them) — then
   `Connection: Upgrade` + `Upgrade: <protocol>` for an upgrade, or no `Connection` header for a
   CONNECT (the tunnel persists — `close` applies to HTTP framing, not the tunnel). The status is
   always the RFC-standard one (the interceptor materials do not expose the application's status
   code, and 101/200 are what §7.8 / §9.3.6 prescribe). Each field line is checked against the core
   field rule as it is encoded (`HttpFieldNormalization`): a name that is not a token, or a value
   holding CR, LF, NUL, or another control character but HTAB, throws an `HttpException` with
   `HttpErrorCode.InvalidResponseField` — the rule the transport's head writers apply, so a value
   reflected from the request cannot split the response (CWE-113). The refusal comes before the
   takeover: nothing is written, the connection is still the transport's, and the response headers
   are as the application staged them, so the exchange can still be answered with an ordinary
   response (`Web.Hosting` answers the resulting fault with a `500`). The accept is spent either way.
3. **Claims the connection** — `IHttpExchangeControl.TakeOver()`. From that instant the
   transport suppresses its own response for the exchange and ends keep-alive, so even a
   cancelled or failed head write cannot be followed by a second HTTP response on a
   desynchronized stream. The takeover is itself one-shot, so two features can never both claim
   a connection.
4. Applies the same field rules to the live response headers, so the exchange records the head
   that was sent, then writes the encoded head to the surrendered raw stream.
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

- **No protocol beyond the transition.** This package writes the `101` and surrenders the stream;
  what runs over it is the consumer's. WebSockets are `Assimalign.Cohesion.Http.WebSockets`, which
  validates the RFC 6455 handshake, stages `Sec-WebSocket-Accept` and its negotiated fields before
  accepting through `context.Upgrade`, and hands the stream to the BCL's
  `WebSocket.CreateFromStream` (decision 16, Http ADR 1).
- **No client-side initiation.** Server-side accept surface only.
- **No HTTP/2 / HTTP/3 upgrade.** Those versions removed `Upgrade`; their bootstrap is extended
  CONNECT (`Assimalign.Cohesion.Http.ExtendedConnect`).
- **No installation by the transport.** A host registers the interceptor per listener; the Web host
  does so by default.

## AOT posture

Pure managed code — no reflection, no dynamic code generation, no runtime type inspection.
Detection is token scanning over parsed headers; the accept path is string and buffer work over
the surrendered stream. Trimming- and NativeAOT-safe.
