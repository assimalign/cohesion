# Connections

The connection layer of the Cohesion networking stack: the contracts for accepting,
establishing, layering, and using network connections, plus the concrete drivers that implement
them.

## Purpose

Everything that produces or consumes a network byte channel goes through this area. The
contracts live in one library; protocol drivers implement them; application protocols (HTTP,
AMQP) and connection transformations (TLS in `libraries/Security`) consume them. There is
deliberately no transport abstraction — see the naming rule and full design rationale in
[Assimalign.Cohesion.Connections/docs/DESIGN.md](Assimalign.Cohesion.Connections/docs/DESIGN.md).

## Projects

| Project | Role |
|---|---|
| `Assimalign.Cohesion.Connections` | The contracts: `IConnection` (a live duplex pipe), `IConnectionListener` / `IConnectionFactory`, `IMultiplexedConnection` (+ listener/factory), `IDatagramConnection`, the `IConnectionLayer` composition arrow, `ConnectionCapabilities`, and the guided abstract bases. Also carries the internal driver toolbox (pipe-pair wiring, pipe options, diagnostics). |
| `Assimalign.Cohesion.Connections.Tcp` | Reliable, ordered, single-stream TCP driver (`TcpConnectionListener` / `TcpConnectionFactory`). |
| `Assimalign.Cohesion.Connections.Udp` | Message-oriented UDP datagram driver (`UdpConnectionFactory` → `IDatagramConnection`). |
| `Assimalign.Cohesion.Connections.Quic` | Reliable, ordered, multiplexed QUIC driver (`QuicConnectionListener` / `QuicConnectionFactory`); each stream is itself an `IConnection` with a `ConnectionDirection`. |
| `Assimalign.Cohesion.Connections.InMemory` | Socketless in-memory driver: cross-wired duplex-pipe connection pairs (`InMemoryConnectionListener` / `InMemoryConnectionFactory`, plus a multiplexed variant) for deterministic, live transport testing. |

## Layering

This area is the lowest networking layer (L1 in the repo's layering model): it depends only on
`Assimalign.Cohesion.Core`. Consumers select transports by **capability**
(`ConnectionCapabilities`: delivery mode, reliability, ordering, multiplexing, security), never
by protocol identity. Direction is structural — servers hold listeners, clients hold factories —
and connection transformations compose at establishment via `listener.Use(layer)` /
`factory.Use(layer)`.

## Dependencies

- `Assimalign.Cohesion.Core` (all projects)
- Drivers additionally depend on `Assimalign.Cohesion.Connections` (contracts + internal toolbox
  via `InternalsVisibleTo`).

## Further Reading

- [Assimalign.Cohesion.Connections/docs/OVERVIEW.md](Assimalign.Cohesion.Connections/docs/OVERVIEW.md)
- [Assimalign.Cohesion.Connections/docs/DESIGN.md](Assimalign.Cohesion.Connections/docs/DESIGN.md) —
  includes the consolidated design rationale (why the old transport abstraction dissolved, the
  layer algebra, the flattened `IConnection : IDuplexPipe` data plane, capability gating).
