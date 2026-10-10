# Http area decisions

Architecture decisions for the Http family (`libraries/Http/**`) that span more than one package.
Each one records the situation, the options weighed and what the decision costs. The HTTP/Web
program plan (`docs/programs/HTTP_WEB_PROGRAM_PLAN.md`, §7.4) lists them with the owner's other
decisions.

- [ADR 1: Server WebSockets](#adr-1-server-websockets)
- [ADR 2: Trailers, decided apart from gRPC](#adr-2-trailers-decided-apart-from-grpc)

## ADR 1: Server WebSockets

- **Status:** Accepted, 2026-10-07.
- **Decided by:** the integrator, under the owner's standing delegation for the HTTP/Web program. It is reviewed at the end of Stage 10.
- **Records:** plan §7.4 decision 16, and the gate on #765.

### Context

What exists today:

- **HTTP/1.1.** `IHttpProtocolUpgrade.AcceptAsync` (`Http.ProtocolUpgrade`) writes `101` and hands the connection over as a duplex `Stream`. It is opt-in through `HttpProtocolUpgrade.CreateInterceptor()`, and Web.Hosting does not install it.
- **HTTP/2 and HTTP/3.** The transports recognize and validate extended CONNECT (RFC 8441, RFC 9220), and they always advertise `SETTINGS_ENABLE_CONNECT_PROTOCOL = 1`.
  - `IHttpExtendedConnectFeature` exposes only `:protocol`.
  - There is no tunnel: the client's bytes reach the request body, and the response ends the stream as any response does.
- **No RFC 6455 implementation exists.** `Http.ProtocolUpgrade`, `Http.ExtendedConnect` and `Http.Connections` record WebSocket framing as a non-goal "because Cohesion keeps no WebSocket surface". Nothing references `System.Net.WebSockets`.

Two facts force the scope:

- **Browsers prefer HTTP/2.** Chrome and Firefox carry a WebSocket over an existing HTTP/2 connection with RFC 8441 whenever the server advertises the setting. Since #1063, `UseHttps` endpoints negotiate HTTP/2 with browsers. A WebSocket that works only over HTTP/1.1 would therefore fail in browsers on exactly the endpoints that matter, unless HTTP/2 stops advertising extended CONNECT.
- **No origin check exists.** CORS does not apply to WebSockets (Web.Cors DESIGN). A browser sends cookies with a cross-site WebSocket handshake, so without an origin check any site can open an authenticated socket for its visitor (cross-site WebSocket hijacking).

A real-time hub framework (SignalR-style) waits on this decision, but it is a separate decision and is not in this program.

### Decision

1. **Cohesion supports server WebSockets on HTTP/1.1, HTTP/2 and HTTP/3.**
2. **Framing comes from the BCL.** `System.Net.WebSockets.WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, ... })` provides it, and applications receive a `System.Net.WebSockets.WebSocket`. Cohesion owns the bootstrap and the policy, and writes no frame codec.
3. **Two new packages:**
   - `Assimalign.Cohesion.Http.WebSockets` (libraries/Http) owns the protocol side:
     - the handshake on each protocol: `Sec-WebSocket-Key`, `Sec-WebSocket-Accept` and `Sec-WebSocket-Version` on HTTP/1.1, and extended CONNECT with `:protocol = websocket` on HTTP/2 and HTTP/3;
     - subprotocol selection;
     - permessage-deflate negotiation (RFC 7692), mapped onto `WebSocketDeflateOptions`;
     - the request surface: `IsWebSocketRequest`, the requested subprotocols, and an accept call that returns the `WebSocket`.

     It references Http, Http.ProtocolUpgrade and Http.ExtendedConnect. *(As built: #1316 moved `IHttpExtendedConnectFeature` into Http, and until #1368 the package referenced Http and Http.ProtocolUpgrade only, reading the feature from the exchange's features. #1368 returned the feature to Http.ExtendedConnect and restored this reference; the handshake reads `context.ExtendedConnect`.)*
   - `Assimalign.Cohesion.Web.WebSockets` (resources/Web) owns the policy: `UseWebSockets(options)` middleware for the origin policy, the keep-alive interval, and closing open WebSockets with `1001` when the server drains.
4. **The extended CONNECT tunnel ships in the transports.** `IHttpExtendedConnectFeature` gains an accept call on HTTP/2 and HTTP/3. It sends `200` and returns a duplex `Stream`:
   - reads deliver the client's DATA;
   - writes go out as DATA at once;
   - disposing the stream ends it.

   The transport installs the feature in the exchange's feature collection.

   *(As built since #1368, owner decision 20 of 2026-10-09: the transport installs no feature. It passes the validated `:protocol` to the request-parse hooks (`HttpExchangeInterceptorRequestContext.Protocol`) and offers the accept as a generic mechanism on its exchange control (`IHttpExchangeControl.CanAcceptTunnel` and `AcceptTunnelAsync`). `Http.ExtendedConnect`'s interceptor, `HttpExtendedConnect.CreateInterceptor()`, installs `IHttpExtendedConnectFeature` over it, the way `Http.ProtocolUpgrade` wraps `TakeOver`. The feature therefore exists only on a listener that registers the interceptor.)*
5. **Web.Hosting installs the HTTP/1.1 upgrade interceptor by default**, as it already does the request-size interceptor. A request that no application accepts is served exactly as before. *(Since #1368 it also installs the extended CONNECT interceptor, after the upgrade interceptor. A host that clears `options.Interceptors` loses WebSockets on all three protocols: the HTTP/2 and HTTP/3 transports keep advertising `SETTINGS_ENABLE_CONNECT_PROTOCOL`, but no feature surfaces the handshake.)*
6. **Cross-site WebSocket hijacking is refused by default.** A handshake whose `Origin` is present and is neither the request's own origin nor in `AllowedOrigins` gets `403`. A handshake without `Origin` (a non-browser client) passes.
7. **The drain closes WebSockets cleanly.** The default server publishes its drain signal to each exchange through a Web-root feature, in the same pattern as `IWebResponseCompletionFeature` (both moved to `Web.Server` by #1379, owner decision 33). Web.WebSockets closes open sockets with `1001 Going Away` when the drain begins, rather than letting the budget abort them without a close frame.

### Options considered

**A. BCL framing over a Cohesion bootstrap (chosen).** Complexity: low to medium (the handshakes, the tunnel and the middleware).
- **Pros:**
  - The RFC 6455 and RFC 7692 implementation is the one Kestrel uses: maintained, fuzzed and NativeAOT-compatible.
  - The masking, control-frame, close-code and UTF-8 rules come with it.
  - `WebSocket` is the type every .NET library already accepts.
- **Cons:**
  - Framing runs over a `Stream`, not pipelines.
  - Details such as keep-alive and close-code validation follow the BCL's choices.

**B. A Cohesion frame codec**, which is what #765's acceptance criteria first asked for. Complexity: high.
- **Pros:** pipeline-native, and fully under Cohesion's control.
- **Cons:**
  - It is a security-sensitive parser and compression state to own forever.
  - It duplicates a hardened BCL component.
  - It exposes a WebSocket type that no other library accepts.

**C. HTTP/1.1 only, and stop advertising extended CONNECT.** Complexity: low.
- **Cons:**
  - Browsers on an HTTP/2 connection have to open a separate HTTP/1.1 connection, which is slower and counts against their per-origin limits.
  - Clients that speak only HTTP/3 cannot connect at all.
  - It walks back extended CONNECT support that already ships.

### Trade-off analysis

WebSockets carry their protocol risk in framing and compression state, and that is exactly what the BCL has hardened. What is particular to Cohesion is the bootstrap on three transports, plus the policy: origins and the drain. Option A puts the work there.

Option B's one advantage is pipeline-native framing. WebSocket traffic is message-oriented, so the cost of the `Stream` adapter is small next to the cost of handling the messages.

### Consequences

**Easier:**
- Browser WebSockets work on every endpoint shape.
- A hub framework can later be decided on a working transport.

**Harder:**
- The extended CONNECT tunnel is new transport surface on HTTP/2 and HTTP/3: flow control and the end of the stream interact with an exchange that never finishes its body.
- The non-goal statements in Http.ProtocolUpgrade, Http.ExtendedConnect and Http.Connections are superseded and rewritten.

**Also:**
- App.Web gains Http.WebSockets, Web.WebSockets, Http.ProtocolUpgrade and Http.ExtendedConnect. Since #1368, Web.Hosting references Http.ExtendedConnect, so every other area framework that carries Web.Hosting lists it as a private member, beside Http.ProtocolUpgrade.
- Since #1368, each HTTP/2 or HTTP/3 WebSocket handshake builds the transport's response sink and exchange control, because the extended CONNECT interceptor reaches the control through the response phase. Ordinary exchanges keep the fast path.
- An open WebSocket holds its connection on HTTP/1.1, or its stream on HTTP/2 and HTTP/3, so it counts against `MaxConcurrentConnections` and the stream limits.
- **To revisit:** a hub framework, and per-message size limits beyond what the application reads.

### Action items

1. **Transports:** the extended CONNECT tunnel on HTTP/2 and HTTP/3.
2. **Packages:** Http.WebSockets and Web.WebSockets.
   - End-to-end tests run over all three protocols.
   - Raw-frame tests assert the RFC behavior: masking, control frames, close codes, UTF-8 and permessage-deflate.
3. **Wiring:**
   - the default upgrade interceptor in Web.Hosting;
   - App.Web membership, CI and the release inventory;
   - NativeAOT guard checks.

## ADR 2: Trailers, decided apart from gRPC

- **Status:** Accepted, 2026-10-07.
- **Decided by:** the integrator, under the owner's standing delegation for the HTTP/Web program. It is reviewed at the end of Stage 10.
- **Records:** plan §7.4 decision 18, and the Stage 10 row "HTTP/2 and HTTP/3 trailers" that waited on "a gRPC ADR".

### Context

Trailer fields (RFC 9110 §6.5) were parked with gRPC, their best-known consumer, and gRPC hosting is outside this program. The code on 2026-10-07:

- **Request trailers:**
  - HTTP/1.1 chunked requests and HTTP/3 parse, validate and expose `Request.Trailers`.
  - HTTP/2 appends a trailing HEADERS block to the request's header buffer after the head has been decoded, and never decodes it (`Http2Stream.AppendHeaderBytes`, decoded only in `CreateContextAsync`). HPACK is stateful, so a trailer field the client adds to the dynamic table leaves the server's decoder out of step for every later request on that connection.

  The HTTP/2 gap is a conformance defect, not a missing feature: RFC 9113 §4.3 requires every field block to be decoded.
- **Response trailers:** no protocol emits them.
  - `IHttpResponse.Trailers` exists and returns the unsupported collection everywhere.
  - The Http DESIGN, the OpenTelemetry DESIGN and the `IHttpResponse.Trailers` remarks all say otherwise.

### Decision

1. **Trailers are HTTP semantics,** decided apart from gRPC.
2. **HTTP/2 request trailers are decoded,** which keeps HPACK in step, and validated as HTTP/3's are: no pseudo-header and no field prohibited in trailers (`HttpFieldRules.IsProhibitedInTrailers`). They are then exposed on `Request.Trailers`. The gap is filed as a defect.
3. **Response trailers ship on HTTP/2 and HTTP/3** through the existing `IHttpResponse.Trailers`. There `IsSupported` is true, and the trailers go out as a HEADERS frame that ends the stream after the last DATA, on buffered and streaming responses alike.
4. **HTTP/1.1 keeps `IsSupported = false`.** A buffered HTTP/1.1 response carries `Content-Length`, and HTTP/1.1 clients rarely consume chunked trailers. Kestrel makes the same call.
5. **gRPC hosting stays outside this program.** It needs its own ADR on serialization (protobuf or code-first), and trailers no longer stand in its way.

### Options considered

**A. Decide trailers on their own and ship them (chosen).** Complexity: medium, and the work is localized.
- HTTP/2 streaming responses already end with an empty DATA frame carrying END_STREAM, which a trailing HEADERS frame replaces.
- HTTP/3 ends the stream after the last DATA frame, and the trailers go just before that end.

**B. Keep trailers waiting on gRPC.**
- **Cons:** it leaves the HPACK defect in place, though that defect has nothing to do with gRPC, and it keeps a public `Trailers` property that throws on every protocol.

**C. Decode HTTP/2 request trailers only.**
- **Pros:** it fixes the defect.
- **Cons:** the response contract keeps claiming a capability it lacks.

### Consequences

- The documentation drift is corrected: the Http DESIGN, the OpenTelemetry DESIGN, the `IHttpResponse.Trailers` remarks, and DigestFields' notes on trailers.
- Http.DigestFields can later send `Content-Digest` as a trailer on HTTP/2 and HTTP/3.
- **To revisit:** HTTP/1.1 response trailers, if a consumer appears; gRPC hosting.

### Action items

1. **Fix the HTTP/2 request-trailer defect.** The regression test adds a trailer field to the dynamic table and then sends a second request on the same connection.
2. **Ship response trailers on HTTP/2 and HTTP/3.** Tests cover buffered and streaming responses, and the docs are corrected.
