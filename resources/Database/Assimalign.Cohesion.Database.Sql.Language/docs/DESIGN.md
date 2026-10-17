# Assimalign.Cohesion.Database.Sql.Language — Design

## Parameters (#1102)

SQL follows the [shared parameter contract](../../Assimalign.Cohesion.Database.Language/docs/DESIGN.md#parameter-contract-1102).
The parser retains sigils in SqlParameterExpression. It links Execution's stateless
name check, with no Execution assembly dependency. Numeric names use ASCII digits only;
Unicode numeric spellings such as `$١` and `$1١` report SQL0003 at the whole reference.
Malformed names report SQL0003; operand `?` and `:name` use the shared supported-form message.
Trailing `?` keeps the lexical generic error. Adjacent host names in operand position bypass the
clause/capability scan even when they spell keywords such as `:union`, `:like` and `:select`; syntax parsing
reports the whole reference once. DIALECT.md and ParameterConformanceTests measure these forms.


## Fixed-length character declarations (#1437)

CHAR and CHARACTER resolve to DatabaseType.Character, distinct from VARCHAR/TEXT (String).
The parser supplies length one for bare CHAR, and explicit lengths reuse the existing shared
SqlTypeModifiers rule in every declaration and CAST. SQL0005 and the common length ceiling remain
unchanged. The runtime owns padding, trimming casts and comparison; DIALECT.md specifies their
PostgreSQL anchors and the dialect differences. Schema and SDK normalization share this identity
in the v6 canonical document.

The SQL front-end (area architecture: [resources/Database/DESIGN.md](../../../../docs/resources/Database/DESIGN.md)
§3.3). The dialect is deliberately fundamental — basic SQL, declared precisely —
and structured to be extended without churning what exists (open/closed).

## Executable functions and vocabulary (#1103)

The profile's Functions list contains only executable built-ins, including the CAST special
form. Non-executing recognized functions live in SqlUnsupportedVocabulary, with call,
niladic datetime and window positions. Each call reports one named COHDBL001; the five ISO
niladic datetime forms also reject without parentheses and cannot bind unquoted columns.
The engine's single function-coverage guard requires a passing live case for every declaration.

`SqlLanguageProfile.RecognizedUnsupportedVocabulary` exposes a read-only snapshot of spellings
for identifier rendering and unknown-function classification. The internal position/construct
table stays internal. There is no shipped-to-shipped InternalsVisibleTo grant to reuse, so this
is the issue's approved public-profile option; no visibility grant or public internal type is added.

## DML qualifiers (#1106)

The UPDATE and DELETE dialect rows specify the same target qualifier rule as SELECT:
an alias hides the base relation name, and unknown qualifiers fail with located COHSQLE015
before any mutation. The engine supplies relation bindings; parser shapes and the existing
COHDBL001 refusal of DML subqueries stay unchanged. PostgreSQL `parse_relation.c:3759`
is the reference for the hidden-name rule.

## System relation joins (#1106)

The ordinary two-relation INNER JOIN grammar accepts stored tables and registered
INFORMATION_SCHEMA/COHESION_SCHEMA sources in either position. No clause is added;
all existing join-shape and read-only diagnostics remain. Engine binding owns source
resolution and one lazy statement catalog capture, including child plans. DIALECT.md
and the live JOIN conformance case cover system-to-system joins.

## EXPLAIN (#1104)

`SqlExplainExpression` wraps a SELECT, INSERT, UPDATE or DELETE tree, retains the source text,
and reports `SqlQueryCommandType.Explain`. The parser handles the prefix once, then uses the
ordinary statement parser and terminator check. Capability scanning classifies the wrapped
statement, so supported subqueries remain supported and excluded UPDATE/DELETE subqueries stay
excluded. DDL, transactions, nested EXPLAIN and ANALYZE receive `COHDBL001`. The wrapped
statement's nesting measure remains subject to the engine's existing limit. SqlClauses.Explain,
the statement corpus and the live conformance case change together; DIALECT.md is the contract.
The engine's deferred-provisioning admission walks the wrapped statement before binding, just
as it does for an ordinary data statement; an EXPLAIN cannot bypass a pending table reservation.

## Design intent

Decision 49a adds `SqlAlterColumnAction`, a full column-definition action:
`ALTER TABLE t ALTER COLUMN c INT NOT NULL`. It reuses column parsing, including bounds,
DEFAULT and collation, and the executor checks which changes preserve storage representation.
The PostgreSQL `TYPE ... USING`, `SET` and `DROP` variants remain explicitly refused. Lookahead
copies the token lexer without changing parser bookkeeping. The action changes no clause count.

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
- **Typed literal values are normalized by the parser (#1110).** SqlLiteralText is the
  public, strict invariant grammar in Sql.Language, alongside SqlTypeNames. It uses
  spans and BCL exact readers, with no Regex or reflection. Six literal kinds carry
  canonical text: dates and times, wall-clock timestamps, offset timestamps, day-time
  intervals and uppercase binary hex. N strings use String. The existing multi-word
  type grammar resolves temporal prefixes; X/N require token adjacency. Unsupported
  valid forms (including year-month intervals) report COHDBL001, invalid contents
  SQL0008, both at the complete literal. Canonical rendering retains typed prefixes
  so persisted-expression equivalence checks kind and value without losing type.
  SqlTemporalText keeps the established plain-string input acceptance and delegates
  its normalized value to this grammar; typed input reaches the strict readers directly.
  PostgreSQL gram.y:17806-17828 supplies the typed-string/interval split; datetime.c:3959-3977
  distinguishes date M from time M in ISO durations. X deliberately uses ISO binary semantics.
  PostgreSQL clock intervals accept explicit or implicit days and independently signed clock
  fields (datetime.c:3504-3596), with hh:mm, hh:mm:ss.fraction and mm:ss.fraction interpreted
  by DecodeTimeCommon (datetime.c:2656-2740). Hours are unbounded within TimeSpan, while
  seven fractional digits retain the dialect's 100 ns precision. SQL years-months
  (datetime.c:3669-3695) and ISO alternative dates (datetime.c:4038-4108) are recognized and
  refused as COHDBL001 rather than malformed SQL0008. Day-time ISO P1DT2H remains executable.
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
  their own copies. The mapping and modifier ranges live in the stateless internal shared source
  `SqlTypeModifiers`; `SqlTypeNames.TryResolve` refuses invalid modifiers and defaults an
  explicit decimal precision to scale zero. SQL, Sql.Schema and Sdk.Database compile the same
  source through `CohesionSharedSource`, with no new public seam or assembly-access grant.
  A name outside the table is not this package's error (phase E3):
  an engine's type catalog may register it as a domain, so a CAST target that is not
  a built-in name parses with no resolved type and no diagnostic when it has no
  arguments (domains take none; arguments produce `SQL0005`). DDL retains normalized type text
  and a built-in's resolved `DatabaseTypeInfo` in `SqlColumnDefinition.ResolvedType`;
  the engine resolves remaining domain names and reports a name it does not know. Which conversions a
  CAST may make is the engine's cast registry's question too, so every built-in type
  is a valid CAST target here.
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

## ISO names and lexical aliases (#1108)

All three national names accept optional VARYING: NATIONAL CHAR VARYING, NATIONAL CHARACTER
VARYING and NCHAR VARYING resolve to unpadded String, like VARCHAR. Without VARYING they
resolve to padded Character, like CHAR; bare padded names default to length one, while
bare varying names are unbounded. This follows PostgreSQL src/backend/parser/gram.y:15130-15140.

CAST delegates its target grammar and validation to ParseDefinitionType, shared by column,
domain and function declarations. The grammar consumes only defined contextual continuations
(PRECISION, VARYING and time-zone suffixes), without enlarging the reserved list. The stateless
SqlTypeModifiers mapping supplies ISO names and D1 SQL Server lexical aliases to SQL, typed
schemas and SDK extraction. No reflection, Regex, new public type, interface or grant is needed.

MAX means unbounded only on VARCHAR, NVARCHAR and VARBINARY. NCHAR, NATIONAL CHAR and NATIONAL CHARACTER
retain #1437's padded Character identity. Every temporal precision, including 7, and INTERVAL
field qualifiers retain #1438's COHDBL001, because their semantics are not enforced. SQL0004
stays retired for domains. BIT, SMALLDATETIME and TIME WITH TIME ZONE have SQL0005 reasons
and replacements; arrays use COHDBL001. The planner consumes resolved column types directly.
The preflight scan skips complete temporal WITH TIME ZONE suffixes so they cannot become CTE
diagnostics. It walks the whole parenthesized modifier list, including malformed arguments and
nested parentheses, without validating it. Shared declaration/CAST parsing alone reports SQL0005
for malformed lists; a well-formed temporal precision keeps COHDBL001. Canonical CAST rendering retains spaces between type-name words; parser/renderer
fixed points and persisted SQL function bodies cover these targets through reopen and the wire.

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
`SqlQueryCommandType`. `START TRANSACTION` and `BEGIN WORK` map to Begin, `END`
and `COMMIT WORK` map to Commit, and `ROLLBACK WORK` maps to Rollback (#1125).
`WORK` is the ISO synonym of the optional `TRANSACTION` noise word after
`BEGIN`, `COMMIT`, and `ROLLBACK`; START requires TRANSACTION and END takes no
suffix. These are spellings of existing clauses, so the profile remains 33/49
(+0): no `SqlClauses` constant or profile entry is added. START and WORK match
contextually with ordinal, case-insensitive comparisons, keeping unquoted columns
named start/work bindable and keeping the schema renderer from quoting them.
END was already a keyword; its statement dispatch leaves CASE ... END unchanged.
The executor joins subsequent statements to the session's active scope, and
disconnect aborts that transaction. The language adds no concurrency machinery.
Malformed suffixes produce `SQL0003` instead of silently treating `ROLLBACK TO`
as a complete rollback. END without a transaction retains the canonical
`COHSQLT002` message, "COMMIT requires an open transaction."

The uniform `SQL0100` policy applies to transaction statements too: omitting `;`
produces an informational diagnostic, with no error or warning for a valid spelling.
A valid spelling terminated with `;` produces no diagnostics. The transaction corpus
asserts both forms rather than exempting transaction control from the common policy.

The PostgreSQL grammar maps START TRANSACTION to its start node and END to its
commit node, with WORK/TRANSACTION sharing `opt_transaction`
(`src/backend/parser/gram.y:11358-11365`, `11471-11485`, local reference clone).
The family deliberately stops short of that broader grammar: END WORK and
END TRANSACTION, transaction modes, AND [NO] CHAIN, ROLLBACK [WORK] TO
[SAVEPOINT], BEGIN TRAN and named transactions, and COMMIT PREPARED remain
`SQL0003`. ABORT [WORK|TRANSACTION], SAVEPOINT, RELEASE, SET TRANSACTION, and
PREPARE TRANSACTION remain `SQL0002`. Recognition or recoding belongs to #1012.

B7 anticipates **`SAVEPOINT <name>`**, **`ROLLBACK TO [SAVEPOINT] <name>`**,
**`RELEASE [SAVEPOINT] <name>`**, and **`SET TRANSACTION ISOLATION LEVEL <level>`**.
Those forms remain outside this family, reserved for #1012. The session owns
exactly one root transaction through `DatabaseSession`, plus a defaulted value of
the existing transaction coordinator's `IsolationLevel`. Savepoints can add scope
markers within that transaction, and isolation syntax can set the carried value.

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

`SqlDefaultExpression` is a parser-owned, scopeless leaf for a whole INSERT VALUES
item or UPDATE SET value (#1193); canonical rendering emits `DEFAULT`. It never enters
the scalar precedence parser as an operand. INSERT/UPDATE share a contextual value
parser which recognizes the whole-item keyword before scalar parsing. The scalar parser
rejects bare DEFAULT everywhere with SQL0003; qualified field names such as `t.default`
and `t."default"` use the normal column-reference parser. Quoted identifiers, comments
and strings retain their lexical identity; no token rescan is needed.
`SqlInsertExpression.IsDefaultValues` distinguishes one all-omitted row from a missing
source, an empty VALUES list or INSERT SELECT. A column list is allowed on that form
and remains available for target-name validation. No clause or profile entry is added.
The planner maps these forms to target-column assignment instructions; catalog default
binding and execution remain in Database.Sql, with no cross-assembly visibility grant.

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
- **A chain costs its own terms, however it is grouped.** A chain that absorbs the
  parenthesized chain opening it takes over that chain's operand list
  (`SqlLogicalExpression.DetachOperands`) and appends to it, and it tracks its
  deepest operand as it adds each term, so building the node never rescans the list
  (the internal constructor takes the operand depth). The absorbed node is dropped.
  The first n-ary pass copied the list at every merge and recomputed the depth over
  it, so `((X AND t) AND t) ... AND t` cost the length of `X` once per pair of
  parentheses: a 1.45 MB statement under the 4096 ceiling parsed in 17 s and
  allocated 9.6 GB, against 0.5 s and 81 MB for the same chain written flat, and any
  wire client could send it. `SqlExpressionDepthTests` pins the parse of a
  20,000-term chain under 4,095 merges to the allocation of the flat chain.
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
  again. The parser stamps only the root, so a query it did not return as a root, a
  subquery a caller takes out of a parsed statement and wraps in a statement of its
  own, falls back to the depth of its own tree (`SqlQueryExpression.NestingDepth` is
  the greater of the stamp and `SqlSelectExpression.ExpressionDepth`; `SELECT` is the
  only query kind a statement nests). Both are cached, so the read stays
  constant-time, and the fallback is what the engine's walks recurse through: such a
  query is never parsed again, so its parentheses no longer matter.
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

Column DEFAULT parses through the unary rung too, so negative numeric defaults retain the same
unary-minus node as a scalar expression. The engine's DEFAULT admission remains narrower than
the expression grammar: one literal, or one minus over a numeric literal, plus E4's stored
scalar-call defaults. General expression-default admission remains #1121's scope. This supports
the schema builder's invariant rendering of negative C# numeric values without rewriting them
as text literals; DEFAULT assignment (#1193) reuses whatever the catalog already stores.

## AOT posture

Hand-written parser over the zero-allocation `TokenLexer` ref struct; no
reflection, no grammar codegen.

## E4 grammar

SqlQueryParser.Definitions parses single-expression CREATE FUNCTION, DROP FUNCTION with its
parameter-type signature, and CREATE/DROP DOMAIN. Immutable AST nodes carry bodies, domain
checks/defaults and declaration attributes. Syntax belongs here; namespace, volatility, recursion,
ownership and binding belong to the engine. DIALECT.md defines the supported clauses and errors.
Function parameter and return type text retains identifier delimiters until the engine resolves
it, so a quoted type name's punctuation cannot be mistaken for type arguments.

## Declaration modifiers (#1438)

CAST and ParseDefinitionType apply SqlTypeModifiers before producing an executable statement.
The latter serves columns (CREATE, ADD and ALTER), domain bases, and function parameters and
results, with an object-specific diagnostic subject. SQL0005 now covers malformed and invalid
modifiers on every type spelling, including a domain given arguments. SQL0003 remains for
surrounding syntax errors. SqlTypeNames.TryResolve returns false for an unknown name or invalid
modifiers; name-only resolution still distinguishes known built-ins. The shared splitter preserves
quoted identifiers and reads at most two unsigned Int32 arguments; typed schemas admit
identifiers and the closed built-in multi-word names, and recognize interval qualifiers for the unsupported-modifier
diagnostic. DIALECT.md records the exact ranges and unbounded defaults.

`NormalizeScale` is the shared decimal default: an explicit precision with no scale means zero;
a bare decimal stays unconstrained. Compiled-schema snapshots, their validation, the typed
builders, SDK extraction and engine provisioning use this same rule as SQL and CAST resolution.

FLOAT(p) admission and resolution follow PostgreSQL's `gram.y:14991-15016`: 1..24 bits selects
Float32, 25..53 selects Float64, and bare FLOAT is Float64. The resolved type carries no length,
decimal precision or scale. Out-of-range FLOAT precision remains SQL0005. Temporal precision
(TIME, TIMESTAMP/DATETIME, TIMESTAMPTZ and INTERVAL) is valid PostgreSQL syntax whose rounding
is not implemented here: the shared rule returns COHDBL001 with the modifier, and parser callers
attach the declaration or CAST target. The rule distinguishes these unsupported modifiers from
malformed argument lists, which remain SQL0005. A single unsigned precision above six is also
recognized, because PostgreSQL clamps it with a warning; DIALECT.md records the difference.

The declaration parser and CAST share interval-field consumption before argument parsing.
SqlTypeModifiers owns the six fields and seven accepted X TO Y ranges, following PostgreSQL
`src/backend/parser/gram.y:15197-15267`. Only SECOND (alone or at a range's end) takes precision.
Every valid qualifier reports one COHDBL001 naming the declaration and complete modifier, including
forms without precision. Invalid ranges remain SQL0003; malformed arguments remain SQL0005 and
suppress a secondary unsupported diagnostic. Case and token trivia do not change admission.
Delimited names retain their lexical identity so a domain named `"INTERVAL DAY"` is still a domain.

## Quoted function recognition (#1103 review)

The function-call branch checks quoted names against the same recognized unsupported table
as unquoted calls. Quoted names must match the lowercase lookup spelling exactly; unquoted
names match without regard to case. Parentheses distinguish calls from quoted columns.
DIALECT documents the PostgreSQL scanner and function-lookup references and examples.

Rendering preserves accepted quoted uppercase and mixed-case window-like names with delimiters.
A lowercase quoted unsupported call retains its diagnostic and NULL recovery expression; the
renderer round-trip guarantee continues to apply to error-free parser trees. Renderer tests
cover both accepted spellings and the coded recovery of rejected spellings.

Definition-binding diagnostics remain engine-owned. DIALECT.md records COHSQLD001 for invalid
CHECK predicates and absent DROP definitions, including the structured carrier and span fallback.

Wire diagnostic overflow does not change parser admission or its complete in-process
carrier. The endpoint bounds delivery with an explicit COHDBP001 prefix/omission notice
while retaining the original failure and session; DIALECT.md records this wire behavior.
