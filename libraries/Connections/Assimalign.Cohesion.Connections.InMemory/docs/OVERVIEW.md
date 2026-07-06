# Assimalign.Cohesion.Connections.InMemory — Overview

## Purpose

The in-memory driver for the Cohesion Connections contracts. It produces cross-wired
duplex-pipe connection pairs — a listener plus a dial factory, and a multiplexed variant —
so application protocols can be exercised over a live, socketless byte channel. It is the
shared foundation for deterministic transport testing across the repo, replacing the
divergent private pipe-pair connection doubles the transport test projects previously each
maintained.

## Scope

- `InMemoryConnectionPair.Create(...)` — the primitive: a connected pair of `Connection`
  ends whose duplex pipes are cross-wired, supporting live multi-round-trip exchange.
- `InMemoryConnectionListener` / `InMemoryConnectionFactory` — the dial-connected
  server/client pair: the factory dials the listener, the listener accepts the server end.
- `InMemoryMultiplexedConnectionPair.Create(...)`,
  `InMemoryMultiplexedConnectionListener` / `InMemoryMultiplexedConnectionFactory` — the
  multiplexed variant for h2/h3-shaped stream tests, where each opened stream is itself an
  in-memory `Connection`.
- `InMemoryEndPoint` — a name-addressed `EndPoint` for the socketless transport.

Protocol semantics belong to the layers above; this driver only moves bytes.

## Dependencies

- `Assimalign.Cohesion.Connections` — the contracts this driver implements (`Connection`,
  `ConnectionListener`, `ConnectionFactory`, and the multiplexed counterparts).
- `Assimalign.Cohesion.Core`.
- `System.IO.Pipelines` and `System.Threading.Channels` from the shared framework. No
  operating-system socket, no reflection, no third-party packages.

## Usage

```csharp
// A connected pair, driven directly.
(Connection client, Connection server) = InMemoryConnectionPair.Create();
await client.Output.WriteAsync(request);
ReadResult received = await server.Input.ReadAsync();

// Or the dial-connected listener/factory pair (the shape a server accept loop consumes).
await using InMemoryConnectionListener listener = new();
InMemoryConnectionFactory factory = listener.CreateFactory();

ValueTask<Connection> accept = listener.AcceptAsync(cancellationToken);
Connection dialed = await factory.ConnectAsync(listener.EndPoint, cancellationToken);
Connection accepted = await accept;
```

See [DESIGN.md](DESIGN.md) for the pair wiring and the close/dispose/abort semantics.
