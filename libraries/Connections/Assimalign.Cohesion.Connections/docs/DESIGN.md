# Assimalign.Cohesion.Connections Design

## Design Intent

This library defines the connection contract for the Cohesion networking stack: the small set of
interfaces that describe how to accept, establish, layer, and use a network connection,
independent of any concrete protocol (TCP, UDP, QUIC) or any application protocol (HTTP, AMQP)
built on top. It is the lowest layer in the stack and depends only on `Assimalign.Cohesion.Core`.
Concrete transports implement these contracts; application protocols consume them; security and
other connection transformations compose over them. Neither direction inherits the other's
concerns.

## The Contract at a Glance

Three channel shapes, each honest about its delivery model:

| Shape | Contract | Data plane |
|---|---|---|
| Single stream | `IConnection` (+ `IConnectionListener` / `IConnectionFactory`) | **is** a duplex pipe (`IConnection : IDuplexPipe`) |
| Multiplexed | `IMultiplexedConnection` (+ listener/factory) | **yields** streams, each an `IConnection` |
| Datagram | `IDatagramConnection` | **speaks** discrete messages; no pipe |

One generic composition unit:

- `IConnectionLayer` — a connection-to-connection arrow (`UpgradeAsync(IConnection) → IConnection`),
  applied once per connection at establishment via `listener.Use(layer)` / `factory.Use(layer)`.
  TLS (in `Assimalign.Cohesion.Security`) is the first implementation; proxy-protocol handling,
  traffic accounting, throttling, and connection-level compression are the same shape.

Selection is by **capability**, not protocol identity: `ConnectionCapabilities` (delivery mode,
reliability, ordering, multiplexing, security) is what consumers gate on; `ConnectionProtocol` is
diagnostics-only.

---

## Design Rationale

This section consolidates the reasoning that produced this design, recorded so future changes are
made with the original constraints in view. It replaced an earlier model
(`ITransport` / `ITransportConnection` / `ITransportConnectionContext` plus a connection
middleware pipeline) after a full design review.

### Why the old transport abstraction dissolved

The old `ITransport` had one meaningful member: `InitializeAsync() → ITransportConnection`. Every
consumer call site followed the same pattern — call it, then immediately **downcast** the result
(`is ISingleStreamTransportConnection` / `is IMultiplexTransportConnection`) to find out what it
really had, throwing at runtime on mismatch. An abstraction whose every use is a factory call
followed by a type test is not an abstraction; the downcast targets were the real contract. This
design promotes them into the type system:

- **Direction is structural.** Servers are `IConnectionListener` (accept); clients are
  `IConnectionFactory` (connect). The old `TransportKind` flag is gone — handing a client where a
  server is required is now a compile error, not a runtime check. Note that direction is a
  property of *establishment only*: once produced, inbound and outbound connections are
  indistinguishable `IConnection`s (a BitTorrent node, which simultaneously listens and dials and
  runs one wire protocol over both, is the canonical demonstration).
- **Stream multiplicity is structural.** HTTP/1.1 and HTTP/2 take an `IConnectionListener`;
  HTTP/3 takes an `IMultiplexedConnectionListener`. The old runtime switch became parameter types.
- **Datagrams are a separate shape.** The old model forced UDP datagrams through a byte
  `Pipe`, erasing message boundaries — the one property UDP guarantees. `IDatagramConnection`
  speaks messages (`ReceiveAsync` into a caller buffer, `SendAsync` to an endpoint) and never
  pretends to be a stream.

The old connection middleware pipeline (`ITransportPipeline`) was removed outright: it ran once
per connection *after* the connection's pipe was already constructed and wired, so it could
observe a connection but could never substitute the byte stream the consumer read. Its one
motivating use case — TLS — was therefore unimplementable through it. The capability it was
reaching for exists correctly as `IConnectionLayer` (below).

"Transport" survives in this stack only as a *concept* — the underlying medium a driver speaks —
never as a type. The medium is described by data (`ConnectionCapabilities` and the
diagnostics-only `ConnectionProtocol`), implemented by the **driver packages**
(`Assimalign.Cohesion.Connections.Tcp/Udp/Quic`), and supported by this library's internal
implementation **toolbox** (pipe-pair wiring, pipe options, diagnostics — shared with the drivers
via `InternalsVisibleTo`). Transports are configuration; connections are runtime. The naming rule
follows: every type and package is named for the connection domain — the unit you consume
(`IConnection`), its producers (`IConnectionListener`/`IConnectionFactory`), its description
(`ConnectionCapabilities`/`ConnectionProtocol`/`ConnectionDelivery`/`ConnectionSecurity`), and its
diagnostics correlation (`ListenerId`, stamped only on listener-produced connections —
factory-dialed connections carry none). No type or assembly carries the word "transport", because
there is no transport abstraction.

### Layering: abstract the lifecycle, not the datapath

The obvious generic model — a uniform `ILayer` interface with L1–L7 instances, modules stacked
OSI-style — has a forty-year experimental record and lost on performance every time it was tried
at the data plane (AT&T STREAMS, the x-kernel). A uniform per-layer interface must reduce every
boundary to a least-common-denominator message and pay a virtual hop, and often a buffer handoff,
*per layer per IO operation*; it also dissolves type safety, since L3 packets and L4 streams do
not share a service vocabulary. The frameworks that survived (netty's channel pipeline, Kestrel's
connection middleware) all made the same retreat: the recurring unit is the **connection**, and
layers are transformations attached to one.

Hence the rule this library is built on: **abstract the lifecycle, not the datapath.**

- **Control plane (per-connection, at establishment): generic.** `IConnectionLayer` composes via
  `Use(...)`; each layer costs one interface dispatch and at most one wrapper allocation, once per
  connection. A pass-through layer (metrics, a proxy-protocol preamble reader) may return the
  inner connection unchanged — zero steady-state cost.
- **Data plane (per byte): concrete.** One best-in-class primitive, `System.IO.Pipelines`, with
  no framework frames between the consumer and the transport's buffers. A transforming layer
  (TLS) inserts only its intrinsic cost; the composition mechanism adds nothing per byte.

The OSI model maps onto this as an *algebra of arrows between the three shapes*, not as seven
nominal layers:

| OSI | Here | Arrow shape |
|---|---|---|
| L4 | TCP/UDP drivers | origin → `IConnection` / `IDatagramConnection` |
| L5 (sessions/mux) | QUIC streams; any future stream-mux layer | `IConnection` ⇄ `IMultiplexedConnection`; streams are `IConnection` again (the recursion) |
| L6 (TLS, compression) | `IConnectionLayer` | `IConnection → IConnection` |
| L7 | HTTP, AMQP | consume `IConnection`, produce protocol sessions |
| L1–L3 | the operating system | below the framework boundary |

Two properties of the algebra matter:

1. **Fusion is permitted.** QUIC is L4+L5+L6 in one protocol — an origin arrow landing directly on
   `IMultiplexedConnection`. A rigid layer stack cannot express that; arrows can. (Modern
   performance work — QUIC itself, kTLS — comes from fusing layers, so the model must allow it.)
2. **The recursion is real.** L4 itself is an arrow: TCP is, literally, a layer that turns
   unreliable IP datagrams into a reliable ordered stream, and QUIC turns UDP datagrams into a
   multiplexed secure stream bundle. A userspace protocol of the same kind (µTP, reliable-UDP
   variants) would be implemented as `IDatagramConnection → IConnection` and every consumer above
   it — TLS layer, HTTP, AMQP — would compose unchanged, because they bind to shapes, not
   protocols. Likewise DTLS is `IDatagramConnection → IDatagramConnection`, and tunnels are
   `IConnection → IDatagramConnection`. This is the recursive-layer insight (cf. RINA): layers
   differ in scope and policy, not in kind, and the recurring unit is the connection.

### Why `IConnection : IDuplexPipe` (the flattened data plane)

Earlier drafts gave the connection a `Transport` property carrying an `IDuplexPipe` (and the
pre-redesign code wrapped the pipe in a bespoke `ITransportConnectionPipe`). Both were removed
for the same reasons:

- **`IDuplexPipe` already is the abstraction.** It is two members over `PipeReader`/`PipeWriter`,
  which are abstract classes that exist precisely so implementations vary. A wrapper interface
  adds a seam with nothing varying behind it — and the bespoke wrapper's one addition (`Stream`)
  forced an eager stream allocation per connection and `Stream.Null` fakes where no stream
  existed. The BCL ships the lazy equivalents (`AsStream()`).
- **`Input`/`Output` are holder-relative**, and nesting (`connection.Transport.Input`) multiplies
  the perspectives a reader must track. Every duplex connection is internally *two* mirrored pipe
  views (the consumer's end and the wire pump's end), and surfacing a pairing object invites that
  mirror into consumer code. Flattening makes the connection itself the only pipe-bearing object:
  `connection.Input` is what the peer sent you, `connection.Output` is what you send — anchored,
  documented, and identical at every level of the recursion (a TCP connection and a QUIC stream
  read the same). The mirrored pump end lives in the transports' internal toolbox
  (`DuplexPipePair`), never on a contract.
- **Polarity.** Pipe → stream is a cheap lazy adapter (`DuplexPipeStream`, `AsStream()`);
  stream → pipe re-buffers. Making the pipe canonical lets hot consumers (HTTP/AMQP frame
  parsers) read zero-copy sequences while stream-based APIs (`SslStream`) adapt at their own
  boundary.
- **Interop is free.** Anything accepting `IDuplexPipe` accepts the connection itself.

The shapes stay honest because the flattening is scoped: only the single-stream shape *is* a
pipe. A multiplexed connection has no pipe (it yields streams); a datagram connection has no pipe
(a pipe erases message boundaries — making it carry one would force inventing a framing protocol
just to satisfy the interface). Nothing in the stack ever holds two duplex pipes.

### Directionality

Multiplexed transports produce unidirectional streams (QUIC; HTTP/3's control and QPACK streams
are unidirectional in practice), so "duplex" cannot be an unconditional promise.
`ConnectionDirection` makes it explicit per channel instance — `Bidirectional`, `ReadOnly`
(inbound unidirectional: output throws), `WriteOnly` (outbound unidirectional: input is
pre-completed) — and `IMultiplexedConnection.OpenStreamAsync(direction)` chooses the stream type
per call (an HTTP/3 endpoint needs bidirectional request streams *and* unidirectional control
streams on one connection, so a per-connection setting cannot work). Direction lives on the
connection, not on `ConnectionCapabilities`, because it varies per instance, not per transport
class.

### Capability selection, not protocol identity

Consumers state *requirements*, not protocol names: HTTP/1.1 needs a reliable, ordered,
single-stream listener — satisfied by TCP today, by a Unix domain socket, an in-memory transport,
or a userspace reliable-UDP protocol tomorrow, with no consumer change. Branching on
`ConnectionProtocol` would close that set and re-create the old hardcoded
protocol-to-transport mapping this design deleted. `ConnectionProtocol` exists for diagnostics
and logging only.

### Precedents

The shape matches the systems that survived production: Kestrel
(`Microsoft.AspNetCore.Connections.Abstractions`: `IConnectionListener`/`IConnectionFactory`/
`ConnectionContext` over `IDuplexPipe`; its "Transport.*" packages contain drivers, not
abstractions), netty (`Channel` + pipeline of per-connection handlers), and ADO.NET (consumers
hold `DbConnection`; the driver is a package choice, not an interface applications touch).
Validation cases worked during the design review: HTTP/1.1–HTTP/3 and AMQP (the in-repo
consumers), and BitTorrent as an adversarial external case — peer wire over TCP or µTP, MSE
encryption as a non-TLS `IConnectionLayer`, DHT/KRPC over datagrams, simultaneous
listener+factory in one node — mapped onto the algebra with no new shapes.

---

## Guided Abstract Bases

Per the repository's interface-first-with-a-guided-abstract-base rule, each contract ships a
public abstract base (`Connection`, `ConnectionListener`, `ConnectionFactory`,
`MultiplexedConnection` and its listener/factory, `DatagramConnection`). Where the base exposes a
concrete-typed member — a listener's `AcceptAsync` returning `Connection` rather than
`IConnection` — the typed member is declared `public` (so holders of the concrete type get the
richer signature without casting) and the interface member is implemented explicitly, forwarding
to it. The interface remains the canonical surface consumers depend on.

## Lifecycle and Error Model

- A connection is **live when produced** — there is no separate open step and no
  connection-versus-context duality. Read and write immediately.
- Three teardown paths: complete `Output` for a graceful half-close; `DisposeAsync()` to close;
  `Abort(Exception?)` to tear down immediately, discarding in-flight data. `ConnectionClosed` is
  signaled on closure. `ConnectionState` tracks `Idle → Opening → Open → Closing → Closed`, or
  `Aborted`.
- `ConnectionException` is the area-scoped exception root (inheriting directly from
  `Exception`; no framework-wide ancestry, per repository rules), with
  `ConnectionAbortedException` and `ConnectionResetException` for the common failures. Transports
  surface reset/abort conditions through this family so consumers catch one hierarchy.

## AOT Posture

Contracts, small value types, and two allocation-light utilities (`DuplexPipeStream`, the layered
listener/factory decorators). No reflection, no runtime code generation, no serialization.
`ConnectionId` and `ConnectionProtocol` are produced by the repository's `CohesionValueType`
source generator. Fully NativeAOT compatible.

## Non-Goals

- No request/application middleware pipeline, and no per-byte layer interfaces — layers compose
  per connection at establishment only.
- No general-purpose `Items` property bag on the connection; cross-cutting state belongs to the
  layers or protocols that own it.
- No `ILayer<TLower, TUpper>`-style nominal OSI hierarchy, and no datagram/multiplexed layer
  interfaces until a second real implementation exists (DTLS would justify
  `IDatagramConnection → IDatagramConnection`).
- `ConnectionProtocol` is not a behavior switch.

## Relationships

- **`Assimalign.Cohesion.Connections.Tcp` / `.Udp` / `.Quic`** — the drivers implementing these
  contracts. Driver-support infrastructure (the `DuplexPipePair` wiring, pipe options and
  adaptive memory pooling, `ConnectionEventSource` diagnostics, `ListenerId`) lives in this
  library's `Internal` namespace and is shared with the drivers via `InternalsVisibleTo`; there is
  no separate toolbox assembly.
- **`Assimalign.Cohesion.Security`** — TLS as `TlsConnectionLayer` / `UpgradeToTlsAsync`.
- **`Assimalign.Cohesion.Http.Connections` / `Assimalign.Cohesion.Amqp.Transports`** — application
  protocols consuming these contracts by capability.
