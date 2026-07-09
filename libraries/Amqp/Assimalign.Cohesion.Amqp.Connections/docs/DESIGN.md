# Assimalign.Cohesion.Amqp.Connections — Design

## Design intent

This library is the AMQP 1.0 *transport layer*: protocol-header negotiation, frame
framing/encoding for the AMQP and SASL protocol phases, the performative and message wire
format, and the server/client establishment surfaces that layer all of that over a carrier
connection produced by the `Assimalign.Cohesion.Connections` contracts.

It deliberately stops at the wire. The performatives (`Open`, `Begin`, `Attach`, `Flow`,
`Transfer`, `Disposition`, `Detach`, `End`, `Close`, and the SASL family) are modeled as plain
DTOs that encode and decode faithfully — but nothing in this package *acts* on them. Session
windows, link credit, delivery settlement, container/broker semantics, and SASL mechanism
execution are orchestration concerns that belong to the sibling `Assimalign.Cohesion.Amqp`
library (currently a placeholder project; this package is its intended substrate).

## The carrier model

The transports consume connections; they never implement or extend the connection contracts.
Two carrier shapes are supported, chosen structurally by constructor overload:

| Carrier shape | Server side | Client side | AMQP byte stream |
|---|---|---|---|
| Single stream | `IConnectionListener` | `IConnectionFactory` + `EndPoint` | the accepted/dialed `IConnection` itself |
| Multiplexed | `IMultiplexedConnectionListener` | `IMultiplexedConnectionFactory` + `EndPoint` | one bidirectional stream of the `IMultiplexedConnection` |

Key decisions:

- **Capability gating, never protocol identity.** AMQP 1.0 framing assumes a reliable, ordered
  byte stream, so both transports require
  `Delivery == Stream && IsReliable && IsOrdered` of the carrier's `ConnectionCapabilities`
  (`AmqpCarrierCapabilityExtensions.ThrowIfNotAmqpCarrier`). `ConnectionProtocol` is
  diagnostics-only and is never branched on — TCP, Unix domain sockets, an in-memory test
  transport, or a future userspace reliable-UDP protocol all qualify by capability alone.
- **Gate at construction, not at accept.** The capability mismatch is a composition error, so
  it surfaces as an `ArgumentException` from the transport constructor (at the composition
  root) rather than failing the first `AcceptAsync`/`ConnectAsync` at runtime.
- **One AMQP connection per multiplexed carrier, one stream per connection.** AMQP 1.0 is
  defined over a single ordered byte stream, so a multiplexed carrier (QUIC-shaped) maps to
  exactly one bidirectional stream: the client side *opens* it
  (`OpenStreamAsync(ConnectionDirection.Bidirectional)`), the server side *accepts* it
  (`AcceptStreamAsync`). Acquisition is deferred to context open (see Lifecycle). Carrying
  multiple AMQP connections over one multiplexed carrier is an explicit non-goal until a
  concrete consumer needs it.

## Surface shape

- **Interface-first with guided abstract bases** (per the repository pattern):
  `IAmqpConnection` / `AmqpConnection` and `IAmqpConnectionContext` / `AmqpConnectionContext`
  are the contracts plus guided bases; the concrete implementations
  (`AmqpSingleStreamTransportConnection`, `AmqpMultiplexedTransportConnection`,
  `AmqpTransportConnectionContext`) are `internal`. The bases expose the concrete-typed
  members (`Open()`/`OpenAsync()` returning `AmqpConnectionContext`) and implement the
  interface members explicitly, forwarding to them.
- **`AmqpServerTransport` / `AmqpClientTransport` are sealed composition-root classes**, not
  extension points. They are the entry-point objects an application constructs over a
  listener/factory — the AMQP analog of the HTTP listener surface — and there is no
  implementation variance to abstract over.
- **The context *is* a duplex pipe** (`IAmqpConnectionContext : IDuplexPipe`), mirroring the
  `IConnection : IDuplexPipe` flattening in the Connections design: `Input` is what the peer
  sent, `Output` is what you send, and `AsStream()` adapts lazily via the shared
  `DuplexPipeStream` for stream-based consumers. No bespoke pipe wrapper, no eager stream
  allocation.
- **Codecs are public static facades** (`AmqpProtocolCodec`, `AmqpFrameCodec`,
  `AmqpMessageCodec`) over the internal `AmqpEncoding` toolbox, so the wire format is testable
  and reusable without standing up a transport.

## Lifecycle

- **The server transport owns its listener.** `AmqpServerTransport.DisposeAsync` disposes every
  tracked connection and then the listener it was constructed over. The client transport does
  *not* own its factory — `IConnectionFactory` is not disposable by contract — so client
  disposal tears down only the tracked connections.
- **Accept/connect produce, open activates.** `AcceptAsync`/`ConnectAsync` wrap each carrier in
  an `AmqpConnection` and track it. `OpenAsync` (or the sync-over-async `Open()` convenience)
  acquires the carrier byte stream — a no-op for a single-stream carrier, the stream
  open/accept for a multiplexed one — constructs the `AmqpConnectionContext`, performs header
  negotiation when configured, and **caches the one context per connection**; repeated opens
  return the same instance.
- **Negotiation timing is an option.** With `AutoNegotiateProtocolHeader = true` (default) the
  header exchange happens inside `OpenAsync` and frames can flow immediately. With it `false`,
  no bytes are exchanged until `NegotiateAsync` is called explicitly, and the frame APIs
  (`SendAsync`/`ReceiveAsync`) throw `InvalidOperationException` if used before negotiation
  (when auto-negotiation is on they negotiate implicitly instead).
- **Disposal cascades.** `AmqpConnection.DisposeAsync` tears down its carrier resources (the
  acquired stream *and* the multiplexed connection on the mux path; the connection itself on
  the single-stream path) and unregisters from the owning transport's tracking list via an
  internal `OnDispose` callback. `Abort` forwards to the carrier's abort.

### Known deliberate quirks

Two simplifications are intentional and preserved as-is; both assume the transports are
composed and driven by a single owner, which is how every in-repo consumer uses them today:

- **The connection tracking list is not thread-safe.** Concurrent `AcceptAsync`/`ConnectAsync`/
  `DisposeAsync` calls can race on the `List<AmqpConnection>`.
- **The `OpenAsync` context cache is unlocked.** Two concurrent first opens on the same
  connection can both acquire a carrier and construct a context; the last writer wins the
  cache. On a multiplexed carrier that would acquire two streams.

If a genuinely concurrent consumer appears, these become synchronization work items — revisit
this section in the same change.

## Protocol-header negotiation

- The header is the fixed eight bytes `"AMQP"` + protocol id + `major.minor.revision`.
  `AmqpProtocolId` carries the spec values (`Amqp = 0`, `Tls = 2`, `Sasl = 3`);
  `AmqpProtocolHeader.Amqp10` / `.Sasl10` are the common constants.
- `NegotiateAsync` is idempotent: it writes the local header, reads the remote one, caches it
  (`RemoteProtocolHeader`), and returns the cached value on every later call.
  `SwitchProtocolAsync` re-arms negotiation for the next protocol phase — the SASL → AMQP
  transition: negotiate the SASL phase, exchange SASL frames, then switch with the AMQP header.
- The negotiated phase selects the frame type (`AmqpProtocolHeader.FrameType`: AMQP = 0,
  SASL = 1), and the frame codec rejects frames whose type does not match the current phase.
- **TLS is not done via protocol id 2.** The `Tls` id exists for wire fidelity, but transport
  security is a pre-composed connection layer: the composition root applies
  `listener.UseTls(...)` / `factory.UseTls(...)` (from `Assimalign.Cohesion.Security`) before
  constructing the AMQP transport, and the carrier's `Capabilities.Security` reports
  `ConnectionSecurity.Tls`. This matches AMQP's pure-TLS variant (TLS established before any
  header is exchanged) and keeps the security seam identical to the HTTP transports.

### Known issue (recorded, not yet fixed)

`AmqpTransportConnectionContext.ReadProtocolHeaderAsync` advances the `PipeReader`'s
*examined* position to `buffer.End` while consuming only the eight header bytes. If the peer's
header arrives in the same buffer as subsequent bytes (TCP coalescing — e.g. a server sending
its SASL header and `sasl-mechanisms` frame in one write), the following read never completes
until *new* bytes arrive, which deadlocks lock-step exchanges. The fix is
`AdvanceTo(buffer.GetPosition(8))` so examined == consumed. A skipped test
(`ReceiveAsync_OnHeaderCoalescedWithFrame_ShouldDecodeOpenPerformative` in
`tests/AmqpConnectionContextTests.cs`) documents the repro.

## Framing and codec surface

- Frame layout follows the spec: 4-byte big-endian size, data offset, frame type, channel,
  then the performative as a described list and an optional payload. `TryReadFrame` is
  incremental over a `ReadOnlySequence<byte>` so partial frames simply wait for more bytes;
  a stream that ends mid-frame is an `AmqpProtocolException`.
- **Max frame size is enforced on send**: an encoded frame larger than
  `AmqpTransportOptions.MaxFrameSize` (default 262,144 bytes; floor 512 per spec) throws
  `AmqpProtocolException` before any bytes are written. Inbound size enforcement is the
  peer's contract and is not duplicated.
- `AmqpMessageCodec` covers the section-based AMQP 1.0 message format (header, delivery and
  message annotations, properties, application properties, data/sequence/value bodies,
  footer).
- Sends are serialized by an internal write lock so a header write and a frame write cannot
  interleave; receives are a single-reader pull loop (`ReceiveAsync` yields decoded frames).

## Error model

- **`AmqpProtocolException` is the area-scoped root** (inherits `Exception` directly, per the
  repository's exception-scoping rule). It covers malformed headers, malformed or mismatched
  frames, unsupported descriptors, and max-frame-size violations.
- Composition errors are argument exceptions: `ArgumentNullException` for missing carriers,
  `ArgumentException` for capability mismatches (both at construction), and
  `ArgumentOutOfRangeException` for an options frame size below the 512-byte floor.
- Misuse is `InvalidOperationException` (frame APIs before manual negotiation) or
  `ObjectDisposedException` (accept/connect after transport disposal).
- Carrier-level failures (reset, abort) surface as the Connections library's
  `ConnectionException` family from the underlying pipes; this package does not wrap them.

## AOT posture

No reflection, no runtime code generation, no serializers. Performative dispatch is a `switch`
over numeric descriptors in both directions; encoding and decoding are span/buffer arithmetic
over `ArrayBufferWriter<byte>` / `ReadOnlySequence<byte>`. Fully NativeAOT compatible
(`IsAotCompatible` is set globally for the `libraries/` tree).

## Non-goals

- **No broker, container, session, or link semantics.** No session windows, link credit,
  delivery settlement, or routing — the performatives are wire DTOs only. That orchestration
  belongs to `Assimalign.Cohesion.Amqp`.
- **No SASL mechanism implementations.** The SASL performatives and frame phase are modeled;
  executing PLAIN/ANONYMOUS/EXTERNAL exchanges is the layer above.
- **No TLS inside this package.** Security is a pre-composed connection layer (see above).
- **No AMQP 0-9-1 (RabbitMQ classic) compatibility.** This is AMQP 1.0 only.
- **No multi-connection sharing of one multiplexed carrier.** One AMQP connection maps to one
  carrier stream.

## Relationships

- **`Assimalign.Cohesion.Connections`** — the carrier contracts this package consumes by
  capability (`IConnection`, listeners/factories, `ConnectionCapabilities`,
  `DuplexPipeStream`).
- **`Assimalign.Cohesion.Security`** — TLS as a pre-composed `UseTls` layer on the carrier
  listener/factory; never an AMQP concern.
- **`Assimalign.Cohesion.Amqp`** — the core AMQP library (placeholder today) intended to build
  session/link/messaging semantics on top of this transport layer.
- **`Assimalign.Cohesion.Http.Connections`** — the sibling application-protocol transport; the
  two consume the connection contracts with the same capability-gated, pre-composed-TLS
  pattern.
