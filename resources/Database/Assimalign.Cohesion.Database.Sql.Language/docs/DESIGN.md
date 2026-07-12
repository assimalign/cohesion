# Assimalign.Cohesion.Database.Sql.Language — Design

The SQL front-end (area architecture: [resources/Database/DESIGN.md](../../DESIGN.md)
§3.3). The dialect is deliberately fundamental — basic SQL, declared precisely —
and structured to be extended without churning what exists (open/closed).

## Design intent

Parse the declared dialect into a stable, fully-typed AST that planners, catalogs,
tooling, and the SDK schema compiler can rely on. "Declared" is the operative word:
[DIALECT.md](DIALECT.md) is a contract, not aspiration — the parser, the matrix,
and the conformance corpus change together, and anything outside the matrix fails
loudly (`SQL0002`) instead of half-parsing.

## Why-this-not-that decisions

- **Recursive descent, hand-written** — over a parser generator. The dialect is
  small, error tolerance matters more than grammar elegance, the AST is the
  product, and AOT rules out runtime-generated parsers. Keyword dispatch at the
  statement level and a precedence ladder for expressions make extension points
  obvious: a new statement kind is a new branch + partial file; a new operator is
  a new rung.
- **Error-tolerant, total parsing.** Malformed input yields a statement with
  diagnostics — never an exception. Hostile inputs are part of the conformance
  corpus. This is what lets tooling (editors, the schema compiler) reuse the
  parser on incomplete text.
- **Sealed AST nodes with internal constructors.** The parser is the only
  producer; consumers pattern-match. Extending the AST is a kernel change, which
  keeps downstream planners honest (no third-party node types appearing mid-plan).
- **Positions are offsets; line/column is presentation.** Nodes carry absolute
  character offsets. Mapping offsets to line/column belongs to the tool holding
  the source text (Roslyn's model) — carrying line numbers per node would bloat
  every node for a consumer that rarely needs them. The raw statement text is
  stamped once on the root (`SqlQueryExpression.Text`) after parsing.
- **String literal nodes carry the value, not the lexeme** — quotes stripped,
  doubled quotes unescaped — because every consumer (executor, planner, schema
  compiler) wants the value, and exactly one component (the parser) knows the
  escaping rules.
- **Type names resolve through one table.** `SqlTypeNames` is the single
  SQL-name → `DatabaseType` mapping (with the `DECIMAL(p[,s])`
  argument-is-precision rule); catalogs and the schema compiler must not grow
  their own copies.
- **Recognized-but-unsupported tokens stay in the lexer tables.** `UNION`, `WITH`,
  window functions and transaction-control keywords are lexed so diagnostics can
  say "unsupported" precisely rather than mis-parsing them as identifiers.

## Namespace note

Types live in `Assimalign.Cohesion.Database.Sql.Language`, matching the assembly
name (the repo rule). Earlier scaffolding used `...Database.Language.Sql`; the
rename happened before external consumers existed.

## Non-goals (current dialect)

Set operations, CTEs, window functions, `MERGE`, `RETURNING`, transaction-control
statements (session/protocol concern), and cost-hint syntax. Each is an additive
dialect extension when its engine feature lands.

## AOT posture

Hand-written parser over the zero-allocation `TokenLexer` ref struct; no
reflection, no grammar codegen.
