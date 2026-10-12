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
  anchored to the holder), with `Direction`, `Capabilities`, `Features`, `State`, and lifetime members.
- `IConnectionListener` / `ConnectionListener` — explicitly binds and accepts inbound connections
  (server side).
- `IConnectionFactory` / `ConnectionFactory` — establishes outbound connections (client side).
- `IMultiplexedConnection` / `MultiplexedConnection` — carries multiple `IConnection` streams;
  `OpenStreamAsync(ConnectionDirection, ...)` for bidirectional or unidirectional streams.
- `IMultiplexedConnectionListener` / `IMultiplexedConnectionFactory` (+ bases).
- `IConnectionFeature` / `IConnectionFeatureCollection` / `ConnectionFeatureCollection` — typed,
  named optional capabilities on all three connection shapes, with own features before wrapped
  features, replacement by name, and removal that hides the wrapped name.
- `IDatagramConnection` / `DatagramConnection` — message-oriented send/receive (e.g., UDP).
- `IConnectionLayer` — connection-to-connection transformation; composed via
  `listener.Use(layer)` / `factory.Use(layer)`. A layered listener upgrades each accepted connection
  on its own task, at most 512 at a time (`listener.Use(layer, maxConcurrentUpgrades)`), and a failed
  or timed-out upgrade closes only that connection.
- Optional packages keep the family namespace: `Connections.Tls` owns `ITlsConnectionInfo`
  (`connection.Tls`); `Connections.StreamAbort` owns `IMultiplexedStreamAbort`
  (`connection.DirectionalAbort`); `Connections.StreamLimits` owns
  `IMultiplexedConnectionStreamLimits` (`connection.StreamLimits`).
- `IConnectionInputCompletion` — optional clean transport input completion; combine it with an empty
  input buffer before concluding that no bytes remain. The QUIC driver publishes this facet.
- `IMultiplexedConnectionAbort` — aborts a multiplexed connection with an application error code on its
  close (QUIC `CONNECTION_CLOSE`), implemented by the QUIC driver and found with a type test.
- `ConnectionCapabilities`, `ConnectionProtocol`, `ConnectionDelivery`, `ConnectionSecurity`,
  `ConnectionDirection`, `ConnectionState`, `ConnectionId`, `DatagramReceiveResult`.
- `DuplexPipeStream`, `ConnectionExtensions.AsStream()` — lazy pipe-to-stream adaptation.
- `ConnectionException`, `ConnectionAbortedException`, `ConnectionResetException` (which carries the
  peer's `ApplicationErrorCode` when a driver reports a coded stream reset through it).

## Registering and reading features

Drivers override `ConfigureFeatures(IConnectionFeatureCollection)` to register their capabilities.
Layers override `InnerFeatures` with their wrapped connection's `Features`, so optional capabilities
remain visible without forwarding individual contracts. The guided bases create and configure the
collection once on first access. Consumers may register their own typed features with
`connection.Features.Set(feature)`, look up by exact name or `Get<TFeature>()`, or hide an inherited
name with `Remove(name)`. Cache a feature when using it repeatedly during I/O.

Adding `Features` to `IConnection`, `IMultiplexedConnection`, and `IDatagramConnection` breaks direct
implementers of 10.0.0-preview.1. The guided bases provide the implementation; see the accepted
breaking change and layering rules in [DESIGN.md](DESIGN.md).

## Source Layout

- `src/Abstractions` — interfaces (connection shapes, listeners/factories, the layer arrow).
- `src/` — guided abstract base classes and `DuplexPipeStream`.
- `src/Extensions` — layer composition (`Use`) and connection conveniences (`AsStream`).
- `src/Internal` — layered listener/factory decorators, and the layered listener's internal event
  source (`Assimalign.Cohesion.Connections`).
- `src/ValueObjects` — value types and enums.
- `src/Exceptions` — area exception root and specific exceptions.
