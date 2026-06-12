# Assimalign.Cohesion.Connections

## Summary

Connection, listener, factory, and layer contracts shared by Cohesion network transports and the
application protocols built on top of them. This is the lowest layer of the networking stack:
concrete transports implement it, application protocols consume it, and connection
transformations (such as TLS) compose over it.

## Dependencies

- `Assimalign.Cohesion.Core`

## Key Types

- `IConnection` / `Connection` — a live byte channel; **is** an `IDuplexPipe` (`Input`/`Output`
  anchored to the holder), with `Direction`, `Capabilities`, `State`, and lifetime members.
- `IConnectionListener` / `ConnectionListener` — accepts inbound connections (server side).
- `IConnectionFactory` / `ConnectionFactory` — establishes outbound connections (client side).
- `IMultiplexedConnection` / `MultiplexedConnection` — carries multiple `IConnection` streams;
  `OpenStreamAsync(ConnectionDirection, ...)` for bidirectional or unidirectional streams.
- `IMultiplexedConnectionListener` / `IMultiplexedConnectionFactory` (+ bases).
- `IDatagramConnection` / `DatagramConnection` — message-oriented send/receive (e.g., UDP).
- `IConnectionLayer` — connection-to-connection transformation; composed via
  `listener.Use(layer)` / `factory.Use(layer)`.
- `ConnectionCapabilities`, `ConnectionProtocol`, `ConnectionDelivery`, `ConnectionSecurity`,
  `ConnectionDirection`, `ConnectionState`, `ConnectionId`, `DatagramReceiveResult`.
- `DuplexPipeStream`, `ConnectionExtensions.AsStream()` — lazy pipe-to-stream adaptation.
- `ConnectionException`, `ConnectionAbortedException`, `ConnectionResetException`.

## Source Layout

- `src/Abstractions` — interfaces (connection shapes, listeners/factories, the layer arrow).
- `src/` — guided abstract base classes and `DuplexPipeStream`.
- `src/Extensions` — layer composition (`Use`) and connection conveniences (`AsStream`).
- `src/Internal` — layered listener/factory decorators.
- `src/ValueObjects` — value types and enums.
- `src/Exceptions` — area exception root and specific exceptions.
