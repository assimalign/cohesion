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
- `IConnectionListener` / `ConnectionListener` — explicitly binds and accepts inbound connections
  (server side).
- `IConnectionFactory` / `ConnectionFactory` — establishes outbound connections (client side).
- `IMultiplexedConnection` / `MultiplexedConnection` — carries multiple `IConnection` streams;
  `OpenStreamAsync(ConnectionDirection, ...)` for bidirectional or unidirectional streams.
- `IMultiplexedConnectionListener` / `IMultiplexedConnectionFactory` (+ bases).
- `IDatagramConnection` / `DatagramConnection` — message-oriented send/receive (e.g., UDP).
- `IConnectionLayer` — connection-to-connection transformation; composed via
  `listener.Use(layer)` / `factory.Use(layer)`. A layered listener upgrades each accepted connection
  on its own task, at most 512 at a time (`listener.Use(layer, maxConcurrentUpgrades)`), and a failed
  or timed-out upgrade closes only that connection.
- `ITlsConnectionInfo` — what a TLS handshake negotiated (ALPN application protocol, TLS version,
  cipher suite, the peer's certificate), implemented by connections that terminate TLS and found
  with a type test.
- `IMultiplexedStreamAbort` — abandons one direction of a multiplexed stream with an application error
  code (`AbortRead` sends QUIC `STOP_SENDING`, `AbortWrite` sends `RESET_STREAM`), implemented by the
  QUIC and in-memory drivers' streams and found with a type test.
- `IMultiplexedConnectionAbort` — aborts a multiplexed connection with an application error code on its
  close (QUIC `CONNECTION_CLOSE`), implemented by the QUIC driver and found with a type test.
- `ConnectionCapabilities`, `ConnectionProtocol`, `ConnectionDelivery`, `ConnectionSecurity`,
  `ConnectionDirection`, `ConnectionState`, `ConnectionId`, `DatagramReceiveResult`.
- `DuplexPipeStream`, `ConnectionExtensions.AsStream()` — lazy pipe-to-stream adaptation.
- `ConnectionException`, `ConnectionAbortedException`, `ConnectionResetException` (which carries the
  peer's `ApplicationErrorCode` when a driver reports a coded stream reset through it).

## Source Layout

- `src/Abstractions` — interfaces (connection shapes, listeners/factories, the layer arrow).
- `src/` — guided abstract base classes and `DuplexPipeStream`.
- `src/Extensions` — layer composition (`Use`) and connection conveniences (`AsStream`).
- `src/Internal` — layered listener/factory decorators, and the layered listener's internal event
  source (`Assimalign.Cohesion.Connections`).
- `src/ValueObjects` — value types and enums.
- `src/Exceptions` — area exception root and specific exceptions.
