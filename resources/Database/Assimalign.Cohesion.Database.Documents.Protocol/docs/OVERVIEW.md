# Assimalign.Cohesion.Database.Documents.Protocol

Shared Documents wire contracts for the remote client and engine. The package owns 14 existing public types, their codecs and the single model protocol-family identity. Consumers import `Assimalign.Cohesion.Database.Documents.Protocol` and explicitly select an engine only for embedded or server work.

See [DESIGN.md](DESIGN.md) for ownership and dependency boundaries.

Documents negotiates the shared transaction-status capability. Ready follows Execute and Command
terminal responses when agreed; pooled reset uses the existing ROLLBACK Execute and two-count
Complete payload. No new message identifiers or protocol version are needed (#1725).

## Documents diagnostic conventions (#1470)

Exception and diagnostic templates use the project's build-generated `ErrorMessages` resources.
Parameterized templates format with the current culture; abort/cancellation templates used in
normal cleanup are cached. The [Database error-code registry](../../Assimalign.Cohesion.Database/docs/ERROR-CODES.md)
assigns this family its two-letter prefix and categorized five-digit identities.
Existing SQLSTATE meanings, numeric wire error kinds, frame layouts and disk formats are preserved.
Structured diagnostics keep their code outside the message. Compiler diagnostic identifiers and
standard .NET argument guards retain their own contracts.

## Debugger inspection (#1470)

DocumentId has a debugger view over its stored public value; protocol parsing and payload handling are unchanged.

Eligible types follow the [Database conventions](../../Assimalign.Cohesion.Database/docs/CONVENTIONS.md) debugger display/proxy rules. Views are private and read-only; inspection does not change the public API, execution paths, error contracts, storage formats or protocol bytes.
