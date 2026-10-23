# Assimalign.Cohesion.Database.Graph.Protocol

Shared Graph wire contracts for the remote client and engine. The package owns 14 public types, their codecs and the single model protocol-family identity. Consumers import `Assimalign.Cohesion.Database.Graph.Protocol` and explicitly select an engine only for embedded or server work.

See [DESIGN.md](DESIGN.md) for ownership and dependency boundaries.
#1533 adds GraphQueryLimits (optional lower MemoryBytes/WorkUnits) for embedded query requests.
The engine defaults are 64 MiB retained memory and 1,000,000 physical work units. Internal read
bindings stream and an unordered LIMIT can stop upstream work; final results remain materialized.
This changes no grammar or wire bytes; remote execution uses the server engine ceilings.

SQL and Graph negotiate transaction-status: each Execute ends with a Ready I/T/E status, enabling rollback before pool reuse. Existing identifiers and protocol 2.1 are retained (#1540).

## Graph diagnostic conventions (#1470)

Exception and diagnostic templates use the project's build-generated `ErrorMessages` resources.
Parameterized templates format with the current culture; abort/cancellation templates used in
normal cleanup are cached. The [Database error-code registry](../../Assimalign.Cohesion.Database/docs/ERROR-CODES.md)
assigns this family its two-letter prefix and categorized five-digit identities.
Existing SQLSTATE meanings, numeric wire error kinds, frame layouts and disk formats are preserved.
Structured diagnostics keep their code outside the message. Compiler diagnostic identifiers and
standard .NET argument guards retain their own contracts.
