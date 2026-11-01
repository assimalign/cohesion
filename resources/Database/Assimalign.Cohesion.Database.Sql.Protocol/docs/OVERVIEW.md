# Assimalign.Cohesion.Database.Sql.Protocol

Shared Sql wire contracts for the remote client and engine. The package owns 5 existing public types, their codecs and the single model protocol-family identity. Consumers import `Assimalign.Cohesion.Database.Sql.Protocol` and explicitly select an engine only for embedded or server work.

See [DESIGN.md](DESIGN.md) for ownership and dependency boundaries.

SQL and Graph negotiate transaction-status: each Execute ends with a two-byte Ready I/T/E plus retained-session-state status, enabling rollback before pool reuse. Existing identifiers and protocol 2.1 are retained (#1540).

## Sql diagnostic conventions (#1470)

Exception and diagnostic templates use the project's build-generated `ErrorMessages` resources.
Parameterized templates format with the current culture; abort/cancellation templates used in
normal cleanup are cached. The [Database error-code registry](../../Assimalign.Cohesion.Database/docs/ERROR-CODES.md)
assigns this family its two-letter prefix and categorized five-digit identities.
Existing SQLSTATE meanings, numeric wire error kinds, frame layouts and disk formats are preserved.
Structured diagnostics keep their code outside the message. Compiler diagnostic identifiers and
standard .NET argument guards retain their own contracts.

## Rules audit after main cleanup

The model protocol extension container is partial. Its public Family and message extensions,
identifiers, capabilities and encoded payloads are unchanged.

## Debugger inspection

Concrete source types with explicit nonpublic instance state use a private nested DebugView
and debugger display/proxy attributes. The view is the last member in the same type file and
exposes only reviewed public/protected state through read-only properties. Exception and
EventSource types remain excluded. A type with no state to filter keeps its normal member view;
a type whose base supplies both debugger attributes inherits that view.

Inspection does not perform I/O, take lifecycle locks, mutate state, invoke application callbacks,
or materialize lazy results. The additions are debugger metadata and nested helper types; existing
instance fields, runtime operations, error identities, data formats and protocol contracts do
not change. SDK and Studio source are outside this rule's scope.

Protocol data carriers whose only storage is public/protected data or compiler-generated
auto-property backing keep the normal debugger member view. No new protocol projection or wire
member is introduced.
