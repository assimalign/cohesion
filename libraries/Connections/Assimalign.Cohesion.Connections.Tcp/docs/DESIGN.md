# Assimalign.Cohesion.Connections.Tcp Design

## Design Intent

The socket-backed stream driver for the Cohesion Connections contracts. It implements
`IConnectionListener` / `IConnectionFactory` producing reliable, ordered, single-stream `IConnection`s
over a connected `System.Net.Sockets.Socket`. It is the default production transport for HTTP/1.1 and
HTTP/2 and the reference implementation of the socket receive/send pump loops the other stream drivers
are modeled against.

Despite the `Tcp` name, this one driver serves **two socket families**: TCP over IP, and Unix domain
sockets. They differ only in the endpoint and address family — the socket, the pump loops, the duplex
pipe, and the connection lifecycle are identical — so folding UDS into this driver (rather than a
separate package) avoids duplicating the entire socket data path. A third endpoint form,
`FileHandleEndPoint`, adopts a listening socket handed off by a parent process (socket activation).

## Data Path

Each connection owns two pump loops moving bytes between the socket and the consumer-facing duplex pipe
(`DuplexPipePair`, compiled in from the contracts library's `shared/` folder):

- **Receive loop** reads from the socket into the transport-output pipe writer, applying back-pressure
  when the consumer is slow (the flush pauses the loop).
- **Send loop** reads from the transport-input pipe reader and writes to the socket via a pooled sender.

`connection.Input` is what the peer sent; `connection.Output` is what you send. The mirrored pump ends
live in the compiled-in `DuplexPipePair` and never surface on the connection contract. Socket tuning (adaptive memory
pool block size, IO-queue schedulers, read/write buffer thresholds) comes from
`SocketPipeOptionsFactory` and is shared per listener across its connections.

### Socket operations are their own value-task sources

The pump loops await reusable socket operations (`SocketPipeAsyncArgs`, the receiver and the pooled
senders) that implement `IValueTaskSource` themselves, so a steady-state receive or send allocates
nothing. The awaiter and the socket completion race: either can run first, and both can run at once.
The awaiter publishes its continuation's state and then the continuation itself through a
compare-exchange; the completion path reads the state only after it has observed the continuation,
with acquire reads. A continuation is therefore always scheduled with its own state and exactly once.

The opposite order (#1093) read the state before the continuation. A completion that read a null
state and then lost the race to a registration scheduled that continuation with null. An async
method's continuation is the runtime's state-machine callback, which rejects any other state with "An
unexpected state object was encountered" on a thread-pool thread, where the exception ends the process.
`SocketPipeAsyncArgsTests` races the two paths 50,000 times; before the fix about one in seven
thousand continuations received the wrong state.

## Endpoint Handling (the bind switch)

`TcpConnectionListener.BindAsync` acquires the endpoint explicitly and is idempotent while the listener
is active. `AcceptAsync` invokes it when necessary for backward compatibility, but hosts bind before
starting accept loops so startup does not complete until the endpoint is owned. The endpoint form
selects the bind strategy:

| Endpoint | Socket | Bind behavior |
|---|---|---|
| `IPEndPoint` | `Stream`/`Tcp`, `DualMode` when `IPv6Any` | `Bind` + `Listen` |
| `DnsEndPoint` | `Stream`/`Tcp`, IPv4 or dual-mode IPv6 selected by the platform | Factory-only: resolve and connect |
| `UnixDomainSocketEndPoint` | `Stream`/`Unspecified` | delete stale socket file → `Bind` + `Listen` |
| `FileHandleEndPoint` | adopt the inherited descriptor | **no** `Bind`/`Listen` — already listening |

The factory (`TcpConnectionFactory`) uses the same switch to construct the outbound socket, then
`ConnectAsync`. A `DnsEndPoint` has `AddressFamily.Unspecified`; its factory branch therefore uses the
address-family-selecting socket constructor so DNS names and socket-facing URI hosts can resolve to
IPv4 or IPv6 without attempting to construct an unspecified-family socket.

### Unix domain socket file lifecycle

A Unix domain socket bound to a filesystem path leaves a socket special file behind, and a stale file
from a prior unclean shutdown makes `Socket.Bind` fail with `AddressAlreadyInUse` even though nothing is
listening. The listener therefore:

1. **Deletes the stale socket file before binding** (`UnixDomainSocketFile.DeleteStale`), restoring the
   rebind-after-crash behavior a real server needs. This is last-writer-wins: it removes the directory
   entry even if another live socket is bound to it (that socket keeps working until it closes, but the
   name now points at the new listener), matching the crash-recovery goal the acceptance criteria
   specify.
2. **Unlinks the socket file on `DisposeAsync`** — but only the filesystem-backed path this listener
   actually bound. Linux abstract-namespace sockets (paths beginning with `@` or a NUL byte) and
   autobind endpoints have no filesystem entry and are skipped; an inherited `FileHandleEndPoint` socket
   is never unlinked (the parent owns the name).

Platform note: Windows removes its AF_UNIX socket file on close, so stale files are primarily a
Linux/macOS concern; the delete-before-bind step is a harmless no-op when no file is present.

### Socket activation / file-descriptor hand-off

`FileHandleEndPoint` models systemd `.socket` activation, launchd, or a supervising parent that has
already **bound and listened** on a socket and passed the descriptor to the child. The listener adopts
the descriptor (`new Socket(new SafeSocketHandle(handle, ownsHandle: true))`) and accepts on it
directly. It must **not** re-`Bind` or re-`Listen` — an inherited listening socket is already in the
listening state, and re-binding a custom `EndPoint` that does not serialize would throw. (An earlier
version constructed the socket from the handle but then unconditionally called `Bind`/`Listen`, which
made this path unusable; the bind switch now branches on it.)

## Honest Protocol Stamping

`ConnectionProtocol` is diagnostics-only (consumers gate on `ConnectionCapabilities`, never on protocol
identity), but it must still be *honest*. The driver derives the protocol from the socket's address
family — `AddressFamily.Unix → ConnectionProtocol.UnixDomainSocket`, otherwise
`ConnectionProtocol.Tcp` (`SocketConnectionProtocol.FromAddressFamily`) — and stamps it on:

- the **listener's** `Capabilities` (from the configured endpoint) and the `protocol` payload of its
  `ListenerBound` event (from the bound socket, which also resolves a `FileHandleEndPoint`'s real family);
- every **connection's** `Capabilities` and the `protocol` payload of its `ConnectionOpened` event (from
  the connected socket's family).

So a connection over a Unix domain socket reports `UnixDomainSocket` in its capabilities and event
stream, not `Tcp`. The delivery guarantees (reliable, ordered byte stream, no multiplexing, no
transport security) are identical for both families — only the protocol identity differs.

## Lifecycle and Error Model

- A connection is **live on construction**: the constructor starts the receive and send loops.
- **Graceful half-close** — complete `Output`; **`DisposeAsync`** — close (transitions to `Closed` once
  the loops finish); **`Abort(reason)`** — immediate tear-down (`Aborted`), discarding in-flight data.
  `ConnectionClosed` is signaled on closure.
- Socket reset/abort conditions are classified by `SocketHelper` and surfaced through the
  `ConnectionException` family (`ConnectionResetException` for resets) so consumers catch one hierarchy.
- The listener tracks live accepted connections and disposes them on `DisposeAsync`, then disposes its
  per-IO-queue pipe options. Disposal also releases the listening endpoint and is terminal; restart uses
  a newly constructed listener.
- **No client is left connected to nothing when the listener stops.** On Windows an accept is an
  `AcceptEx` into a socket created before the call, and the OS can attach an incoming client to that
  socket before the accept completes. If the accept is cancelled, or the listening socket closes, at that
  moment, .NET reports the failure without closing the socket it created, and the client waits on a
  connection nobody owns until a finalizer runs. A request sent while a host shut down waited out its
  whole timeout this way (#1093). On Windows the listener therefore accepts into a socket it creates
  itself and disposes it on every path that does not return it; Unix accepts with `accept(2)` after the
  connection is queued, so nothing is attached early. The regression test races 100 cancelled accepts
  against connecting clients; before the fix about 40 of them were left open.
- **A client that resets before the accept costs only its own connection.** A client can connect and
  reset (RST) while its connection waits in the accept queue. Windows then fails that accept with
  `ConnectionReset`. BSD-derived stacks, and Linux in some cases, fail it with `ConnectionAborted`;
  Linux usually returns the connection anyway, already reset. `AcceptAsync` treats those two errors as
  the queued connection's own, raises `AcceptSkipped`, and accepts the next connection. This is the
  `IConnectionListener.AcceptAsync` contract: a failure that belongs to one inbound connection never
  escapes, because a consumer such as the HTTP accept loop treats whatever escapes as the listener's
  end. Until #1308 the error escaped, and one reset stopped an HTTP endpoint. Neither error can recur
  without a new connection, so the retry cannot spin.
- **On Linux, a network error pending on a queued connection is skipped the same way.** Linux
  `accept(2)` passes network errors already pending on the new socket back as the accept's error, and
  its manual says to retry them like `EAGAIN`. `ENETDOWN`, `ENETUNREACH`, `EHOSTDOWN`, `EHOSTUNREACH`,
  `ENOPROTOOPT` and `EOPNOTSUPP` arrive as `NetworkDown`, `NetworkUnreachable`, `HostDown`,
  `HostUnreachable`, `ProtocolOption` and `OperationNotSupported`, and raise `AcceptSkipped`. On
  Windows the same values mean the listening socket failed, so there they still escape. `EOPNOTSUPP`
  also means a listening socket that is not a stream socket, which can never accept, so the skip
  applies only to a stream listener; otherwise an inherited datagram descriptor would spin. The other
  two errors the manual lists, `EPROTO` and `ENONET`, are not skipped: .NET reports them as the generic
  `SocketError.SocketError`, which they share with `ENOMEM`, so they are backed off instead (see
  *Classification works from `SocketError` alone*, below). #1312's acceptance criterion asks for all
  eight to be skipped; these two are the recorded exception.
- **An accepted socket that cannot be set up costs only its own connection.** After the accept, the
  listener reads the socket's local endpoint, sets `TCP_NODELAY` on a TCP socket, and wraps it in a
  connection. Those calls can fail for the one connection: on macOS, setting `TCP_NODELAY` fails with
  `EINVAL` once the client has reset the connection, and a client can reset right after the accept. A
  `SocketException` there closes the socket, which nothing else owns yet, raises
  `AcceptedConnectionDropped`, and accepts the next connection; any other exception closes the socket
  and escapes. Only the accept call itself sits inside the accept-error filters above. They once covered
  the set-up as well, so a set-up error that matched one was skipped without closing the socket, and one
  that matched none stopped the listener.
- **Running out of descriptors or buffers waits and retries (#1312).** `TooManyOpenSockets`
  (`EMFILE`/`ENFILE`, `WSAEMFILE`) and `NoBufferSpaceAvailable` (`ENOBUFS`, `WSAENOBUFS`) are transient:
  the endpoint is healthy again once connections close. Before #1312 they escaped, so a client that held
  enough connections open stopped the endpoint until the host restarted. Retrying at once would spin
  for as long as the exhaustion lasts, which is the known problem with retrying every accept error. So
  `AcceptAsync` waits and retries, on the schedule Go's `net/http` server uses: 5 ms, doubling with each
  consecutive failure, at most 1 s. The schedule belongs to one `AcceptAsync` call, so it starts over
  after every successful accept. The wait links the caller's token to the listener's disposal, so
  cancelling or disposing ends it at once, and the call then throws `OperationCanceledException` or
  `ObjectDisposedException` as it would have without the wait. Each wait raises `AcceptBackoff`, at most
  once a second per listener, with the number of waits it held back since the previous report, so
  sustained or flapping exhaustion cannot flood a trace.
- **Classification works from `SocketError` alone.** On Unix, .NET maps the native `errno` through a
  fixed table, discards it, and reports any value outside the table as the generic
  `SocketError.SocketError`. `ENOMEM` and Linux's `ENOSR` arrive that way, and so do `EPROTO` and `ENONET`,
  two of the pending network errors above. The value cannot tell them apart, so on Unix the generic value
  is backed off for a stream listener: that never spins while memory is short, and it costs a
  misclassified network error one short wait. Windows reports Winsock codes, each of which has its own `SocketError`, so there the generic
  value escapes. `TcpAcceptErrors` holds the classification and `TcpAcceptBackoff` the schedule and the
  report limit; an internal constructor replaces the accept so tests can fail it with errors a real
  socket cannot be made to report on demand.
- Every other accept error leaves the listening socket unable to accept, and escapes.

## Diagnostics

The driver reports through its own internal event source, named for the assembly:
`Assimalign.Cohesion.Connections.Tcp` (`Internal/EventSource/TcpConnectionEventSource.cs`), per the
repository EventSource convention (`.claude/rules/event-source.md`). Nothing about it is public: tools
enable it by name (`dotnet-trace collect --providers Assimalign.Cohesion.Connections.Tcp`,
`dotnet-counters monitor --counters Assimalign.Cohesion.Connections.Tcp`), and an application forwards
it into its logging with `Assimalign.Cohesion.Logging.EventSource`
(`AddEventSourceForwarding()` on the logger factory builder), where the source name becomes the log
category.

| Id | Event | Level | Payload |
|---|---|---|---|
| 1 | `ListenerBound` | Informational | `listenerId`, `protocol`, `endPoint` |
| 2 | `ListenerClosed` | Informational | `listenerId` |
| 3 | `ConnectionOpened` | Informational | `connectionId`, `listenerId` (empty when dialed), `protocol`, `localEndPoint`, `remoteEndPoint` |
| 4 | `ConnectionClosed` | Informational | `connectionId` |
| 5 | `ConnectionFinished` | Verbose | `connectionId` — the peer finished sending (end of stream) |
| 6 | `ConnectionPaused` | Verbose | `connectionId` — receiving paused under application back-pressure |
| 7 | `ConnectionResumed` | Verbose | `connectionId` |
| 8 | `ConnectionReset` | Verbose | `connectionId` |
| 9 | `ConnectionError` | Error | `connectionId`, `operation` (`receiving`/`sending`), `exceptionType`, `exceptionMessage` |
| 10 | `AcceptSkipped` | Verbose | `listenerId`, `socketError` (`ConnectionReset`/`ConnectionAborted`, and on Linux a pending network error): a queued connection that failed before the accept |
| 11 | `AcceptBackoff` | Warning | `listenerId`, `socketError`, `delayMilliseconds`, `unreportedBackoffs`: an accept failed and is retried after the delay. `socketError` names the cause: `TooManyOpenSockets` (out of descriptors), `NoBufferSpaceAvailable` (out of buffers), or, on Unix, `SocketError`, an `errno` .NET does not name, which is `ENOMEM`, `ENOSR`, `EPROTO` or `ENONET`; the message names no cause of its own. At most one per listener per second; `unreportedBackoffs` counts the waits held back since the previous report |
| 12 | `AcceptedConnectionDropped` | Verbose | `listenerId`, `socketError`: the listener closed a socket it had accepted because setting it up failed, typically because the client reset it right after the accept |

Counters: `current-connections`, `total-connections`, and `connections-per-second`.

Ordering and pairing are guaranteed, which is what keeps `current-connections` exact:

- `ConnectionOpened` is raised in the constructor **before** the pump loops start, so no loop event can
  precede it.
- `ConnectionClosed` is raised exactly once, from the receive loop's close path (the one place the
  connection-closed signal is scheduled), and **before** that signal completes — so `DisposeAsync` never
  returns ahead of it. The driver previously raised no close event at all.
- Lifetimes use `Bound`/`Opened`/`Closed` names, not `Start`/`Stop`: a connection opens on the accept
  loop's flow and closes on the receive loop's, and EventSource's activity tracking would otherwise
  nest every connection under the previous one.

## AOT Posture

No reflection, no runtime code generation, no serialization. Sockets, `System.IO.Pipelines`, pooled
async socket-event args, and `System.Diagnostics.Tracing` counters — all from the shared framework.
`AllowUnsafeBlocks` is enabled for the pooled socket-IO fast paths. Fully NativeAOT/trim compatible
(`IsAotCompatible=true`).

## Non-Goals

- **No datagram or multiplexed shape.** UDP datagrams live in `Connections.Udp`; QUIC multiplexing in
  `Connections.Quic`. This driver is single-stream only.
- **No transport security of its own.** A TLS connection is produced by composing the Connections
  `Security` layer over a connection; `Capabilities.Security` stays `None` here.
- **No protocol branching on `ConnectionProtocol`.** It is for diagnostics and observability only.
- **No named-pipe support.** The Windows-native local IPC transport is the sibling
  `Connections.NamedPipes` driver; this driver's local IPC form is the Unix domain socket.

## Relationships

- **`Assimalign.Cohesion.Connections`** — the guided bases (`Connection`, `ConnectionListener`,
  `ConnectionFactory`), `ConnectionCapabilities` / `ConnectionProtocol`, the exception family, and
  `ListenerId`. Diagnostics are not taken from it: this driver owns its event source (see
  *Diagnostics*). Its pipe plumbing (`DuplexPipePair`,
  `PipeOptionsContext`, `PipeOptionsFactory`) is compiled into this driver from that
  library's `shared/` folder via `CohesionSharedSource` - internal here, and never part of
  the contracts assembly's public surface.
- **`Assimalign.Cohesion.Connections.NamedPipes`** — the sibling local-IPC driver; its named pipe is the
  Windows-native counterpart to this driver's Unix domain socket.
- **`Assimalign.Cohesion.Security`** — TLS as a connection layer composed over this driver.
- **`Assimalign.Cohesion.Http.Connections`** — the primary consumer, composing this listener via
  `UseHttp1/UseHttp2(IConnectionListener)` (HTTP-over-UDS is validated end-to-end by
  `HttpOverUnixDomainSocketTests`).
