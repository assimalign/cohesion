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
(`DuplexPipePair`, from the core library's internal toolbox):

- **Receive loop** reads from the socket into the transport-output pipe writer, applying back-pressure
  when the consumer is slow (the flush pauses the loop).
- **Send loop** reads from the transport-input pipe reader and writes to the socket via a pooled sender.

`connection.Input` is what the peer sent; `connection.Output` is what you send. The mirrored pump ends
live in the internal `DuplexPipePair` and never surface on the contract. Socket tuning (adaptive memory
pool block size, IO-queue schedulers, read/write buffer thresholds) comes from
`SocketPipeOptionsFactory` and is shared per listener across its connections.

## Endpoint Handling (the bind switch)

`TcpConnectionListener` binds lazily on the first `AcceptAsync`, and the endpoint form selects the bind
strategy:

| Endpoint | Socket | Bind behavior |
|---|---|---|
| `IPEndPoint` | `Stream`/`Tcp`, `DualMode` when `IPv6Any` | `Bind` + `Listen` |
| `UnixDomainSocketEndPoint` | `Stream`/`Unspecified` | delete stale socket file → `Bind` + `Listen` |
| `FileHandleEndPoint` | adopt the inherited descriptor | **no** `Bind`/`Listen` — already listening |

The factory (`TcpConnectionFactory`) uses the same switch to construct the outbound socket, then
`ConnectAsync`.

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

- the **listener's** `Capabilities` (from the configured endpoint) and its `ListenerInitialized`
  diagnostic (from the bound socket, which also resolves a `FileHandleEndPoint`'s real family);
- every **connection's** `Capabilities` and all of its `ConnectionEventSource` events (from the
  connected socket's family).

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
  per-IO-queue pipe options.

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
  `ConnectionFactory`), `ConnectionCapabilities` / `ConnectionProtocol`, the exception family, and the
  internal toolbox (`DuplexPipePair`, pipe options, `ConnectionEventSource`, `ListenerId`) shared via
  `InternalsVisibleTo`.
- **`Assimalign.Cohesion.Connections.NamedPipes`** — the sibling local-IPC driver; its named pipe is the
  Windows-native counterpart to this driver's Unix domain socket.
- **`Assimalign.Cohesion.Security`** — TLS as a connection layer composed over this driver.
- **`Assimalign.Cohesion.Http.Connections`** — the primary consumer, composing this listener via
  `UseHttp1/UseHttp2(IConnectionListener)` (HTTP-over-UDS is validated end-to-end by
  `HttpOverUnixDomainSocketTests`).
