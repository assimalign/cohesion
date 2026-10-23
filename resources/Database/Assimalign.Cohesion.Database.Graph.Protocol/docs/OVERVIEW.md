# Assimalign.Cohesion.Database.Graph.Protocol

Shared Graph wire contracts for the remote client and engine. The package owns 14 public types, their codecs and the single model protocol-family identity. Consumers import `Assimalign.Cohesion.Database.Graph.Protocol` and explicitly select an engine only for embedded or server work.

See [DESIGN.md](DESIGN.md) for ownership and dependency boundaries.
#1533 adds GraphQueryLimits (optional lower MemoryBytes/WorkUnits) for embedded query requests.
The engine defaults are 64 MiB retained memory and 1,000,000 physical work units. Internal read
bindings stream and an unordered LIMIT can stop upstream work; final results remain materialized.
This changes no grammar or wire bytes; remote execution uses the server engine ceilings.
