# Assimalign.Cohesion.Connections.NamedPipes — Overview

## Purpose

The named-pipe driver for the Cohesion Connections contracts: the Windows-native local
inter-process transport, and the cross-platform peer of the Unix domain socket path in the
stream (`Tcp`) driver. It produces reliable, ordered, single-stream connections over
`NamedPipeServerStream` / `NamedPipeClientStream` for same-host IPC — the ApplicationModel
LocalGateway addressing co-located service processes by pipe name instead of a loopback
port. Because transports are selected by capability, a named-pipe listener composes into the
same `UseHttp1/2(IDuplexConnectionListener)` seams a socket listener does, with no HTTP-layer
change. This mirrors Kestrel's `ListenNamedPipe` surface.

## Scope

- `NamedPipeConnectionListener` — the accept side: reserves the pipe through `BindAsync`, then
  creates a fresh `NamedPipeServerStream` instance per accepted client on a local pipe name, with
  builder-time access control.
- `NamedPipeConnectionFactory` — the dial side: opens a `NamedPipeClientStream` to a
  `NamedPipeEndPoint` (local or remote host) and returns a live `DuplexConnection`.
- `NamedPipeEndPoint` — a name-addressed `EndPoint` (`ServerName` + `PipeName`), formatted as
  `\\server\pipe\name`.
- `NamedPipeConnectionListenerOptions` / `NamedPipeConnectionFactoryOptions` — buffer sizing,
  write-through, `MaxServerInstances`, and access control (`PipeSecurity` ACL on Windows,
  `CurrentUserOnly` cross-platform).

Protocol semantics belong to the layers above; this driver only moves an ordered byte stream.

## Dependencies

- `Assimalign.Cohesion.Connections` — the contracts this driver implements (`DuplexConnection`,
  `DuplexConnectionListener`, `DuplexConnectionFactory`) plus `ConnectionProtocol.NamedPipe`. Diagnostics are
  the driver's own internal event source, `Assimalign.Cohesion.Connections.NamedPipes` (see
  DESIGN.md, "Diagnostics").
- `Assimalign.Cohesion.Core`.
- `System.IO.Pipes` and `System.IO.Pipelines` from the shared framework. No third-party
  packages, no reflection. Windows ACL support is isolated behind
  `[SupportedOSPlatform("windows")]` guards.

## Usage

```csharp
// Server: accept connections on a local pipe.
await using NamedPipeConnectionListener listener = NamedPipeConnectionListener.Create(
    options => options.EndPoint = new NamedPipeEndPoint("cohesion-gateway"));

ValueTask<DuplexConnection> accept = listener.AcceptAsync(cancellationToken);

// Client: dial the same pipe.
NamedPipeConnectionFactory factory = new();
DuplexConnection dialed = await factory.ConnectAsync(new NamedPipeEndPoint("cohesion-gateway"), cancellationToken);
DuplexConnection accepted = await accept;

// Compose HTTP/1.1 over it, exactly like a socket listener:
await using HttpConnectionListener http = HttpConnectionListener.Create(o => o.UseHttp1(listener));
```

See [DESIGN.md](DESIGN.md) for the stream bridge, accept model, buffer sizing, and access-control design.

Listener options reject invalid numeric values when assigned with `ArgumentOutOfRangeException`.
`MaxServerInstances` accepts `1` through `254` or `NamedPipeServerStream.MaxAllowedServerInstances`
(`-1`, the default). `InputBufferSize` and `OutputBufferSize` accept nonnegative byte counts; `0`
continues to select the operating-system default, and both default to 4 KiB. The ranges follow the
[.NET 10 server-pipe API](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.IO.Pipes/src/System/IO/Pipes/NamedPipeServerStream.cs#L99-L106).
Relative to 10.0.0-preview.1, invalid values fail during configuration rather than bind or a later
accept, including invalid changes to retained options.

The driver participates in the common `IConnection` identity, feature, and lifecycle hierarchy.
Listeners expose a stable `Id` and live `Connections`. Entries disappear on closure; disposing
the listener leaves accepted connections open for their owners to dispose. `ConnectionClosed`
fires on abort, observed peer closure, and no later than local disposal.
