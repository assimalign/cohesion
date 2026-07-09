# Assimalign.Cohesion.Connections.Tcp — Overview

## Purpose

The socket-backed stream driver for the Cohesion Connections contracts. It produces reliable, ordered,
single-stream connections over a `System.Net.Sockets.Socket` and is the default production transport for
HTTP/1.1 and HTTP/2. One driver serves both TCP-over-IP and Unix domain sockets — the same socket data
path, differing only in the endpoint and address family — plus adoption of a listening socket handed off
by a parent process (socket activation).

## Scope

- `TcpConnectionListener` — the accept side: binds lazily on first accept and produces live
  `Connection`s. Binds an `IPEndPoint`, a `UnixDomainSocketEndPoint` (with socket-file lifecycle), or
  adopts a `FileHandleEndPoint` (inherited descriptor).
- `TcpConnectionFactory` — the dial side: connects an outbound socket to any of the same endpoint forms.
- `TcpConnectionListenerOptions` / `TcpConnectionFactoryOptions` — backlog, Nagle, IO-queue count,
  read/write buffer thresholds, and scheduling.

Local IPC is served here via Unix domain sockets; the Windows-native equivalent is the sibling
`Assimalign.Cohesion.Connections.NamedPipes` driver.

## Dependencies

- `Assimalign.Cohesion.Connections` — the contracts this driver implements plus the shared internal
  toolbox (duplex-pipe wiring, pipe options, diagnostics) consumed via `InternalsVisibleTo`.
- `Assimalign.Cohesion.Core` — `FileHandleEndPoint` and shared primitives.
- `System.Net.Sockets` and `System.IO.Pipelines` from the shared framework. No third-party packages, no
  reflection.

## Usage

```csharp
// TCP over IP.
await using TcpConnectionListener listener = TcpConnectionListener.Create(
    options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 8080));

// Unix domain socket (local IPC) — same API, honest UnixDomainSocket capabilities/diagnostics.
await using TcpConnectionListener uds = TcpConnectionListener.Create(
    options => options.EndPoint = new UnixDomainSocketEndPoint("/tmp/cohesion.sock"));

ValueTask<Connection> accept = listener.AcceptAsync(cancellationToken);

TcpConnectionFactory factory = new();
Connection dialed = await factory.ConnectAsync(listener.EndPoint, cancellationToken);
Connection accepted = await accept;
```

See [DESIGN.md](DESIGN.md) for the data path, the endpoint bind switch, the Unix domain socket file
lifecycle, socket activation, and honest protocol stamping.
