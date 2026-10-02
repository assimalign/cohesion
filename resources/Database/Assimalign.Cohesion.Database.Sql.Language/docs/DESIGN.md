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
- **Total parsing never means silent parsing (#1068).** A recursive-descent branch
  returns at the first token it does not understand, so `ParseCore` requires the
  statement to end at the end of the text or at one terminating `;`, for every
  statement kind. Anything left over is `SQL0003` at the first unconsumed token.
  Required tokens (closing `)`, `THEN`, `END`, `BY`, `SET`, `=`, names) are
  expected, not skipped when absent. Error recovery may still substitute a
  placeholder node, such as the NULL literal for a missing expression or the `?`
  table name, but only together with an error diagnostic, so a placeholder never
  executes. Recovery leaves the offending token in place for the caller, and every
  token-anchored syntax diagnostic, the leftover check included, skips a position
  an earlier error already covers, so a mistake is reported once per position.
  Before parsing, `ParseCore` scans the tokens for lexical errors the shared lexer
  passes through as ordinary tokens: an unterminated string, quoted identifier or
  block comment (each would swallow the rest of the statement), and a character
  outside the dialect. A `--` comment cannot swallow the next line: it ends at the
  first line terminator (LF, CR, NEL, LS or PS), not only at LF, because a clause
  hidden inside the comment token is invisible to the leftover check (#1150). A
  rejected `ALTER TABLE` action yields a bare `Alter` expression, not a
  placeholder action. `SqlStatementCompletenessTests` enforces
  the rule: every profile clause and statement kind needs a corpus form, and every
  form followed by leftover text must report an error after the form.
- **Sealed AST nodes with internal constructors.** The parser is the only
  producer; consumers pattern-match. Extending the AST is a kernel change, which
  keeps downstream planners honest (no third-party node types appearing mid-plan).
- **Positions are offsets; line/column is presentation.** Nodes carry absolute
  character offsets. Mapping offsets to line/column belongs to the tool holding
  the source text (Roslyn's model) — carrying line numbers per node would bloat
  every node for a consumer that rarely needs them. Such a tool breaks lines by
  the rule `TokenLexer.CountLineBreaks` applies (every `IsLineTerminator`
  character, CR LF once), so its lines break where the lexer ends a `--`
  comment and agree with OQL and GQL diagnostic lines. The raw statement text
  is stamped once on the root (`SqlQueryExpression.Text`) after parsing.
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
table list once. `CHECK` retains both its expression tree and the predicate source
as written (`CheckExpressionText`, for tooling); the engine never persists that
source. It persists the canonical text `SqlExpressionRenderer` renders from the
tree (below), so no spelling — and no leniency an older parser had for a spelling —
reaches storage. New constraint syntax follows the DDL parser's diagnostic recovery
discipline and emits `SQL0003` on malformed input.

**`UNIQUE` is a unique index, not a new compiled-schema constraint kind.** The
syntax still has a `Unique` AST kind so it can preserve the declaration and name,
but execution lowers it to the same unique catalog index used by `CREATE UNIQUE
INDEX` and `CompiledSchemaIndex(IsUnique: true)`. This keeps compiled schemas,
SQL DDL, persistence, and concurrent uniqueness enforcement on one path.
Foreign keys and checks remain constraint catalog records. Only `ON DELETE
CASCADE` and `ON DELETE RESTRICT` are accepted; `ON UPDATE` stays outside the
profile and reports `COHDBL001`.

## Canonical rendering (`SqlExpressionRenderer`)

`SqlExpressionRenderer.Render(SqlExpression)` and `Render(SqlSelectExpression)` turn
a parsed tree back into SQL text. It is the one AST-to-SQL renderer: every
definition the engine persists — CHECK predicates and literal DEFAULTs today,
expression defaults (#1121) and view queries (#1124) next — is stored as its
output (the rule is in [DIALECT.md](DIALECT.md#persisted-definitions-are-canonical)).

- **The text is a function of the tree.** Keywords upper case, single spaces,
  `<>` for inequality, comments dropped, names and literal values as the tree holds
  them. Two spellings of one predicate store the same text, which is what lets the
  engine compare a compiled schema's CHECK with the catalog by canonical form.
- **Parentheses follow the parser's precedence ladder, not the source.** The
  renderer assigns each node the rung `SqlQueryParser.Expressions.cs` parses it at
  and parenthesizes an operand only when its position parses a tighter rung, with
  left associativity for logical and arithmetic operators and none for comparisons.
  Four spellings are special because the lexer or parser folds them: a sign
  applied to a sign is parenthesized (`--` would start a comment), `+(1)` keeps its
  parentheses (`+1` is the literal `1`), `NOT` before a plain `EXISTS` is
  parenthesized (`NOT EXISTS` is one construct), and a call's first argument whose
  text starts with `*` but is not the star itself is parenthesized (`f((* = 1))`,
  because `f(*` is read as the `COUNT(*)` star argument).
- **Identifiers are delimited only when needed**, mirroring the lexer's identifier
  scan: a keyword, builtin function name, unsupported-vocabulary or positional word,
  or a name the lexer would not read as one word is quoted; a name containing `"`
  cannot be delimited and is refused with `NotSupportedException`, as is a node type
  the parser does not produce. A called function name is bare when it is a builtin
  or a plain word, except `CAST` and the nine window function names (`ROW_NUMBER`,
  `RANK`, ...): a bare call to one of those parses as the window function, which the
  dialect rejects, so an error-free tree that calls one named a delimited user
  function and keeps the quotes (`"rank"(x)`).
- **The invariant is tested, not assumed.** `SqlExpressionRendererTests` pins the
  canonical spelling of every form a CHECK accepts, proves that parsing the output
  yields the same tree (positions aside; a CAST target's whitespace is normalized)
  and that the output is a fixed point, and runs thousands of randomized
  expressions over the whole grammar through the same checks. A parser change that
  would read stored canonical text differently fails that suite. The engine also
  re-parses each canonical text when it is first rendered and refuses to store a
  definition that does not reproduce its tree.
- **An `AND`/`OR` chain renders term by term** (#1151). Every term is rendered at
  the rung above the chain's, so a nested chain of the same operator keeps its
  parentheses. The parser merges a parenthesized chain only in first position, so
  no parsed chain holds one there, and the text is exactly what the nested binary
  form rendered: `a AND b AND c`, `a AND (b AND c)`, `(a OR b) AND c`. Definitions
  stored before chains became n-ary therefore read back to the same chain and keep
  their canonical text.
- **A tree deeper than any parser reads has no canonical text** (#1151): `Render`
  refuses a tree deeper than `SqlQueryParserOptions.MaximumExpressionNestingLimit`
  (4096) with `NotSupportedException` before walking it, because no parser would
  read the text back. That check is constant-time, and it keeps the walk's own
  recursion within the ceiling; the walk also checks the stack at every node, so a
  thread with little stack left gets `InsufficientExecutionStackException`, never an
  overflow. The text of a tree within the ceiling parses under every limit at least
  as deep as the tree, the limit of the parser that built it included.

## Expression nesting limit (#1151)

Recursive descent recurses once per level of the text, and every consumer walks
the tree it builds recursively too. Nothing bounded either, so `1 + 1 + ...` with
200,000 terms, or 200,000 nested parentheses, overflowed the stack. .NET cannot
catch a `StackOverflowException`: the parse, or the planner or evaluator after it,
ended the process, and on the server one statement ended every session. The first
pass bounded every tree at 128 levels, counting each link of an `AND`/`OR` chain.
The owner's follow-up decision (2026-10-01) is a middle course between SQL Server
and PostgreSQL: genuine nesting counts against one documented, configurable limit
(256 by default, 32..4096), a flat `AND`/`OR` chain counts once, and the stack
checks stay as PostgreSQL's backstop. The parser reports nesting past the limit as
`SQL0006`; the user-facing rule, with the comparison table, is in
[DIALECT.md](DIALECT.md#expression-nesting-limit-1151).

- **`AND` and `OR` chains are n-ary, PostgreSQL's shape.** `SqlLogicalExpression`
  holds one operator and the chain's terms in source order; `ParseLogicalChain`
  builds one for each run of the operator at its rung, so a chain of any length is
  one level of the tree and a 10,000-term predicate is three levels deep. The parser
  no longer builds a `SqlBinaryExpression` with `And` or `Or`; the enum members stay
  for compatibility. A parenthesized chain of the same operator that opens a chain is
  merged into it, which is exactly what left associativity made of it before (both
  `(a AND b) AND c` and `a AND b AND c` were one left-leaning tree); one in a later
  position stays a nested node, as `a AND (b AND c)` stayed a right-nested tree. The
  n-ary tree is therefore the binary tree with each run of links collapsed, and the
  canonical text of every stored definition is unchanged. Flattening across later
  positions too, as PostgreSQL's planner does, would make `a AND (b AND c)` and its
  canonical text change shape, and would let the top-down depth count, which charges
  the nested chain's node while its terms parse, disagree with the final tree.
- **Why only `AND` and `OR`.** They are associative under three-valued logic, and
  left-to-right short-circuit visits the terms of `(a AND b) AND c` and of
  `a AND b AND c` in the same order and stops at the same term, so one node changes
  neither the result nor which faults are raised (the #1069 contract). Arithmetic is
  not: `+` over BIGINT can overflow at a different point in another grouping, and
  `-`, `/` and `%` do not associate, so every other chain keeps a node per link and
  counts per link, as SQL Server and the first pass both do.
- **The limit is configured, not compiled in.** `SqlQueryParserOptions`, a
  `QueryParserOptions` subclass, carries `ExpressionNestingLimit` (default 256) and
  the range constants; the parser reads and validates it once, in its constructor,
  and keeps it in `_nestingLimit`. Any other options type gives the default, so
  existing callers keep a valid parser. The SQL engine passes its own configured
  value (`SqlDatabaseEngineOptions.ExpressionNestingLimit`).
- **Depth is a property of the tree.** Every `SqlExpression` carries an internal
  `Depth`, the node count of its longest path to a leaf, computed by its internal
  constructor from its children (and from `SqlSelectExpression.ExpressionDepth` for
  a subquery), so reading it is constant-time for a tree of any size. Measuring the
  tree, not the recursion, is what makes an arithmetic chain count: the parser
  builds `1 + 1 + ...` in a loop, but the result is as deep as it is long, and every
  later walker recurses through it.
- **Three checks make the bound exact.** Before the parser recurses into an operand
  that the node it is building will enclose (`ParseOperand`, and `ParseStarArgument`
  for the `*` of `COUNT(*)`), it counts that node in `_expressionDepth` and rejects
  the statement if the node and a leaf under it would already exceed the limit.
  Whenever it builds a node over an operand parsed before the node existed (each
  link of an arithmetic chain, a comparison, `IS NULL`, `BETWEEN`, `IN`, `LIKE`,
  `COLLATE`, an `AND`/`OR` chain), `Nest` adds the node's `Depth` to the enclosing
  count. An `AND`/`OR` chain is also checked at its first operator (`TryOpenChain`),
  against its first operand or the terms of the group it absorbs, so a chain that is
  already too deep is rejected before its remaining terms are parsed. Every other
  node is bounded by the checks its operands passed on the way down, so a statement
  is rejected exactly when its tree is deeper than the limit, counting subqueries,
  whose clauses parse one level below the subquery node. The first pass added the
  star of `COUNT(*)` without a check, so a call at the deepest level built a tree one
  level deeper than the limit.
- **The statement records how deep it nests.** Each check that passes also records
  the depth it saw (`Deepen`: every primary at its level, every `Nest`ed node, every
  parenthesis), and `ParseCore` stamps the greatest on the root, which
  `SqlQueryStatement.ExpressionNestingDepth` exposes. Because the checks are exact,
  a parser with any limit at least that large accepts the statement and one with a
  smaller limit rejects it, so an engine with a lower limit than the parser that
  built a typed request refuses it on that number alone, without parsing the text
  again.
- **Parentheses are bounded apart from the tree.** They are not nodes, so they do
  not change `Depth`, but each pair is a level of recursion, so `_parenthesisDepth`
  caps them at the limit too. Folding them into the tree count would break persisted
  definitions: the renderer parenthesizes a sign applied to a sign (`- -1` is stored
  as `-(-1)`), so stored text can carry more parentheses than the declaration did.
  It adds at most one pair per node, so the stored text nests its parentheses no
  deeper than its tree, which is the declared tree, and it always parses back.
- **Recursion is bounded by construction, not by the limit's size.** Every cycle
  through the expression rules passes `ParseOperand` (or `ParseSubquery`, which
  enters the subquery node the same way) or a parenthesis, so the parser recurses
  at most a limit's worth of node levels plus a limit's worth of parenthesis levels.
  Each entry also calls `RuntimeHelpers.TryEnsureSufficientExecutionStack`. A
  parenthesis or call level runs the whole precedence ladder, measured at about
  0.7 to 1.9 KB of stack in a release build, depending on how far the JIT has
  optimized the parser, and about 5 to 6.5 KB in a debug build. In a release build
  the deepest text the default limit accepts, 256 parentheses around 255 nested
  calls, parses on a 1 MB thread; in a debug build it needs about 3 MB, so the tests
  parse at the limit on an 8 MB thread. A higher configured limit admits text no
  default thread can recurse through. A thread with too little left reports
  `SQL0007` rather than overflowing. It is a separate code from the limit's `SQL0006`
  because the text is within the dialect and parses on a bigger stack; the engine
  relies on the difference to tell a thread too small for a stored definition apart
  from a damaged catalog, and reports it to clients as `COHSQLE004`, statement too
  complex, like any other walk out of stack. Collapsing the binary rungs into one
  precedence-climbing loop would cut the cost per level, should smaller threads ever
  need to accept the deepest text the default limit admits in a debug build.
- **The parser's own tree walk iterates.** The correlation check over a subquery's
  clauses (`ValidateSubqueryReference`) runs after the clause is built, over a tree
  the parser may have built in a loop thousands of levels deep, so it uses an explicit
  stack and reports references in source order without recursing.
- **Crossing the limit abandons the statement.** The parser reports `SQL0006` (or
  `SQL0007`) once, consumes the rest of the text, and every rule still on the stack
  meets the end and returns. What those rules report on the way out describes the
  abandoned text, not the statement, so `ParseCore` drops every diagnostic after it and keeps
  only the statement's command type, as it does for a character outside the dialect.
  The scans that run over the whole text before parsing (lexical errors and the
  unsupported-clause preflight) are unaffected and keep their diagnostics.
  Recovering past the deep operand instead would mean parsing it, or skipping it by
  matching parentheses and `CASE ... END` without parsing; abandoning keeps the
  error single and the partial tree out of every consumer's hands.

## Unary plus

ISO unary plus is `SqlUnaryOperator.Plus`, parsed on the unary rung beside `-`.
Directly before a numeric literal the sign stays part of the literal, so `+1` and
`1` are the same tree and `ORDER BY +1` keeps its ordinal meaning; `+a`, `+(1 + 2)`
and `+@p` build a unary-plus node. Typing and evaluation belong to the engine.

## AOT posture

Hand-written parser over the zero-allocation `TokenLexer` ref struct; no
reflection, no grammar codegen.
