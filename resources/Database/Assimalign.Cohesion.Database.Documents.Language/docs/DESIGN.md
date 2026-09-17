# Assimalign.Cohesion.Database.Documents.Language — Design

The package owns the document model's OQL grammar, expression tree, diagnostics, and conformance
corpus. `OqlQueryParser` derives from the shared `QueryParser` and declares
`OqlLanguageProfile.Instance` as its profile. The shared `TokenLexer` provides tokenization;
there is no alternate lexer or duplicated keyword configuration.

## Family and pipeline

`Database.Documents.Language` references only `Assimalign.Cohesion.Database.Language`.
`Database.Documents` consumes its AST for logical planning, physical index selection, and
execution. This package has no catalog, storage, hosting, or ApplicationModel dependency.
Parsing runs shared lexing, capability validation, statement/expression parsing, then shared
analyzers. Malformed input remains an `OqlQueryStatement` with error diagnostics; consumers
must reject statements containing errors before planning.

The diagram shows that parsing flow; the steps and their responsibilities are stated above.

```mermaid
flowchart TD
    Src["OQL text"] --> Lex["Shared TokenLexer and OqlLanguageProfile"]
    Lex --> Cap["Supported-clause validation"]
    Cap --> Ast["OqlQueryStatement and expression tree"]
    Ast --> Ana["Shared query analyzers"]
    Ana --> Plan["Documents planner"]
```

## Supported-clause matrix

The profile's supported set is deliberately the executable subset of `OqlClauses`.
Lexical recognition preserves the established vocabulary so rejected constructs receive
`COHDBL001`; recognizing a token does not make its operation supported.

| Clause | Supported | Shape or reason |
| --- | --- | --- |
| `SELECT` | Yes | One or more projected expressions, optional `AS` output names; `*` returns the source document |
| `FROM` | Yes | Exactly one unqualified collection and optional iteration variable, with or without `AS` |
| `WHERE` | Yes | Scalar comparison and Boolean document filtering |
| `GROUP BY` | Yes | One or more grouping expressions |
| `HAVING` | Yes | Group filtering, including aggregate calls |
| `ORDER BY` | Yes | One or more expressions, each optionally `ASC` or `DESC` |
| `DEFINE` | No | Named query definitions are not planned |
| `ELEMENT` | No | Singleton extraction is not planned |
| `FLATTEN` | No | Collection expansion is not planned |
| `SUBQUERY` | No | A nested `SELECT` reports this capability name at the inner keyword |

Only `COUNT`, `SUM`, `AVG`, `MIN`, and `MAX` are callable. Each takes one expression;
`COUNT(*)` also counts documents. Aggregate placement, grouping consistency, and value semantics
are validated by the Documents planner/executor. `DISTINCT`, `ALL`, `IN`, `EXISTS`, `LIKE`,
`BETWEEN`, collection constructors/quantifiers, `ABS`, and other reserved but unimplemented
operations produce `COHDBL001`. Unknown function calls likewise produce `COHDBL001` naming the
function. There is no implied support for the full ODMG specification.

## Grammar and expression tree

```text
query      := SELECT projection (',' projection)* FROM identifier [AS? identifier]
              [WHERE expression] [GROUP BY expression (',' expression)*]
              [HAVING expression] [ORDER BY ordering (',' ordering)*] [';']
projection := expression [AS identifier]
ordering   := expression [ASC | DESC]
path       := identifier ('.' identifier | '[' integer ']' | '[' string ']')*
```

Keywords and function names are case-insensitive. Collection and property names preserve case.
Double quotes delimit identifiers; single quotes delimit strings, with a doubled single quote
representing one quote. Property names after a dot may use a reserved word; bracket strings
address arbitrary property names. Array indices are nonnegative, zero-based 32-bit integers.
Parentheses group expressions. `$name`, `$1`, and `@name` all produce parameter names without
the prefix. Empty identifiers and empty parameter names are syntax errors.

Literal nodes hold a string, Boolean, decimal, or null; `NIL` is a synonym for null. Decimal
and scientific notation use invariant culture and reject invalid/out-of-range decimal values.
Numeric query literals therefore have a bounded decimal range even when JSON can represent a
larger number. The parser does not silently round a literal to an infinity or coerce it to text.

Operator precedence, lowest first: `OR`, `AND`, comparisons and null tests, addition/subtraction,
multiplication/division/remainder, unary signs. `NOT` binds around comparisons, so
`NOT age < 18` means `NOT (age < 18)`. Comparison operators are `=`, `!=`, `<>`, `<`, `<=`, `>`,
and `>=`; the AST normalizes `<>` to `!=`. Null tests are unary `IS NULL` and `IS NOT NULL`
nodes. Arithmetic supports `+`, `-`, `*`, `/`, and `%`.

The parser is a partial class, split into dispatch/token handling, SELECT clauses, and expression
parsing, following `SqlQueryParser`. Parser-produced list properties are read-only snapshots;
AST nodes retain source spans and the top-level expression retains the original statement text.
Locations use zero-based UTF-16 offsets with exclusive ends and one-based line numbers.
Nested expressions are capped at 128 recursive parse levels; rejection is a diagnostic, not a
stack-overflow exception. Concurrent calls on one parser are serialized. The parser owns no
disposable resources beyond those used by the shared analyzer pipeline.

## Diagnostic contract

| Code | Meaning |
| --- | --- |
| `COHDBL001` | A recognized clause or operation lies outside the executable profile |
| `OQL0001` | Empty query, including whitespace/comment-only text |
| `OQL0002` | Syntax error, missing token, invalid identifier/index, or multiple statements |
| `OQL0003` | Unterminated quoted text or block comment |
| `OQL0004` | Invalid or out-of-range numeric literal |
| `OQL0005` | Expression nesting exceeds the supported depth |
| `OQL0006` | Invalid aggregate argument count or star operand |

Every error has an absolute start/end span and source line. End-of-input errors use the source
length for both offsets. Capability validation precedes statement parsing so an unsupported
construct receives the shared capability diagnostic instead of an accidental generic syntax
error. Quotes, comments, and property names after dots are not mistaken for clauses.

## Scope and mutations

A query cannot name a server or switch databases. `FROM other.collection` is invalid syntax;
quoted collection names remain a single opaque identifier. `CREATE DATABASE`, `DROP DATABASE`,
`USE`, and SQL mutation/transaction commands are unsupported. Multiple statements per parse
are rejected. Logical database creation and deletion remain engine-side C# operations.

OQL mutation syntax is outside the existing `OqlClauses` surface. Document insert/replacement and
delete use the frozen `IDocumentCollection.PutAsync` and `DeleteAsync` contracts, including their
transaction and expected-version semantics. No interface was widened to introduce a second
mutation language. The Documents engine design states query ordering, mixed-shape behavior,
and aggregate/mutation semantics.

## Verification and extension

`OqlQueryParserTests` contains valid and malformed query corpora plus structural AST, exact
diagnostic span, precedence, nested-path/array, parameter, scope, and recursion-limit checks.
Profile tests assert every supported capability and every deferred OQL clause. Existing lexer
conformance remains unchanged, including recognition of unsupported reserved vocabulary.

A new clause requires parser, planner, executor, conformance tests, and a matrix entry together;
only then may it join the profile's supported set. The package uses static code and BCL values,
with no reflection, runtime discovery, or `Microsoft.Extensions.*` dependencies, and remains
NativeAOT compatible.
