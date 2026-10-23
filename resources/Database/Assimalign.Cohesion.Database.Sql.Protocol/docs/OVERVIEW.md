# Assimalign.Cohesion.Database.Sql.Protocol

Shared Sql wire contracts for the remote client and engine. The package owns 5 existing public types, their codecs and the single model protocol-family identity. Consumers import `Assimalign.Cohesion.Database.Sql.Protocol` and explicitly select an engine only for embedded or server work.

See [DESIGN.md](DESIGN.md) for ownership and dependency boundaries.

SQL and Graph negotiate transaction-status: each Execute ends with a Ready I/T/E status, enabling rollback before pool reuse. Existing identifiers and protocol 2.1 are retained (#1540).
