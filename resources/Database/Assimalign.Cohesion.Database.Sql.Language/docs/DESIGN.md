# Assimalign.Cohesion.Database.Sql.Language — Design

The SQL front-end (area architecture: [resources/Database/DESIGN.md](../../../../docs/resources/Database/DESIGN.md)
§3.3). The dialect is deliberately fundamental — basic SQL, declared precisely —
and structured to be extended without churning what exists (open/closed).

## Design intent

Parse the declared dialect into a stable, fully-typed AST that planners, catalogs,
tooling, and the SDK schema compiler can rely on. "Declared" is the operative word:
[DIALECT.md](DIALECT.md) is a contract, not aspiration — the parser, the matrix,
and the conformance corpus change together, and anything outside the matrix fails
loudly instead of half-parsing. `SqlLanguageProfile.Instance` is the executable
contract: it owns the SQL keyword/function tables and opts into only the clauses
the parser implements today. Recognized clauses outside that set produce the shared
`COHDBL001` model-specific diagnostic; `SQL0002` remains the unknown-command diagnostic.

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
- **Delimited identifier nodes carry the identifier value.** Name consumption
  strips the surrounding double quotes for tables, schemas, aliases, columns,
  indexes and constraints. Tokens retain their quoted-identifier classification,
  so a quoted reserved word cannot become a keyword or literal. Keyword dispatch,
  literal parsing and diagnostics continue to consume raw token text. Embedded
  double-quote escapes are outside the current lexer subset.
- **Type names resolve through one table.** `SqlTypeNames` is the single
  SQL-name → `DatabaseType` mapping (with the `DECIMAL(p[,s])`
  argument-is-precision rule); catalogs and the schema compiler must not grow
  their own copies.
- **Recognized-but-unsupported tokens stay in the lexer tables.** `UNION`, `WITH`,
  and window functions are lexed so diagnostics can
  say "unsupported" precisely rather than mis-parsing them as identifiers. Gateable
  names live in `SqlClauses`; supported names are present in `SqlLanguageProfile`,
  while reserved future clauses such as set operations, CTEs, windows, `FETCH`,
  `RETURNING`, `TOP`, unsupported join forms, and views remain absent. `ParseCore` scans a copy of its configured
  lexer before recursive descent and attaches the first rejected clause to the
  statement, so analyzers observe `COHDBL001` with the token location and the `SQL`
  surface name. An actually unknown leading command still receives `SQL0002`, even
  when a later recognized token is also unsupported.

## Namespace note

Types live in `Assimalign.Cohesion.Database.Sql.Language`, matching the assembly
name (the repo rule). Earlier scaffolding used `...Database.Language.Sql`; the
rename happened before external consumers existed.

## Non-goals (current dialect)

Set operations, CTEs, window functions, views, `ON UPDATE`,
`MERGE`, `RETURNING`, savepoints, isolation-level syntax, and cost-hint syntax.
Each is an additive dialect extension when its engine feature
lands.

## Transactions and B7 extension points

`SqlTransactionExpression` identifies `BEGIN`, `COMMIT`, and `ROLLBACK` through
`SqlQueryCommandType`. The optional `TRANSACTION` token changes no semantics.
The executor joins subsequent statements to the session's active scope, and
disconnect aborts that transaction. The language adds no concurrency machinery.
Malformed suffixes produce `SQL0003` instead of silently treating `ROLLBACK TO`
as a complete rollback.

B7 anticipates **`SAVEPOINT <name>`**, **`ROLLBACK TO [SAVEPOINT] <name>`**,
**`RELEASE [SAVEPOINT] <name>`**, and **`SET TRANSACTION ISOLATION LEVEL <level>`**.
Those forms are not implemented by B2. The session owns a stack of transaction
scopes with exactly one root entry today, plus a defaulted value of the existing
transaction coordinator's `IsolationLevel`. Savepoints can add scope markers and
isolation syntax can set that value without replacing the session representation.

## Constraint normalization and persistence

Column and table declarations produce `SqlConstraintDefinition` nodes with the
same shape: optional name, ordered key columns, foreign-key target and deletion
action, or a check predicate. `SqlCreateTableExpression.Constraints` contains all
constraints; column definitions also retain their own constraints so `ALTER TABLE
ADD COLUMN` carries the same information. The executor consumes the normalized
table list once. `CHECK` retains both its expression tree and exact predicate
source; the catalog persists source and reparses it after restart without
reflection or a second expression codec. New constraint syntax follows the DDL
parser's diagnostic recovery discipline and emits `SQL0003` on malformed input.

**`UNIQUE` is a unique index, not a new compiled-schema constraint kind.** The
syntax still has a `Unique` AST kind so it can preserve the declaration and name,
but execution lowers it to the same unique catalog index used by `CREATE UNIQUE
INDEX` and `CompiledSchemaIndex(IsUnique: true)`. This keeps compiled schemas,
SQL DDL, persistence, and concurrent uniqueness enforcement on one path.
Foreign keys and checks remain constraint catalog records. Only `ON DELETE
CASCADE` and `ON DELETE RESTRICT` are accepted; `ON UPDATE` stays outside the
profile and reports `COHDBL001`.

## AOT posture

Hand-written parser over the zero-allocation `TokenLexer` ref struct; no
reflection, no grammar codegen.
