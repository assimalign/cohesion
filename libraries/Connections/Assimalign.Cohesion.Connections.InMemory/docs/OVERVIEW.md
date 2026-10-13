# Assimalign.Cohesion.Connections.InMemory — Overview

## Purpose

The in-memory driver for the Cohesion Connections contracts. It produces cross-wired
duplex-pipe connection pairs — a listener plus a dial factory, and a multiplexed variant —
so application protocols can be exercised over a live, socketless byte channel. It is the
shared foundation for deterministic transport testing across the repo, replacing the
divergent private pipe-pair connection doubles the transport test projects previously each
maintained.

## Scope

- `InMemoryConnectionPair.Create(...)` — the primitive: a connected pair of `DuplexConnection`
  ends whose duplex pipes are cross-wired, supporting live multi-round-trip exchange.
- `InMemoryConnectionListener` / `InMemoryConnectionFactory` — the dial-connected
  server/client pair: the factory dials the listener, the listener accepts the server end.
- `InMemoryMultiplexedConnectionPair.Create(...)`,
  `InMemoryMultiplexedConnectionListener` / `InMemoryMultiplexedConnectionFactory` — the
  multiplexed variant for h2/h3-shaped stream tests, where each opened stream is itself an
  in-memory `MultiplexedConnectionStream` with whole-stream `Abort(code, reason)` and `IsInputCompleted`.
  It can also abandon either direction with an application error code
  (`stream.DirectionalAbort`, from `Connections.StreamAbort`); the other end observes the code on a
  `ConnectionResetException`. Both stream ends register that feature; byte-stream pairs have no directional-abort feature.
- `InMemoryEndPoint` — a name-addressed `EndPoint` for the socketless transport.

Closing or aborting either multiplexed parent notifies both ends and completes both accept loops.
Active streams fail with `ConnectionResetException.ApplicationErrorCode`: the selected close code,
or 0 for disposal and no-code abort. The first pair close wins.

Protocol semantics belong to the layers above; this driver only moves bytes.

## Dependencies

- `Assimalign.Cohesion.Connections` — the contracts this driver implements (`DuplexConnection`,
  `DuplexConnectionListener`, `DuplexConnectionFactory`, and the multiplexed counterparts).
- `Assimalign.Cohesion.Core`.
- `System.IO.Pipelines` and `System.Threading.Channels` from the shared framework. No
  operating-system socket, no reflection, no third-party packages.

## Usage

```csharp
// A connected pair, driven directly.
(DuplexConnection client, DuplexConnection server) = InMemoryConnectionPair.Create();
await client.Output.WriteAsync(request);
ReadResult received = await server.Input.ReadAsync();

// Or the dial-connected listener/factory pair (the shape a server accept loop consumes).
await using InMemoryConnectionListener listener = new();
InMemoryConnectionFactory factory = listener.CreateFactory();

ValueTask<DuplexConnection> accept = listener.AcceptAsync(cancellationToken);
DuplexConnection dialed = await factory.ConnectAsync(listener.EndPoint, cancellationToken);
DuplexConnection accepted = await accept;
```

See [DESIGN.md](DESIGN.md) for the pair wiring and the close/dispose/abort semantics.

The driver participates in the common `IConnection` identity, feature, and lifecycle hierarchy.
Listeners expose a stable `Id` and live `Connections`. Entries disappear on closure; disposing
the listener leaves accepted connections open for their owners to dispose. `ConnectionClosed`
fires on abort, observed peer closure, and no later than local disposal.
