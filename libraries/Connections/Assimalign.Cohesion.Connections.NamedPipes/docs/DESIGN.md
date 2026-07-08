# Assimalign.Cohesion.Connections.NamedPipes Design

## Design Intent

Provide a named-pipe implementation of the Connections contracts: the Windows-native local
inter-process transport, and the cross-platform peer of the Unix domain socket path in the stream
(`Tcp`) driver. It is the same *shape* as the socket drivers — a reliable, ordered, single-stream
`IConnectionListener` / `IConnectionFactory` producing `IConnection`s — but the wire is a named pipe
(`NamedPipeServerStream` / `NamedPipeClientStream`) rather than a socket.

Named pipes matter for Cohesion's own architecture. The ApplicationModel LocalGateway spawns
co-located service processes; local IPC over a named pipe (or a Unix domain socket) removes port
allocation, firewall, and loopback-security concerns and replaces them with filesystem/ACL access
control. Because the Connections stack selects transports by **capability, not protocol identity**, a
named-pipe listener drops into the same `UseHttp1/2(IConnectionListener)` seams a TCP or UDS listener
uses, with no change to the HTTP stack above it.

The driver is a peer of `Tcp` / `Udp` / `Quic` / `InMemory`: it lives beside them under
`libraries/Connections`, depends only on the Connections contracts (plus `Core`), and adds no contract
changes. This mirrors Kestrel's `ListenNamedPipe` surface, expressed through the Cohesion connection
model instead of a bespoke transport abstraction.

## The Stream Bridge

A named pipe is a `Stream` (`PipeStream`), not a socket, so this driver does not run the socket
receive/send pump loops the `Tcp` driver does. Instead `NamedPipeConnection` bridges the pipe stream to
the connection's duplex pipe with the BCL adapters:

```
connection.Input  = PipeReader.Create(pipeStream, leaveOpen: true)
connection.Output = PipeWriter.Create(pipeStream, leaveOpen: true)
```

These are pull-based: a read pulls from the stream on demand, and a flush writes buffered bytes to the
stream — no background copy loop, no extra thread. This is the same technique the TLS layer
(`Assimalign.Cohesion.Security`) uses to bridge an `SslStream` to a connection, and it keeps the driver
allocation-light and trim-safe. The pipe stream is left open by both adapters; the connection owns it
and disposes it exactly once on tear-down.

## Accept Model

A `NamedPipeServerStream` instance serves exactly one client, so the listener creates a fresh instance
per accept. `AcceptAsync` creates a server-stream instance (the first call reserves the pipe name),
awaits `WaitForConnectionAsync`, and returns a `NamedPipeConnection` wrapping the connected instance;
the next `AcceptAsync` creates the next instance. A continuously-accepting consumer (the HTTP connection
listener loops accept) therefore always has one instance waiting, so the pipe name stays live between
connections. `MaxServerInstances` (default: unlimited) bounds how many instances may share the name; the
practical bound is the consumer's concurrency, not the transport.

The in-flight listening instance is tracked so `DisposeAsync` can dispose it, which unblocks a pending
`WaitForConnectionAsync` (it observes `ObjectDisposedException`) and surfaces as
`OperationCanceledException` from the pending `AcceptAsync` — the same disposal contract the other
drivers honor. A client that aborts mid-handshake surfaces as an `IOException`, which the accept loop
absorbs by retrying with a fresh instance.

## Buffer Sizing

The pipe buffers default to a non-zero size (`NamedPipeConnectionListenerOptions.DefaultBufferSize`, 4
KiB). This gives the pipe socket-like semantics: a peer's small write completes without this end
actively reading, exactly as a TCP send buffer would absorb it. A buffer size of `0` requests the
operating-system default, under which — on Windows — a write can block until the reader drains it (a
synchronous rendezvous), which is surprising for a connection abstraction. The default is deliberately
non-zero so write-then-read on a single task does not deadlock; consumers that want strict flow-control
realism can lower it.

## Access Control

Access control is applied at pipe-creation time (builder-time), never per request:

- **`PipeSecurity` (Windows).** When set, the server pipe is created through
  `NamedPipeServerStreamAcl.Create`, so the caller supplies a full Windows ACL controlling which
  principals may connect. Reading and using this option is guarded by `OperatingSystem.IsWindows()`
  behind a `[SupportedOSPlatform("windows")]` helper, so the analyzer is satisfied and non-Windows
  builds never touch the Windows-only API. A `PipeSecurity` value can only be constructed on Windows, so
  the option is naturally inert elsewhere.
- **`CurrentUserOnly`.** A cross-platform convenience that restricts the pipe to the current user via
  `PipeOptions.CurrentUserOnly`. It is ignored when an explicit `PipeSecurity` is supplied, since the ACL
  fully specifies the policy (and `NamedPipeServerStreamAcl.Create` rejects the combination).

A `NamedPipeConnectionListener` can only bind a pipe on the local host (`NamedPipeEndPoint.ServerName`
must be `"."`), because a named-pipe server cannot be created on a remote machine; a
`NamedPipeConnectionFactory` may dial a remote host.

## Lifecycle and Error Model

- A connection is **live when produced**. Read `Input` and write `Output` immediately.
- **Graceful dispose** completes both pipe halves without an error and disposes the pipe stream, so the
  peer observes end-of-stream on its next read; state becomes `Closed`. Dispose is idempotent.
- **Abort(reason)** completes the halves with the reason and disposes the stream so the peer's next
  read/write faults; state becomes `Aborted` and `ConnectionClosed` is signaled. A `null` reason
  surfaces a `ConnectionAbortedException`.
- `ConnectionClosed` fires when **this** end is disposed or aborted; a peer close is observed by reading
  `Input` (which completes) or writing `Output` (whose flush faults) — exactly how a byte-stream
  consumer such as an HTTP parser already detects end-of-connection. There is no background watcher.

## AOT Posture

No reflection, no runtime code generation, no serialization. A pure `System.IO.Pipes` +
`System.IO.Pipelines` composition over BCL types, with `System.Diagnostics.Tracing` counters shared from
the core Connections library. Fully NativeAOT/trim compatible (`IsAotCompatible=true`). The Windows-only
ACL surface is isolated behind `[SupportedOSPlatform("windows")]` guards so no platform-specific code is
reachable on other operating systems.

## Non-Goals

- **No message framing.** The pipe is created in `PipeTransmissionMode.Byte` (a byte stream), never
  message mode; the transport delivers an ordered byte stream and leaves framing to the protocol above,
  consistent with the single-stream connection shape.
- **No datagram or multiplexed variant.** Named pipes model a single reliable, ordered stream.
- **No security layer of its own beyond OS access control.** A TLS-secured named-pipe connection is
  produced by composing the Connections `Security` (TLS) layer over it, exactly as for any other
  transport; ACLs handle local authorization.
- **No request-time service location or per-connection middleware.** Access control and tuning are
  builder-time options, matching the Connections philosophy.

## Relationships

- **`Assimalign.Cohesion.Connections`** — the `Connection` / `ConnectionListener` / `ConnectionFactory`
  guided bases this driver implements, plus `ConnectionCapabilities`, `ConnectionProtocol.NamedPipe`,
  `ConnectionAbortedException`, `ListenerId`, and the shared `ConnectionEventSource` diagnostics.
- **`Assimalign.Cohesion.Connections.Tcp`** — the sibling stream driver whose Unix domain socket path is
  the POSIX counterpart to this Windows-native local IPC transport.
- **`Assimalign.Cohesion.Http.Connections`** — composes this listener via
  `UseHttp1/UseHttp2(IConnectionListener)` with no HTTP-layer change (validated by the
  `HttpOverNamedPipeTests` end-to-end test).
- **ApplicationModel LocalGateway** — the intended consumer: co-located service processes addressed by a
  named pipe instead of a loopback port.
