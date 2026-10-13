# Assimalign.Cohesion.Connections.InMemory Design

## Design Intent

Provide an in-memory implementation of the Connections contracts whose two connection ends are
cross-wired over process memory instead of an operating-system socket. The goal is a *live*
transport for tests: application protocols (HTTP/1.1 keep-alive, HTTP/2 flow control and GOAWAY,
HTTP/3 streams) can run multiple request/response cycles against a real `DuplexConnection` with no
loopback port, deterministically, on every CI operating system.

This driver exists because `ConnectionProtocol.Memory` was already reserved in the value objects
but no driver implemented it, so four transport test projects each grew their own private
pipe-pair connection double. Those copies diverged — most notably the `Http.Connections` one was
*single-shot*: it preloaded request bytes and completed the writer in its constructor, which made
live keep-alive and flow-control round-trips impossible. Consolidating the pipe-pair mechanics
here gives every project one correct, live implementation.

The driver is a peer of `Tcp` / `Udp` / `Quic`: it depends only on the Connections contracts,
implements the standard `IDuplexConnectionListener` / `IDuplexConnectionFactory` (and multiplexed) surfaces
with no contract changes, and plugs into the same `UseHttp1/2/3(IDuplexConnectionListener)` seams a real
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
`(Client, Server)` tuple of `DuplexConnection`; the listener/factory produce the same pair on dial.

Pipes are configured **non-pausing** (`pauseWriterThreshold: 0`): the in-memory transport favors
deterministic, non-blocking byte movement over back-pressure realism, so a synchronous prime write
or a write-then-read on the same task never blocks waiting for the peer to drain. HTTP/2 and
HTTP/3 exercise their own flow control above the transport, which is where flow-control realism
belongs.

## Close / Dispose / Abort Semantics

Teardown propagates through pipe completion. Every pair holds peer references so local close
or abort signals both tokens immediately, without a background watcher or application I/O:

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

`ConnectionClosed` fires on local disposal, abort, and peer closure before local disposal returns.
Closing either end signals the peer synchronously. Completing one output cleanly remains a
half-close. Multiplexed streams retain their directional reset notifications.

Completion of the underlying pipe ends is guarded (idempotent, and tolerant of the holder having
already completed a writer), so double dispose, abort-then-dispose, and holder-completed-output
followed by dispose are all safe.

## Multiplexed Variant

`InMemoryMultiplexedConnection` models a QUIC-shaped connection: each end owns an inbound-stream
`Channel`, and the two ends reference each other. `OpenStreamAsync` builds a cross-wired
stream pair (via `InMemoryConnection.CreateStreamPair`), returns the local end to the opener, and
enqueues the peer end on the *other* multiplexed connection's inbound channel, where
`AcceptStreamAsync` picks it up. Each stream is a `MultiplexedConnectionStream` with its own lifetime and
duplex pipe; stream capabilities report `IsMultiplexed = false` while the parent reports `true`.

Stream direction is honored: an outbound unidirectional (`WriteOnly`) stream mirrors to a
`ReadOnly` stream on the peer — the write-only end's `Input` is pre-completed (it reads nothing)
and the read-only end's `Output` throws on write, per the `IMultiplexedConnection` contract.
Opening a `ReadOnly` stream throws `ArgumentException`, since a peer cannot open a stream only the
remote side may write to.

**A parent close reaches both ends (#1451).** The pair shares one close gate. Opening a stream
registers both ends before handing either out; each parent tracks queued, accepted and locally
opened streams until their terminal teardown. A stream leaving an accept queue stays tracked.
Stream disposal, whole-stream abort and completion of both usable halves remove the end; a
directional peer notification alone does not, since the opposite half can still be live.

The first abort or disposal on either parent chooses the pair close. Coded abort uses its caller
code; no-code abort and disposal use 0. Both parents signal `ConnectionClosed`, both accept queues
end, and pending and future accepts throw `OperationCanceledException`. The initiating parent is
`Aborted` for abort or `Closed` for disposal; its peer becomes `Aborted`. Repeated closes and later
disposal preserve that first decision. Opening another stream fails with the selected loss.

Every active stream first records a `ConnectionResetException` whose `ApplicationErrorCode` is the
connection close code. Only then are queues completed and parent tokens signaled; callbacks and
pipe completion run outside the pair gate. A callback can dispose its stream without deadlocking
or replacing the recorded code with a clean end. A throwing cancellation callback does not strand
other notifications. Pipe wrappers check recorded loss before and after pending operations, so
pending and subsequent reads/writes fail as connection loss even after cleanup. Parent disposal
is not a clean stream FIN. The optional reason exception stays local; the code is the peer-facing
close reason, matching the existing reset-code mapping.

**Compatibility and triage.** This corrects behavior shipped in 10.0.0-preview.1: parent closure
previously left the peer token and accepts live, and failed to terminate accepted streams. Code
relying on those pending operations surviving closure now observes immediate cancellation/loss,
an accepted preview behavior break under decisions 15 and 56. The socketless driver has no remote
network entry point; this is an in-process lifecycle defect, not an unauthenticated remote attack.

**An end that abandons a stream signals the other end (#1329).** The two ends of a stream pair hold
each other. An end that aborts, or whose holder completes `Output` with an error (the in-memory
`RESET_STREAM`) or `Input` with an error (the in-memory `STOP_SENDING`), cancels the other end's
`ConnectionClosed` at once, on the calling thread, and leaves its state alone. That is what the QUIC
driver reports for a peer `RESET_STREAM` or `STOP_SENDING`, and it lets an HTTP/3 server fire
`RequestCancelled` for a request the client cancelled while the application neither reads nor
writes. A clean completion (`Output` without an error, the FIN) is a half-close and signals nothing.
To see an errored `Input` completion, a stream end hands out a thin delegating `PipeReader` instead of
the pipe's own reader.

**A stream end carries application error codes (#1080, #1452).** It registers itself as the
`IMultiplexedStreamAbort` feature from `Connections.StreamAbort`, available through
`stream.DirectionalAbort`: the in-memory `STOP_SENDING` and `RESET_STREAM` with a code, so a protocol
tested over this driver puts its codes where the QUIC driver puts them:

- `AbortRead(errorCode)` completes the end's receive pipe with a `ConnectionResetException` whose
  `ApplicationErrorCode` is the code, so the other end's next flush throws it. A read waiting on the
  pipe is woken first: completing a pipe reader leaves a pending read waiting for the writer, so the
  delegating reader cancels it and fails it, and every later read, with `ConnectionAbortedException`.
- `AbortWrite(errorCode)` completes the end's send pipe with the same exception, so the other end's
  reads throw it, a read in flight included.
- Both signal the other end's `ConnectionClosed`, leave this end's state alone, and do nothing for a
  direction that has already ended or that a unidirectional end lacks. A later `Abort(reason)` or
  dispose does not complete an aborted direction again, so the other end keeps seeing the code, not the
  reason. Codes outside 0 to 2^62 - 1 throw, as on the QUIC driver.
- The ends of a byte-stream pair do not register the feature: a single-stream transport has no code to
  carry. `DirectionalAbort` is available only on `IMultiplexedConnectionStream`. Stream ends derive from
  `MultiplexedConnectionStream` and compose the internal `InMemoryConnection` pipe and lifecycle mechanics.

**The multiplexed roots (#1453).** Streams expose `Abort(code, reason)`, which resets both
available directions before ending the lifecycle. A direction already ended retains its first code.
Exceptions thrown by local or peer `ConnectionClosed` registrations are isolated, as in the QUIC
driver, so abort still notifies the peer and removes the ended stream from its parent's live set.
This also applies to no-code abort, directional abort and disposal through the shared cancellation
path. Compared with 10.0.0-preview.1, callback exceptions no longer escape teardown; this behavior
break is accepted under decision 15. The coded stream root itself postdates preview.1.
`IsInputCompleted` is true when the peer completed output cleanly and this end has not aborted
reading, aborted the stream, or completed input with an error; buffered bytes may still remain.
Local input failure is recorded separately from directional read cancellation, so a later peer FIN
cannot turn failed input into clean completion. A whole-stream abort records failure even when the
holder already completed input cleanly. This corrects the unreleased root contract; input-completion
reporting did not exist in 10.0.0-preview.1.
The parent root accepts coded aborts in the same 62-bit
range as streams; the first abort or disposal decides the pair close and notifies both ends as
described above.

The stream return types change from `ValueTask<IDuplexConnection>` to `ValueTask<IMultiplexedConnectionStream>`
(and from `ValueTask<DuplexConnection>` to `ValueTask<MultiplexedConnectionStream>` on guided bases). Together
with the required coded connection abort, these break preview.1 binaries and external implementers,
accepted during previews on decisions 15 and 56. Awaited streams can still be assigned to
`IDuplexConnection`. Stream reset and completion members are new; the removed optional contracts
postdate preview.1.

## DuplexConnection features (#1452)

The stream's `ConfigureFeatures` registers it once on first access, with
`IConnectionFeature.Name` returning `IMultiplexedStreamAbort.FeatureName` (`"StreamAbort"`).
Byte-stream pairs and multiplexed parent connections have empty collections. Each multiplexed
stream owns its collection; parent features never appear on the stream. A wrapper forwards the
stream's features by overriding `InnerFeatures`, which removes the need to reimplement and forward
every optional capability. A wrapper can replace or remove a named slot when its semantics demand it.

The core `Features` additions break external implementers of the connection interfaces shipped
in 10.0.0-preview.1, accepted under owner decisions 15 and 56; subclasses inherit the guided-base
implementation. The directional-abort contract itself postdates preview.1, so its package move
breaks no released API.

## Dial / Accept Model

`InMemoryConnectionListener` holds an unbounded `Channel<DuplexConnection>` of accepted server ends and
is dialed by an `InMemoryConnectionFactory` bound to it (`listener.CreateFactory()` or
`new InMemoryConnectionFactory(listener)`). A dial creates a fresh pair, queues the server end, and
returns the client end; `AcceptAsync` waits on the channel when empty — so a pending accept
resolves the moment a dial arrives, exactly like a real listener re-arming. Disposing the listener
completes the channel, so a pending or subsequent `AcceptAsync` throws `OperationCanceledException`
and a later dial throws `ConnectionAbortedException`. The transport has a single logical server, so
the endpoint argument to `ConnectAsync` selects nothing further; the returned connection reports
the listener's endpoint as its remote endpoint, and endpoints mirror across the pair.

`BindAsync` is a logical, allocation-free bind because there is no operating-system endpoint to
acquire. It still honors cancellation and rejects a disposed listener, keeping the same host lifecycle
contract as the resource-owning drivers. Disposal is terminal; a restart creates a new listener and a
factory bound to that new instance.

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

- **`Assimalign.Cohesion.Connections`** — the `DuplexConnection` / `DuplexConnectionListener` /
  `DuplexConnectionFactory` guided bases and their multiplexed counterparts this driver implements, plus
  `ConnectionCapabilities`, `ConnectionProtocol.Memory`, and `ConnectionAbortedException`.
- **`Assimalign.Cohesion.Connections.StreamAbort`** — the optional directional-abort contract
  registered by each multiplexed stream.
- **Transport test projects** (`Connections`, `Connections.Security`, `Http.Connections`,
  `Amqp.Connections`) — consumers that previously each carried a private pipe-pair double and now
  build their test connections on this driver.
- **Web test factory (#793)** — the downstream consumer that dials this driver's client side to run
  the full HTTP pipeline socketlessly.

## Connection hierarchy compatibility

Decision 58 changes the hierarchy shipped in 10.0.0-preview.1 under decision 15's accepted
preview source and binary breaks. Byte-pipe APIs use `IDuplexConnection` and `DuplexConnection`, with duplex listener/factory bases.
Their public accept/connect results are interfaces. The generic listener base supplies `Id`,
`Connections`, and the shared accept core.
Every connection shares root lifecycle and features. Accepted connections stay in the live set
until their closed token fires; listener disposal never closes handed-out connections.
Only the code that accepted a connection reads or writes it.

Completing every usable holder direction, or both peers completing their outputs cleanly, signals
`ConnectionClosed` on both ends of an in-memory multiplexed stream. Completing only one direction
remains a half-close and leaves both lifecycle tokens unsignaled.
