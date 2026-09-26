# Assimalign.Cohesion.Connections.Udp — Overview

## Purpose

The message-oriented UDP driver for the Cohesion Connections contracts. It produces
`IDatagramConnection`s: a server socket bound to a local endpoint that receives from any peer, or a
client socket connected to one remote peer. Datagrams keep their boundaries — one send is one message,
one receive is one message — which is why this shape is not an `IConnection` byte stream.

## Scope

- `UdpConnectionFactory` — `Bind`/`BindAsync` for the server role and `Connect` for the client role.
  UDP has no listener/accept lifecycle, so one factory serves both.
- `UdpBindOptions` / `UdpConnectOptions` — endpoint, dual-mode IPv6, address reuse (bind only), and
  socket buffer sizes.
- IPv4 and IPv6 only.

## Dependencies

- `Assimalign.Cohesion.Connections` — the `IDatagramConnection` contract, the `DatagramConnection`
  guided base, `DatagramReceiveResult`, and `ConnectionCapabilities`.
- `Assimalign.Cohesion.Core`.
- `System.Net.Sockets` from the shared framework. No third-party packages, no reflection.

## Usage

```csharp
UdpConnectionFactory factory = new();

await using IDatagramConnection server = factory.Bind(new IPEndPoint(IPAddress.Loopback, 0));
await using IDatagramConnection client = factory.Connect(server.LocalEndPoint);

await client.SendAsync(payload, server.LocalEndPoint, cancellationToken);
DatagramReceiveResult result = await server.ReceiveAsync(buffer, cancellationToken);
```

## Diagnostics

Datagram connection lifecycle events and counters are reported through the driver's internal event
source, `Assimalign.Cohesion.Connections.Udp`. Enable it by name in `dotnet-trace` /
`dotnet-counters`, or forward it into an application's logging with
`Assimalign.Cohesion.Logging.EventSource`. See [DESIGN.md](DESIGN.md#diagnostics).
