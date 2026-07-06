# Assimalign.Cohesion.Connections.InMemory Design

## Design Intent

Provide an in-memory implementation of the Connections contracts whose two connection ends are
cross-wired over process memory instead of an operating-system socket. The goal is a *live*
transport for tests: application protocols (HTTP/1.1 keep-alive, HTTP/2 flow control and GOAWAY,
HTTP/3 streams) can run multiple request/response cycles against a real `Connection` with no
loopback port, deterministically, on every CI operating system.

This driver exists because `ConnectionProtocol.Memory` was already reserved in the value objects
but no driver implemented it, so four transport test projects each grew their own private
pipe-pair connection double. Those copies diverged — most notably the `Http.Connections` one was
*single-shot*: it preloaded request bytes and completed the writer in its constructor, which made
live keep-alive and flow-control round-trips impossible. Consolidating the pipe-pair mechanics
here gives every project one correct, live implementation.

The driver is a peer of `Tcp` / `Udp` / `Quic`: it depends only on the Connections contracts,
implements the standard `IConnectionListener` / `IConnectionFactory` (and multiplexed) surfaces
with no contract changes, and plugs into the same `UseHttp1/2/3(IConnectionListener)` seams a real
transport does. Phase 2 — the Web test-application factory (#793) — rides on this driver's client
side over BCL types.

## Pair Wiring

A connection is an `IDuplexPipe`: `Input` is the bytes received from the peer, `Output` the bytes
to send. An in-memory connection needs no wire and therefore **no pump loop** — unlike the TCP
driver, which runs receive/send loops between a socket and the consumer pipe. Instead, the two
ends of a pair share two `System.IO.Pipelines.Pipe` instances directly:

```
            aToB pipe                         bToA pipe
   A.Output ─────────► B.Input       B.Output ─────────► A.Input
   (aToB.Writer)  (aToB.Reader)      (bToA.Writer)  (bToA.Reader)
```

`InMemoryConnection.CreatePair` builds the two pipes and hands each end the reader it consumes and
the writer it produces to. A flush on one end's `Output` is directly observable on the other end's
`Input`; there is no copying loop in between. `InMemoryConnectionPair.Create` exposes this as a
`(Client, Server)` tuple of `Connection`; the listener/factory produce the same pair on dial.

Pipes are configured **non-pausing** (`pauseWriterThreshold: 0`): the in-memory transport favors
deterministic, non-blocking byte movement over back-pressure realism, so a synchronous prime write
or a write-then-read on the same task never blocks waiting for the peer to drain. HTTP/2 and
HTTP/3 exercise their own flow control above the transport, which is where flow-control realism
belongs.

## Close / Dispose / Abort Semantics

Teardown propagates to the peer purely through pipe completion — no end holds a reference to the
other, and there is no background watcher:

- **Graceful half-close** — the holder completes `Output`. Completing `A.Output` (the `aToB`
  writer) makes `B.Input` (the `aToB` reader) observe `ReadResult.IsCompleted`. The connection's
  `Output` is wrapped so that the holder completing it transitions the connection `Open → Closing`,
  mirroring a real send loop that drains its backlog before the connection closes. Protocol
  connections that wait for transport drain observe this transition without burning a timeout.
- **Dispose (graceful)** — completes both this end's `Output` and `Input` with no error. The peer
  observes end-of-stream on its next read (its `Input` completes) and on its next flush (its
  `Output` flush returns `IsCompleted`). State becomes `Closed` unless the connection was already
  `Aborted`. Dispose is idempotent.
- **Abort(reason)** — completes this end's `Output` **with the reason exception**, so the peer's
  next `ReadAsync` throws exactly that exception; completes this end's `Input` so the peer's next
  flush observes completion. State becomes `Aborted` and `ConnectionClosed` is signaled. A `null`
  reason surfaces a `ConnectionAbortedException` on the peer.

`ConnectionClosed` fires when **this** end is disposed or aborted. It does not fire proactively
when the *peer* closes — a peer close is observed by reading `Input` (which completes) or writing
`Output` (whose flush reports completion), which is exactly how a byte-stream consumer such as an
HTTP parser already detects end-of-connection. Keeping the closed token local avoids a background
watcher and keeps the driver allocation-light and trim-safe.

Completion of the underlying pipe ends is guarded (idempotent, and tolerant of the holder having
already completed a writer), so double dispose, abort-then-dispose, and holder-completed-output
followed by dispose are all safe.

## Multiplexed Variant

`InMemoryMultiplexedConnection` models a QUIC-shaped connection: each end owns an inbound-stream
`Channel`, and the two ends reference each other. `OpenStreamAsync` builds a cross-wired
single-stream pair (via `InMemoryConnection.CreatePair`), returns the local end to the opener, and
enqueues the peer end on the *other* multiplexed connection's inbound channel, where
`AcceptStreamAsync` picks it up. Each stream is a full `Connection` with its own lifetime and
duplex pipe; stream capabilities report `IsMultiplexed = false` while the parent reports `true`.

Stream direction is honored: an outbound unidirectional (`WriteOnly`) stream mirrors to a
`ReadOnly` stream on the peer — the write-only end's `Input` is pre-completed (it reads nothing)
and the read-only end's `Output` throws on write, per the `IMultiplexedConnection` contract.
Opening a `ReadOnly` stream throws `ArgumentException`, since a peer cannot open a stream only the
remote side may write to. Disposing or aborting a multiplexed connection completes its inbound
channel (so pending and future `AcceptStreamAsync` calls observe `OperationCanceledException`) and
tears down any queued-but-unaccepted streams; opening a stream toward a closed peer throws
`ConnectionAbortedException`.

## Dial / Accept Model

`InMemoryConnectionListener` holds an unbounded `Channel<Connection>` of accepted server ends and
is dialed by an `InMemoryConnectionFactory` bound to it (`listener.CreateFactory()` or
`new InMemoryConnectionFactory(listener)`). A dial creates a fresh pair, queues the server end, and
returns the client end; `AcceptAsync` waits on the channel when empty — so a pending accept
resolves the moment a dial arrives, exactly like a real listener re-arming. Disposing the listener
completes the channel, so a pending or subsequent `AcceptAsync` throws `OperationCanceledException`
and a later dial throws `ConnectionAbortedException`. The transport has a single logical server, so
the endpoint argument to `ConnectAsync` selects nothing further; the returned connection reports
the listener's endpoint as its remote endpoint, and endpoints mirror across the pair.

## AOT Posture

No reflection, no runtime code generation, no dynamic dispatch beyond interface calls. A pure
`System.IO.Pipelines` + `System.Threading.Channels` composition over BCL types. NativeAOT/trim
compatible (`IsAotCompatible=true`).

## Non-Goals

- Not a production transport. It never touches a socket, applies no back-pressure, and offers no
  network semantics beyond in-order byte delivery between two in-process ends.
- No datagram (`IDatagramConnection`) variant — the in-memory transport is stream-oriented; add a
  datagram shape only if a test genuinely needs message framing at the transport layer.
- No security layer of its own; a secured in-memory connection is produced by composing the
  Connections `Security` (TLS) layer over a pair, exactly as for any other transport.
- Not a general connection registry/hub. The factory dials the single listener it is bound to; a
  multi-listener, endpoint-routed hub can be added when a consumer (for example the Web test
  factory, #793) needs one.

## Relationships

- **`Assimalign.Cohesion.Connections`** — the `Connection` / `ConnectionListener` /
  `ConnectionFactory` guided bases and their multiplexed counterparts this driver implements, plus
  `ConnectionCapabilities`, `ConnectionProtocol.Memory`, and `ConnectionAbortedException`.
- **Transport test projects** (`Connections`, `Connections.Security`, `Http.Connections`,
  `Amqp.Connections`) — consumers that previously each carried a private pipe-pair double and now
  build their test connections on this driver.
- **Web test factory (#793)** — the downstream consumer that dials this driver's client side to run
  the full HTTP pipeline socketlessly.
