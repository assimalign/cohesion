# Assimalign.Cohesion.Database.KeyValuePair.Protocol

Shared KeyValuePair wire contracts for the remote client and engine. The package owns 6 public top-level types, their codecs and the single model protocol-family identity. Consumers import `Assimalign.Cohesion.Database.KeyValuePair.Protocol` and explicitly select an engine only for embedded or server work.

See [DESIGN.md](DESIGN.md) for ownership and dependency boundaries.

## KeyValuePair diagnostic conventions (#1470)

Exception and diagnostic templates use the project's build-generated `ErrorMessages` resources.
Parameterized templates format with the current culture; abort/cancellation templates used in
normal cleanup are cached. The [Database error-code registry](../../Assimalign.Cohesion.Database/docs/ERROR-CODES.md)
assigns this family its two-letter prefix and categorized five-digit identities.
Existing SQLSTATE meanings, numeric wire error kinds, frame layouts and disk formats are preserved.
Structured diagnostics keep their code outside the message. Compiler diagnostic identifiers and
standard .NET argument guards retain their own contracts.

## Shared scalar function identifier

`KeyValueFunctionName` is a generated readonly value object with implicit string conversion,
ordinal-ignore-case equality and ASCII identifier validation (letter/underscore first, then
letters/digits/underscores). Default is refused by engine and client function boundaries.
It adds one public top-level type (with its generated nested JSON converter) without adding frames, capabilities, protocol versions or references.
The engine owns function metadata/resolution and the client owns CALL construction; both use
this shared name grammar. No function implementation or catalog enters this contract package.
