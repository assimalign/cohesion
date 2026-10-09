# The Declared SQL Dialect

The contract for the Cohesion SQL surface that executes today. **Supported** means
the documented subset has been measured through a live engine, including correct
results, state changes, or intended semantic errors. Recognized clauses without
execution support report `COHDBL001`; unknown commands report `SQL0002`; text the
parser did not consume reports `SQL0003`, so nothing executes a truncated statement
(see [Statement completeness](#statement-completeness-1068)). Extending
the profile requires updating the parser, this matrix, and the engine's
`SqlLanguageConformanceTests` execution-case table in the same change. That test
enumerates the profile and fails if any advertised clause lacks a passing case.

## Statement matrix

Phase 22 measures **33 of 49 declared clauses** against the live SQL engine.
The earlier 32/48 figure included `JOIN`, `GROUP BY`, `HAVING`, and `SUBQUERY`,
removed in Phase 12e (#1019–#1021), plus a no-op `CAST` removed in Phase 13.
Phase 14 restores `CAST` with actual conversion, type metadata, and wire execution
coverage (#1022). Phase 15 restores `JOIN` for two stored-table inner joins with
an `ON` predicate, including server/client execution (#1019). Phase 16 restores
`GROUP BY` and `HAVING`, including aggregation over the supported inner join
and server/client execution (#1020). Phase 17 adds executable column and expression
`COLLATE`, including persisted defaults, collation-aware index seeks, grouping,
uniqueness, and server/client execution (#1025). Phase 18 restores `SUBQUERY`
for uncorrelated scalar, `IN`/`NOT IN`, and `EXISTS`/`NOT EXISTS` queries and
adds transactional `INSERT ... SELECT`, with server/client execution (#1021).
Phase 21 restores literal `ALTER TABLE ADD COLUMN` defaults for populated tables,
including old-row and omitted-column insert values over the wire, and rejects
nonliteral defaults before mutation (#1023). Phase 22 resolves `ORDER BY`
projection aliases, including aliases inside scalar expressions, and select-list
ordinals through the SQL server/client (#1024). The count remains 33/49 because
`ALTER TABLE` and `ORDER BY` were already advertised for smaller executable subsets.

The **16 excluded clauses** are set operations (`UNION`,
`INTERSECT`, `EXCEPT`), CTEs (`WITH`, `RECURSIVE`), window clauses (`WINDOW`,
`OVER`, `PARTITION`), views (`CREATE VIEW`, `DROP VIEW`), `NATURAL`, `USING`,
`TOP`, `ALL`, `FETCH`, and `RETURNING`. Counts describe named clauses, not
complete ISO SQL support; the boundaries below are part of the contract.

| Statement | Status | Notes |
|---|---|---|
| `SELECT` | Supported subset, measured | One stored table or virtual system relation, or a two stored-table `INNER JOIN ... ON`; `DISTINCT`, projections and aliases, scalar expressions, `WHERE`, grouping, multi-expression `ORDER BY ASC/DESC`, nonnegative integer `LIMIT`/`OFFSET`. `COUNT(*)`, `COUNT(expr)`, `SUM`, `AVG`, `MIN`, and `MAX` execute in grouped and ungrouped queries. See the aggregate contract below. `SELECT` without `FROM` is rejected by the planner. |
| `INSERT` / `VALUES` | Supported subset, measured | Optional column list, multi-row literal/scalar `VALUES`, and transactional `INSERT ... SELECT` with the same destination coercion, defaults, and constraints. `VALUES` expressions have no columns in scope: a column reference reports `COHSQLE005` before anything executes (see [VALUES and counts have no column scope](#values-and-counts-have-no-column-scope-1165)). Subqueries inside `VALUES` are excluded; use `INSERT ... SELECT`. |
| `UPDATE` | Supported | multi-column `SET`, `WHERE`; no aggregate in either (see [Grouping and aggregate functions](#grouping-and-aggregate-functions-1020)) |
| `DELETE` | Supported | optional `WHERE`, without an aggregate |
| `CREATE TABLE` | Supported | `IF NOT EXISTS`, column definitions with parameterized types, `COLLATE <name>`, `NOT NULL`/`NULL`, `DEFAULT <literal>`, column and table `PRIMARY KEY`, `REFERENCES`/`FOREIGN KEY`, `CHECK`, and `UNIQUE`; optional `CONSTRAINT <name>` |
| `ALTER TABLE` | Supported subset, measured | ADD/DROP COLUMN and ADD/DROP CONSTRAINT execute. ADD COLUMN literal defaults backfill old-row reads and apply to subsequent inserts that omit the column; explicit NULL follows nullability. Nullable additions without a default read NULL. NOT NULL additions to populated tables require a non-null default. Invalid defaults and nonliteral expressions reject before mutation. Column COLLATE persists and governs default comparisons. See the default, atomicity and MVCC contract below (#1023). DROP COLUMN changes only the catalog: it rewrites no row, survives a crash at any point on one definition, and SELECTs running beside it read every value in its own column; see the DROP COLUMN contract below (#1241). |
| `DROP TABLE` | Supported | `IF EXISTS` |
| `CREATE INDEX` | Supported | `CREATE [UNIQUE] INDEX [IF NOT EXISTS] <name> ON <table> (<column> [, ...])` — plain column lists only (no `ASC`/`DESC`, expressions, or `INCLUDE`; each is an additive extension) |
| `DROP INDEX` | Supported | `DROP INDEX [IF EXISTS] <name> ON <table>` — the `ON <table>` qualifier is required: index names are scoped per table |
| `CASE` | Supported, measured | Simple and searched forms, multiple branches, `ELSE`, implicit null result, and row expressions; branch expressions remain limited to the executable scalar subset. |
| `ORDER BY` | Supported subset, measured | Multiple source-column/scalar-expression keys, projection aliases (bare or nested in expressions), and one-based select-list ordinals execute with ASC/DESC. Unqualified aliases take precedence over same-named source columns; qualified references bind to the source. Composes with DISTINCT, LIMIT/OFFSET, supported joins, grouped/aggregate queries, and system relations. Zero, negative, out-of-range, and non-integer numeric ordinals error; `1 + 1` remains a constant expression. Explicit NULLS FIRST/LAST, derived-table output ordering, and GROUP BY ordinals report `COHDBL001`. See the ordering contract below (#1024). |
| `CAST` | Supported subset, measured | Exact signed integer, decimal, boolean, and string conversions in projections, predicates, ordering, and DML expressions, including the SQL server/client. See the exact pair and error contract below. CAST in DEFAULT or CHECK remains rejected. |
| `COLLATE` | Supported subset, measured | Column and expression overrides: `binary`, `case_insensitive`, `case_accent_insensitive`, plus compatibility `invariant` with scan execution only. Effective collation governs comparisons, `LIKE`, ordering, grouping, `DISTINCT`, and unique keys. See the collation contract below. |
| `JOIN` | Supported subset, measured | Two stored-table `INNER JOIN ... ON` or bare `JOIN ... ON`, with index assistance where the mandatory equality predicate matches an applicable secondary-index prefix. `LEFT [OUTER]`, `RIGHT [OUTER]`, `FULL [OUTER]`, `CROSS`, additional joins beyond two tables, joins without `ON`, comma joins, and joins of virtual system relations report `COHDBL001`. See the precise contract below. |
| `GROUP BY` / `HAVING` | Supported subset, measured | One or more grouping expressions; `WHERE` filters input rows and `HAVING` filters groups after aggregation. Composes with supported two-table inner joins, `ORDER BY`, `LIMIT`, and `OFFSET`, including server/client execution. Ungrouped, unaggregated projected columns are errors. `DISTINCT`/`ALL` aggregate modifiers, grouping extensions, windows, and ordered-set aggregates report `COHDBL001`. |
| Subqueries / `INSERT ... SELECT` | Supported subset, measured | Uncorrelated scalar, `IN`/`NOT IN`, and `EXISTS`/`NOT EXISTS` queries in supported SELECT expressions, plus transactional insert-source queries. All use the outer statement snapshot; nesting is limited to 32 subquery levels, within the statement's expression nesting limit (256 levels by default, `SQL0006`). Correlated queries, derived tables, quantified comparisons, subqueries in UPDATE/DELETE, VALUES, CHECK/DEFAULT, and LIMIT/OFFSET expressions report `COHDBL001`. See the subquery contract below. |
| `TOP` / `SELECT ALL` / `FETCH` | Recognized, not supported | row-limit and select modifiers rejected with `COHDBL001` |
| DML `RETURNING` | Recognized, not supported | rejected with `COHDBL001` |
| `NATURAL JOIN` / `JOIN ... USING` | Recognized, not supported | rejected with `COHDBL001` |
| `UNION` / `INTERSECT` / `EXCEPT` | Recognized, not supported | keywords lexed; rejected with `COHDBL001` |
| `WITH` / `WITH RECURSIVE` (CTEs) | Recognized, not supported | rejected with `COHDBL001` |
| Window functions / `OVER` / `WINDOW` | Recognized, not supported | function names lexed; clauses rejected with `COHDBL001` |
| `CREATE VIEW` / `DROP VIEW` | Recognized, not supported | rejected with `COHDBL001` |
| `CONSTRAINT` / `FOREIGN KEY` / `REFERENCES` / `CHECK` / `UNIQUE` constraints | Supported subset, measured | Column and table declarations normalize into constraint definitions; UNIQUE lowers to a unique catalog index. NULL is an equal index key: a second NULL violates a single-column UNIQUE constraint. Foreign-key NULL values are allowed; CHECK accepts UNKNOWN and rejects FALSE. CHECK expressions must be deterministic Boolean row expressions with the engine's scalar functions (built-in or registered, `IMMUTABLE` only), each called with arguments its signature accepts (`COHSQLE006` otherwise); parameters and aggregates are excluded. CHECK predicates and DEFAULT literals are stored as canonical text and parsed once per table version; see [Persisted definitions are canonical](#persisted-definitions-are-canonical). |
| `ON DELETE CASCADE` / `ON DELETE RESTRICT` | Supported | omitted deletion action defaults to `RESTRICT`; a cascade deletes its whole transitive closure with no depth limit, so a self-referencing chain deletes however long it is (#1164), and a cycle deletes each row once; `DROP TABLE ... CASCADE` is not supported |
| `ON UPDATE` | Recognized, not supported | absent from the profile; rejected with `COHDBL001` |
| `BEGIN [TRANSACTION]` / `COMMIT [TRANSACTION]` / `ROLLBACK [TRANSACTION]` | Supported | session-scoped transactions through the existing MVCC coordinator; `TRANSACTION` alone is not a statement |
| `MERGE`, `TRUNCATE`, `GRANT` | Not in the dialect | `SQL0002` |

## Statement completeness (#1068)

A request carries exactly one statement, and the parser either consumes all of
it or reports what it could not. One terminating `;` is optional, and comments
may follow it. Any other text after a complete statement reports `SQL0003` at the
first token the parser did not consume, and nothing executes: the text-execute
seam throws `DatabaseParseException` (`ParseFailure` on the wire), and a typed
request returns an error result carrying the diagnostics. Until #1068 the parser
checked for leftover tokens only after `CREATE TABLE`, `ALTER TABLE` and
`DROP TABLE`, so every other statement executed the prefix it understood:
`DELETE FROM t WHRE id = 1` read `WHRE` as an alias of `t` and deleted every row.

Required tokens are required. A missing closing `)`, `THEN`, `END`, the `AND` of
`BETWEEN`, the `BY` of `ORDER BY`, the `SET` of `UPDATE`, the `=` of an
assignment, a table, index or column name, or a `VALUES` row reports `SQL0003`
where it was expected. A clause keyword in a name position means the name is
missing: `SET a = 1, WHERE id = 1` reports the trailing comma instead of
assigning `(id = 1)` to a column named `WHERE`. `IF NOT EXISTS` (CREATE) and
`IF EXISTS` (DROP) must be complete, and a column's `NOT` must be followed by
`NULL`. A `?` placeholder name never executes, because it is built only together
with an error.

Lexical errors are reported too. A string literal, quoted identifier or block
comment without its closing delimiter would otherwise run to the end of the text
as one token and swallow the clauses after it: `DELETE FROM t /* WHERE id = 1;`
deleted every row. Each reports `SQL0003` at its opening delimiter. A character
outside the dialect, such as `?`, `#`, `^`, `§`, a backtick or a zero-width space,
lexes as `TokenType.Unrecognized` and reports one `SQL0003` at the character
(#1101). It used to lex as a one-character identifier, so `SELECT * FROM t ?` bound
`?` as an alias and ran, and `SELECT ? FROM t` returned a column named `?`. The
statement now keeps only its command type: no alias, column or `NULL` placeholder
is built around the character. The message that names the supported parameter
forms for `?` and `:name` belongs to shared-parameters. A non-ASCII decimal digit,
such as Arabic-Indic `٣` or fullwidth `１`, is a character outside the dialect too:
only ASCII digits make a numeric literal. It used to lex as an integer, parse
cleanly and then fail at execution with an uncoded `FormatException`. A stray
character before the statement, such as a byte order mark that was not stripped,
is reported once, and the statement after it is not reported as an unknown
command. A numeric literal whose exponent has no digits, such as `1e` or `2.5E-`,
reports `SQL0003` at the literal; it also used to fail only at execution.

| Written | Reported as `SQL0003` | Diagnostic starts at |
|---|---|---|
| `DELETE FROM t WHRE id = 1` | Leftover text after the DELETE; the message adds that `WHRE` was read as an alias of table `t` | `id` |
| `UPDATE t SET a = 1 WHRE id = 1`, `UPDATE t SET ... FROM u WHERE ...` | Leftover text after the UPDATE | `WHRE`, `FROM` |
| `INSERT ... ON CONFLICT DO NOTHING`, `INSERT ... ON DUPLICATE KEY UPDATE ...` | Leftover text after the INSERT | `ON` |
| `SELECT ... OFFSET 1 LIMIT 2` | `LIMIT` must precede `OFFSET` | `LIMIT` |
| `name LIKE 'a!%' ESCAPE '!'` | `LIKE ... ESCAPE` is not supported; `%` and `_` are always wildcards | `ESCAPE` |
| `a IS [NOT] DISTINCT FROM b`, `a IS [NOT] TRUE`, `FALSE` or `UNKNOWN` | Only `IS [NOT] NULL` is supported | `IS` |
| `a NOT NULL`, `flag NOT FALSE`, `x NOT y` | `NOT` after an operand must begin `NOT BETWEEN`, `NOT IN` or `NOT LIKE`; it used to parse as `x AND NOT y` | the token after `NOT` |
| `WHERE id = :id`, `SET a = :a`, `WHERE id = ;`, a statement ending in `WHERE` or `=` | Expected an expression; the NULL literal the parser used to substitute is gone | the token, or the end of the text |
| `WHERE (id = 1`, `id IN (1, 2`, `id BETWEEN 1 2`, `CASE WHEN c 'a' END`, `ORDER id`, `UPDATE t;`, `DELETE;`, `VALUES (1), ()` | The missing token, keyword, name or value | where it was expected |
| `VARCHAR(25 5)`, `DECIMAL(10, 2, 5)`, `VARCHAR(-5)`, `VARCHAR(MAX)` | Type arguments must be one or two unsigned integer literals | the first offending token |
| `'abc WHERE id = 1`, `"id FROM t`, `/* WHERE id = 1` | Unterminated literal, quoted identifier or comment | the opening delimiter |
| `SELECT * FROM t ?`, `SELECT a # b FROM t`, `WHERE b = ?`, `WHERE id = ١` | A character outside the dialect; nothing is bound around it | the character |
| `SELECT 1e FROM t`, `WHERE a = 2.5E-` | A numeric literal whose exponent has no digits | the literal |
| `SELECT 1; SELECT 2`, `BEGIN; DELETE FROM t`, `COMMIT;;` | A request accepts exactly one statement | the first token after `;` |

A `--` comment ends before the first line terminator: line feed (LF), carriage
return (CR), next line (NEL, U+0085), line separator (LS, U+2028) or paragraph
separator (PS, U+2029); CR LF is one line break (#1150). The terminator set is the
one `Database.Language/docs/DESIGN.md` records for every language. The comment
used to end only at LF, so `DELETE FROM t -- note<CR>WHERE id = 1;` parsed as an
unfiltered `DELETE` and removed every row, and `UPDATE t SET a = 1 -- note<CR>WHERE
id = 1;` updated every row. The leftover-text rule above could not catch either,
because the swallowed clause was part of the comment token, not text after the
statement. Both now affect exactly one row, on both session seams and over the
wire, and a statement on the line after a comment is leftover text. Text on the
comment's own line is still comment text, vertical tab and form feed do not end a
comment, and a string literal or block comment may span lines. `//` is not a
comment: `/` is the division operator, so `//` is a syntax error.

A recognized clause outside the profile keeps its `COHDBL001`, for example
`RETURNING` or `FETCH`. The parser stops at such a clause by design, so the text
from that clause on adds no second diagnostic. Text the parser stopped at before
it reached the clause still reports `SQL0003`: `DELETE FROM t WHRE id = 1
RETURNING *` reports both, so fixing `RETURNING` does not reveal a new error. A
derived table, `(SELECT ...) alias`, is skipped as a unit after its `COHDBL001`.
Named `:name` parameters are not part of the dialect; bind `@name` or `$1`.

A syntax error is reported once per position: recovery leaves the offending
token in place for the enclosing parser, which finds it already reported. One
mistake can still yield two diagnostics when it breaks two independent rules, for
example an unclosed type argument list followed by a second statement after `;`.

**Signs.** The operand of a sign is itself a unary expression, so `- -1` is
`1`; it used to parse as a negated NULL followed by leftover text. `+` is ISO
unary plus on any operand: `+a`, `+(1 + 2)` and `+@p` parse to a unary-plus node
(`SqlUnaryOperator.Plus`). Directly before a numeric literal the `+` stays part of
the literal, so `+1` is the literal `1` and `ORDER BY +1` remains an ordinal; `+(1)`
is unary plus applied to `1`. Unary plus returns a numeric operand with its value
and type unchanged (`+tiny` is still `TINYINT`, unlike `-tiny`, which computes in
BIGINT) and propagates NULL. Both signs require a numeric operand. When the plan
already knows the operand is not a number (a string or Boolean literal, a
string, Boolean, date/time or other non-numeric column, a predicate, `||`,
`UPPER`/`LOWER`, a CAST to a non-numeric type, or a scalar subquery of such a
type), the statement fails before it reads a row, with `COHSQLE003` (see the
[arithmetic contract](#arithmetic-and-numeric-faults-1069)): `+'abc'`, `+TRUE`
and `-name` fail over an empty table exactly as over a populated one. A sign
directly over a parameter is checked against the value the statement binds, also
before any row is read: `+@p` with `@p = 'abc'` fails with `COHSQLE003` whether or
not the table has rows, and a NULL value propagates. An operand whose type only a
row's value reveals, such as a `CASE` that returns a string, fails with the same
code when the row is evaluated. `+a` and `+(1 + 2)` reported
`SQL0003` before unary plus existed. `~` is not a sign of the dialect; see
[Expressions](#expressions).

**ALTER TABLE actions.** `ADD [COLUMN]`, `ADD CONSTRAINT`, `DROP [COLUMN]` and
`DROP CONSTRAINT` parse. Any other action, such as `RENAME TO`, `RENAME COLUMN`,
`ALTER COLUMN` or `MODIFY`, reports `SQL0003` naming the action at parse time.
The statement then carries no action node. It used to carry a placeholder
`DROP COLUMN ?` that failed later, in the catalog. A missing action, table name,
column name or constraint name also reports `SQL0003`; `ADD` or `DROP` directly
after `ALTER TABLE` is a missing table name. `TABLE` is required: `ALTER INDEX`,
`ALTER VIEW` and other `ALTER <object>` forms report `SQL0003` naming the object.

**Unknown functions.** A call to a name outside the engine's function catalog (the
standard library and the functions the application registered on the engine, see
[Builtin functions](#builtin-functions)) and outside the profile's function list fails
at plan time with `Unknown function '<name>'.`, a `DatabaseException`
(`ExecutionFailure` on the wire). The planner checks every expression position
before it binds the statement or reads a row: projections, predicates, joins,
grouping, ordering, `LIMIT`/`OFFSET`, subqueries, DML values and `CHECK`. The
statement therefore fails the same way over an empty table as over a populated
one; the evaluator used to find the name per row, so an empty table succeeded.
Declared names that do not execute yet, such as `NULLIF` and `TRIM` (see
[Builtin functions](#builtin-functions)), are not unknown. They still fail during
evaluation; #1103 rejects them at parse time with `COHDBL001`.

**Function arguments (#1189).** A call to an executable function must pass a number
of arguments its signature accepts (the table under
[Builtin functions](#builtin-functions)): `UPPER`, `LOWER`, `LENGTH` and `ABS` take
exactly one, `COALESCE` one or more, each aggregate exactly one, and only `COUNT`
accepts `*`. Any other call fails while planning with `COHSQLE006`, a
`DatabaseException` (`ExecutionFailure` on the wire), in every position the
unknown-function check covers: projections, `VALUES`, `UPDATE ... SET`, predicates
(`WHERE`, `JOIN ... ON`, `HAVING`), grouping, ordering, `LIMIT`/`OFFSET`,
subqueries, `CHECK` (in `CREATE TABLE`, `ALTER TABLE ADD CONSTRAINT` and `ADD
COLUMN`) and `DEFAULT`. Nothing is read or written, and the statement fails the same
way over an empty table as over a populated one. The message names the function as
written, the counts it accepts, what the call passed and the accepted call forms:

```text
COHSQLE006: Function 'ABS' takes exactly 1 argument but was called with 2. Accepted: ABS(numeric).
COHSQLE006: Function 'COUNT' takes exactly 1 argument or '*' but was called with 2. Accepted: COUNT(*) or COUNT(value).
COHSQLE006: Function 'SUM' takes exactly 1 argument but was called with '*'. Accepted: SUM(numeric).
COHSQLE006: Function 'COUNT' takes exactly 1 argument or '*' but was called with 2 arguments including '*'. Accepted: COUNT(*) or COUNT(value).
```

A call passes `'*'` only when `*` is its sole argument; the parser also accepts `*`
after other arguments (`COUNT(id, *)`, `COALESCE(a, *)`), and such a call is reported
by its full count.

**Overloads (E2).** A function name can carry several overloads, distinguished by their
parameter types. Among the overloads whose count matches, the planner chooses the one
the arguments' types fit best: an exact type, then an implicit widening along INT8 →
INT16 → INT32 → INT64 → DECIMAL → DOUBLE (nothing converts to or from text, and REAL does
not widen), then a polymorphic overload. An integer literal has the smallest of INTEGER
and BIGINT that holds it; a NULL literal, a NULL parameter and a grouped value match any
type. A call no overload's types accept also reports `COHSQLE006`, and one that several
accept equally well reports `COHSQLE008` (SQLSTATE 42725); a `CAST` on an argument
chooses. Both are raised while planning:

```text
COHSQLE006: Function 'SUM' has no overload that accepts argument types (TEXT). Accepted: SUM(numeric).
COHSQLE008: Function call 'describe(unknown)' is ambiguous: describe(BIGINT) and describe(TEXT) accept it equally well. Cast an argument to choose one.
```

The engine resolves a call's arguments before the call itself, as PostgreSQL's parse
analysis does, so `COALESCE(name, UPPER())` reports `UPPER`, and `FOO(ABS())` reports
`ABS` rather than the unknown `FOO`. Until #1189 the evaluator evaluated an argument
only when a call had exactly one, so `SELECT ABS(1, 2), UPPER(), COALESCE() FROM t`
returned three NULLs, `CHECK (ABS(c, 1) > 0)` was stored and admitted every row, and
an aggregate with no argument outside a projection (`UPDATE t SET a = SUM()`)
succeeded over an empty table. A wrong aggregate count in a projection failed while
planning already, without a code. A declared name that does not execute has no
signature, and fails as described above. A database whose catalog stores a CHECK
with such a call does not open; see [Persisted definitions are
canonical](#persisted-definitions-are-canonical).

**Guard.** `SqlStatementCompletenessTests` (Sql.Language) holds a complete statement
form for every profile clause and every `SqlQueryCommandType`, appends leftover
text to each form (words, a literal, `)`, a misspelled clause, text after `;`,
an unterminated string or comment, a character outside the dialect, and words on
the line after a `--` comment for every line terminator), and fails unless every
combination reports an error after the form. A clause added to the profile
without a form fails the test, as does a new statement kind.

## Persisted definitions are canonical

Every SQL expression the catalog persists is stored as **canonical SQL rendered
from its parsed tree**, never as the text the user wrote: a `CHECK` predicate, a
literal column `DEFAULT`, and — when they arrive — expression defaults (#1121) and
view queries (#1124), which must use the same path. `SqlExpressionRenderer` (this
package) produces the canonical text; the engine's one persistence helper renders
it, proves before anything is stored that the text parses back to the declared
tree, and is the only code that reads it back. The canonical form is a function of
the tree alone:

- Keywords are upper case (`AND`, `IS NOT NULL`, `NOT BETWEEN ... AND ...`,
  `CASE ... END`, `TRUE`, `NULL`); collation names are lower case, as the parser
  normalizes them.
- Binary operators, `AND`/`OR` and list separators take single spaces; `!=` is
  written `<>`. Comments and line breaks are dropped.
- Parentheses appear only where precedence needs them: `(qty + 1) * 2` keeps
  them, `((qty - 1) - 2)` becomes `qty - 1 - 2`, `qty - (1 - 2)` keeps them. A sign
  applied to a sign is parenthesized (`- -1` becomes `-(-1)`, never a `--`
  comment), and `+(1)` keeps its parentheses so it stays unary plus. An `AND` or
  `OR` chain is written term by term: `(a AND b) AND c` becomes `a AND b AND c`,
  and `a AND (b AND c)` keeps its parentheses, as it did before chains became one
  node (#1151).
- Literals keep their value: strings double embedded quotes (`'O''Brien'`),
  numbers keep their digits as written (`007`, `1.5e3`, `.5`), and a `+` that was
  part of a numeric literal is gone (`+5` is `5`).
- Names keep their spelling. An identifier is delimited with `"` only when the
  lexer would not read it back as one plain word: a keyword, a builtin function
  name, a word with a positional meaning such as `escape`, a name with spaces,
  punctuation or a combining mark, or a name starting with a digit. `"qty"`
  becomes `qty`, while `"order"` and `"my qty"` keep their quotes; unicode words
  such as `größe` stay bare. Function names keep their spelling (`lower(name)`); a
  user function named like a window function stays delimited (`"rank"(x)`).

For example, `CHECK (QTY>0   and /* upper */ qty<100)` is stored, and reported in
`INFORMATION_SCHEMA.CHECK_CONSTRAINTS.CHECK_CLAUSE`, as `QTY > 0 AND qty < 100`,
and `DEFAULT +5` as `5`. The renderer's corpus and a randomized round-trip test
(`SqlExpressionRendererTests`) pin both the canonical spelling of every form a
CHECK accepts and the invariant that parsing the rendered text yields the same
tree. A CHECK supplied by a compiled schema (`table.Check(name, sql)`) keeps its
author's spelling in the schema document; the engine compares it with the catalog by
canonical form, so reapplying an unchanged schema stays a no-op. Before the engine's
build touches any file it parses that text as exactly one expression and binds it to
the engine's functions and the table's declared columns, applying the same rules
`CREATE TABLE` applies (only `IMMUTABLE` functions among them), and refuses the
declaration with `COHSQLP001` when it does not bind.

**Size.** A table's whole definition, the canonical text of its CHECKs and DEFAULTs
included, is one catalog record of at most 8,092 bytes. That bounds a stored CHECK
at a few hundred comparisons however the nesting limit is configured; a larger
definition fails its DDL with `The definition of table '<name>' encodes to <n>
bytes, more than the 8092 bytes a catalog record can hold.` and stores nothing.

**Parsed once.** The engine parses and binds a table's persisted CHECK and DEFAULT
definitions once per table version — when the database opens, and when a DDL
statement (`CREATE TABLE`, `ALTER TABLE ADD/DROP CONSTRAINT`, `ADD/DROP COLUMN`)
publishes a new version — and caches the result. Validated writes evaluate the
cached predicate; no statement parses catalog text. A dropped, re-created or
altered table is a new version, so no write is ever checked against a stale
definition.

**Fails at open.** Because canonical text always reloads, a persisted definition
that does not parse, is more than one expression, or no longer binds to its table
means the catalog is damaged or came from an incompatible engine build. Opening
the database then fails with `Database '<name>' cannot be opened.`, naming the
table and the constraint or column (`CHECK constraint 'ck_qty' on table
'dbo.orders' cannot be loaded: ...`). It never surfaces later as a failure of
every write to that table. A CHECK whose call names an application function the
opening engine no longer registers is not damage and does not fail the open: its
writes fail with `COHSQLE009` instead (see Builtin functions, "A stored CHECK whose
function is gone"). Canonical storage is part of data-storage format 4;
text from earlier formats is not migrated, and a database on an earlier format
whose catalog holds a CHECK or DEFAULT fails the open with a format error that
says so.

**Binding is not re-validation.** Opening a database binds each stored CHECK —
its columns and collations resolve, each function call passes arguments its
signature accepts, and it is a Boolean row predicate the engine can evaluate — but
does not apply again the rules `CREATE TABLE` and `ALTER TABLE` use to accept one,
such as the sign operand check or the ban on `CAST`. Those rules may tighten in a
later release; a predicate an earlier release accepted keeps opening and is
enforced as stored, and a row it cannot evaluate fails its own statement with the
evaluator's coded error.

A call's argument count is not such a rule: a call no signature accepts has no
value on any row (#1189). Engine builds before #1189 stored such CHECKs in format-4
catalogs, and format 4 is unreleased and has no upgrade path (#1152), so none is
carried forward. Opening a database whose catalog holds one fails with the coded
error and the constraint's name, and sends the operator to the build that stored it
rather than to a backup, which holds the same definition:

```text
Database 'shop' cannot be opened. CHECK constraint 'ck1' on table 'dbo.t' cannot be loaded: its persisted definition 'COALESCE() IS NULL' calls a function with arguments the function does not accept (COHSQLE006: Function 'COALESCE' takes 1 or more arguments but was called with none. Accepted: COALESCE(value [, value ...]).). An engine build that did not check function arguments stored it, and this engine cannot evaluate it; open the database with that build and drop the constraint or replace it with a valid one.
```

That holds for the special form `COALESCE`. A stored call of a function name with an
argument count no built-in takes, `ABS(c, 1)`, can only come from an application's
overload of the name since E2 lets an application add one for another arity, so it is a
function the engine no longer registers: the database opens and writes fail with
`COHSQLE009` (see [Builtin functions](#builtin-functions)).

## Ordering, output aliases and ordinals (#1024)

`ORDER BY` evaluates its keys before `OFFSET` and `LIMIT`, using the result of
grouping and `HAVING` when present. It accepts source columns, executable scalar
expressions, output aliases, and one-based select-list ordinals. These keys
compose with `DISTINCT`, pagination, the supported two-table inner join, grouped
and ungrouped aggregates, and virtual system relations. Mixed ASC/DESC keys
compare from left to right. NULL sorts first in ASC and last in DESC. Rows with
equal keys have no promised relative order; add a tie-breaking key for stable
paging. The same expression and column collation rules apply to aliases and
ordinals as to the values they reference.

**Name precedence:** an unqualified name in an ordering expression resolves to
an explicit SELECT alias first, case-insensitively, then to a source column.
This agrees with ISO/IEC 9075 select-list precedence for a bare ordering name
and extends the same rule consistently to names inside scalar expressions.
Thus `SELECT age AS years FROM t ORDER BY years` and `ORDER BY years + 1`
both use the projected age, even if `t` also has a `years` column.
`ORDER BY t.years` explicitly selects that source column. If several output
columns declare the same alias, using that alias is an ambiguity error; an
ordinal can identify the intended column. Alias references use the selected
expression's source scope: an alias never recursively refers to itself or to
another output alias. Grouped queries use the same rules and retain their
grouping validity checks. Aggregate call arguments still bind against their
input source, and inner SELECT bodies resolve names in their own query scope.

**Ordinal syntax:** a standalone integer literal selects an output column after
wildcard expansion: `ORDER BY 1` uses the first output, and `ORDER BY 2 DESC`
uses the second in descending order. A leading `+` directly before the numeric
literal and parentheses around a literal are accepted. Zero, negative values,
values beyond the output width, and non-integer numeric literal syntax
(including `1.0`, `.5`, and `1e0`) are precise planning errors, even on empty
input. They are never executed as constant keys. In contrast, an expression
such as `ORDER BY 1 + 1` is evaluated normally as a constant, not ordinal 2;
use another ordering key when such an expression leaves every row tied.
Parameters and numeric literals inside larger expressions are values, not
output positions. Ordinals are available only in ORDER BY. Standalone numeric
GROUP BY keys report `COHDBL001`; `GROUP BY 1 + 1` remains a constant grouping
expression. WHERE/HAVING predicates and LIMIT/OFFSET values retain their normal
expression meanings and never refer to select-list positions.

**Explicit boundaries:** `NULLS FIRST` and `NULLS LAST` report `COHDBL001`.
Derived tables in FROM/JOIN remain unsupported, so ordering by an output of
`FROM (SELECT ...)` reports `COHDBL001` too. Supported uncorrelated scalar
subquery expressions, including projected scalar subquery aliases, and ordering
inside supported subqueries keep the same ordering rules. Alias names in
GROUP BY and HAVING remain outside the projection-alias binding scope; repeat
the source/grouping expression or aggregate there.

Phase 13's partial boundary is closed for the forms above: ungrouped aliases no
longer fail with an unknown-column error, and `ORDER BY 1 DESC` no longer returns
the unsorted scan. Execution and wire tests use deliberately scrambled values
whose required ordering differs from both insertion order and the actual scan.

## ADD COLUMN defaults, atomicity and MVCC (#1023)

`ALTER TABLE t ADD COLUMN extra INT DEFAULT 7` makes every preexisting row read
7 while preserving all original values. The backfill is **resolved at read time
from persisted catalog metadata**: a missing trailing field takes the added
column's validated literal default. Existing row bytes and MVCC writer/deleter
stamps are unchanged. A stored NULL remains NULL; it is never mistaken for a
missing field. A later UPDATE writes the resulting full row normally.

A subsequent INSERT that omits the column uses the same default. Explicit NULL
is stored for nullable columns and rejected for NOT NULL columns. A nullable
addition with no default, or with `DEFAULT NULL`, yields NULL for old rows and
omitted inserts. On a populated table, NOT NULL requires a non-null default;
without one the statement fails. On an empty table, NOT NULL without a default
is allowed, and future inserts must supply a value. The emptiness check considers
current committed rows; historical row versions visible only to an older row
snapshot retain a missing NULL field when such an addition has no default.

Only literal defaults execute. `DEFAULT (1 + 2)`, function calls, parameters,
CAST, and other expressions are rejected during planning, before schema or data
mutation, using CREATE TABLE's diagnostic:
`Column 'extra': only literal DEFAULT values are supported.` A call to a name
outside the profile's function list fails first, with `Unknown function '<name>'.`,
and so does a call whose arguments its function does not accept, with `COHSQLE006`
(`DEFAULT ABS(1, 2)`; see [Statement completeness](#statement-completeness-1068)),
and a parameter such as `:x` is a parse error. Defaults must
convert to the declared storage type and fit its bounds, including string length
and decimal precision/scale; invalid conversions and out-of-range defaults reject
before publication. `CREATE TABLE` applies the same check to every column
`DEFAULT` before it publishes the table (`INT DEFAULT 'abc'` fails the DDL, not
each later INSERT that omits the column). Strings are not truncated and decimals
are not rounded.
Approximate floating-point defaults use normal IEEE conversion rounding, but
nonfinite results and nonzero values that underflow to zero are rejected.
`COLLATE` on an added string column preserves the literal's
original text and controls its comparisons and indexes, just as for CREATE TABLE.

Publication changes the complete table definition atomically after validation;
there is no row-rewrite phase to partially commit. A failed ADD COLUMN preserves
the previous schema, rows, and catalog state. The default survives catalog reopen,
so old-row reads recover the same values without any separate backfill journal.
Already bound plans keep their immutable table definition. Ordinary statements
planned after the change use the complete new definition, even in a transaction
whose older MVCC snapshot still selects earlier row versions; missing fields
resolve consistently from that statement's definition. Ordinary table schemas
are not pinned to BEGIN. The separate INFORMATION_SCHEMA snapshot contract below
is unchanged.

DDL remains self-committing and is rejected inside an explicit transaction with
`COHSQLT003`; it cannot be undone by a later ROLLBACK. ALTER against a code-owned
table (`DatabaseObjectOwner.Schema`) remains refused unless performed by its
owning schema deployment.

## DROP COLUMN (#1241)

`ALTER TABLE t DROP COLUMN c` removes `c` from the table's definition: `SELECT *`,
INSERT without a column list, name resolution, the wire result header and the
system views no longer see it. It is refused, with nothing changed, for an unknown
column, a primary-key column, a column an index covers (drop the index first), a
column a foreign key, UNIQUE or CHECK constraint uses or references, and a table's
last column. A refusal for a constraint names it, a table-level CHECK that reads the
column included: "Column 'c' is referenced by constraint 'ck_t'. Drop the constraint
first."

The drop is **a catalog-only change**, as in PostgreSQL: the column's physical
position is marked dropped and no stored row is read or rewritten, so the statement
takes the same time on an empty table and a large one and holds up no other
table's writes. The dropped values stay in the rows that hold them, unreadable,
and the space returns as rows are updated and their old versions purged. Rows
written afterwards store a one-byte NULL in the dropped column's place when a
live column follows it, and nothing when none does, so each dropped column with
a live column after it lowers the largest row the table can store by one byte.
(PostgreSQL likewise keeps a null-bitmap bit for each dropped column in every
new row.) A crash at any point leaves either the
table as it was or the column dropped, with every value in its own column either
way. DDL stays self-committing (above).

Statements running beside the drop: a write or a join waits for it, and one planned
on the old definition then fails with "changed while the statement was waiting" and
can be retried. A single-table SELECT does not wait. Whichever definition it was
planned on, every value it returns is in its own column. One planned before the
drop still sees the dropped column, with the values of the rows its snapshot
sees; rows written after the drop are never visible to it, because a statement's
snapshot is taken before it is planned. Snapshots older than the drop keep
reading their rows, without the dropped column once a statement is planned on
the new definition.

`ADD COLUMN` after a drop adds a new column, even under the dropped column's name:
existing rows read the new column's default (or NULL), never the dropped values.
`INFORMATION_SCHEMA.COLUMNS.ORDINAL_POSITION` numbers the live columns 1..n, so a
column behind a dropped one moves up by one, as the SQL standard specifies;
PostgreSQL instead reports its internal attribute number, with gaps. A table's
dropped positions are never reused; each one keeps five bytes in the table's
catalog definition, which is limited to one catalog record, so once a table has
dropped about 1,590 columns (fewer when its live columns are many or have long
names) ADD and DROP COLUMN on it are refused with a message that counts the
dropped columns. Only a new table starts without them: recreate the
table and copy its rows (`CREATE TABLE`, `INSERT ... SELECT`). PostgreSQL counts
dropped columns against its 1,600-column limit in the same way.

## Collation (#1025)

String comparisons resolve from the database default, overridden by a column's
declared `COLLATE`, overridden by an expression `COLLATE`. Nested expression
overrides resolve innermost first. A database without a configured default uses
`binary`. Column metadata and the database default survive restart. The default
is established when the database is created and is fixed for its lifetime;
opening a populated database under a different default is rejected, because
existing index keys are encoded through the collation they inherited. There is
no session override.

```sql
CREATE TABLE people (name TEXT COLLATE case_insensitive UNIQUE);
SELECT name FROM people WHERE name = 'alice';
SELECT name FROM people WHERE name = 'Alice' COLLATE binary;
```

`binary` compares Unicode code points using unchanged UTF-8 bytes.
`case_insensitive` applies invariant Unicode simple case folding, then compares
UTF-8 bytes. `case_accent_insensitive` canonically decomposes Unicode text,
removes combining marks, applies the same fold, then compares UTF-8 bytes.
These transforms are culture-independent and do not use `CompareInfo`.
Collation changes comparison rules, never the stored or returned spelling.

`WHERE` comparisons, `LIKE`, `ORDER BY`, `GROUP BY`, `DISTINCT`, and `UNIQUE`
use the effective collation. Grouping and distinct keys use matching equality
and hash rules. A case-insensitive unique column rejects a second spelling that
folds to an existing key; its enforcing index uses the same transform. A seek
is eligible only when the predicate and index use matching, index-backed
collations. An explicit override that differs from the index uses a scan.

Compatibility `invariant` is **not index-backed**: its linguistic comparison
cannot be represented by the supported byte transforms. Predicates using it
scan even when another collation has an index on the column; declaring an index
or index-backed constraint under `invariant` is rejected.

Unknown names, culture-aware collations, user-defined `CREATE COLLATION`,
session overrides, and collation-aware full-text indexes report `COHDBL001` or
an explicit execution error. Full linguistic collation is deferred to #1026.
SQL defaults do not apply to Documents, Graph, Key-Value, or Blob comparisons.

## Joins (#1019)

`SqlClauses.Join` represents the executable subset `INNER JOIN ... ON`, including
bare `JOIN ... ON`. `SqlLanguageProfile.SupportsJoin` advertises only
`SqlJoinType.Inner`. A SELECT can join exactly two stored tables; aliases and
schema-qualified references such as the following execute, including over the
SQL server/client:

```sql
SELECT usr.Users.FirstName, usr.Users.LastName, usr.UsersProfile.Email
FROM usr.Users INNER JOIN usr.UsersProfile ON usr.Users.Id = usr.UsersProfile.UserId
```

`ON` evaluates for each candidate pair under the same statement MVCC snapshot
for both inputs. Only TRUE matches; FALSE and UNKNOWN do not. Matching pairs
preserve multiplicity, and an empty input produces no joined rows. The result
composes with scalar projections, `WHERE`, `DISTINCT`, `GROUP BY`, `HAVING`,
aggregates, `ORDER BY`, `LIMIT`, and `OFFSET`. Without `ORDER BY`, row order is unspecified;
callers requiring stable paging must provide a sufficient ordering key.

Column references can be unqualified, table/alias-qualified, or schema/table-qualified.
An unqualified name present in both inputs is an ambiguous-column error, even
when the inputs have no rows. Explicit aliases distinguish repeated table inputs.
Unqualified `*` expands both inputs in FROM-then-JOIN column order. Qualified
wildcards such as `u.*` and `usr.Users.*` are outside the executable subset and
report `COHDBL001`.

The planner uses a secondary index on either input when mandatory `ON` equalities
between columns bind a leading index-key prefix. It prefers the longest prefix,
then a unique index, then index name, then the right input. It still evaluates
the complete `ON` predicate after fetching snapshot-visible candidate rows.
Compatible Boolean, String, Json, Date, Time, `TIMESTAMP` (DateTime),
`TIMESTAMPTZ` (DateTimeOffset), TimeSpan, Guid and exact signed integer/Decimal
key comparisons can use this path; temporal keys encode the identity described
under "Temporal value identity" below. Approximate numeric comparisons retain
scanning because their evaluator equality can be broader than their encoded
index keys. Predicates without a
usable mandatory column equality, including computed keys, keys reachable only
through disjunctions, and inequalities, use the scan fallback, as do joins
without an applicable index.

The fallback buffers the right input once: it performs O(L + R) stored-row reads,
O(L × R) predicate evaluations, and O(R) input buffering for L and R input rows,
in addition to result and sort storage. Index assistance replaces the indexed
input scan with probes; performance still depends on key selectivity and result
multiplicity.

LEFT, RIGHT, and FULL outer joins, CROSS joins, NATURAL joins, USING, absent ON,
comma joins, and joins beyond two tables do not execute and report `COHDBL001`.
Joins involving `INFORMATION_SCHEMA` or `COHESION_SCHEMA` are also excluded.
No outer-join null-extension behavior is advertised. These are explicit
ISO/IEC 9075 subset boundaries, not additional named clauses in the 49-clause count.

## Value comparison (#1029)

Predicates (`=`, `<>`, `<`, `<=`, `>`, `>=`, `BETWEEN`, `IN`, and join
conditions), `ORDER BY`, `DISTINCT`, `GROUP BY`, and `MIN`/`MAX` share one
non-null value comparator. NULL retains SQL three-valued predicate semantics;
grouping combines NULL keys and ascending ordering places NULL first.

- **Binary:** unsigned byte-by-byte lexicographic order. The first differing
  byte decides; a proper prefix sorts before its extension. Equality requires
  identical content and length, including empty values; there is no padding.
- **Numerics:** signed integers and Decimal compare exactly. Float32 promotes
  exactly to Float64; floating pairs preserve their represented IEEE values
  across the entire finite range, including subnormals and adjacent doubles.
  A mixed exact/approximate comparison compares their exact represented values
  without rounding either operand. Thus `1d = 1m`, but `0.1d > 0.1m` (host
  notation), and the next double above `1d` is greater than `1m`.
  The previous decimal-first grouping conversion is **not** acceptable for
  equality: it collapsed adjacent doubles and underflowed tiny values. Refining
  those rounded mixed equalities is necessary for a transitive total order;
  existing exact equalities and sign/range ordering are retained. Numeric hashes
  agree with this equality, including across runtime numeric types.
- **IEEE extension:** all NaNs, regardless of sign or payload, compare equal
  to each other and less than every other non-null numeric value. In particular,
  `NaN = NaN` is TRUE and `NaN <> NaN` is FALSE. The numeric order is
  `NaN < -infinity < finite values < +infinity`; like-signed infinities are
  equal. Positive and negative zero are equal in predicates and form one
  DISTINCT/grouping class. NaN is a value, not NULL. These deliberate rules
  apply to Float32, Float64, and mixed numeric comparisons.
- **Temporal values:** `TIMESTAMP` compares wall-clock ticks and ignores the
  host `DateTimeKind`; `TIMESTAMPTZ` compares instants and ignores the offset.
  See "Temporal value identity" below.
- **Other values:** strings use the effective collation; Boolean orders FALSE
  before TRUE. Supported same-type comparable values retain their runtime
  ordering. Incompatible type families raise a query error.

This implements an ISO/IEC 9075 subset for binary strings and approximate
numerics, with an explicit NaN/infinity extension rather than an SQL-standard
NaN claim. Bind binary/IEEE values as parameters or read stored columns; SQL
fractional/exponent literals still follow the exact Decimal literal contract,
and there are no NaN/infinity literals. This changes comparison, not arithmetic,
SUM/AVG accumulation, CAST, storage coercion, or physical key identity.
Floating range predicates and signed-zero equality predicates do not supply
index seek bounds: physical index ordering places NaN last and distinguishes
signed zeros. They are evaluated on scanned rows or as residuals of another
safe index predicate.
Nonzero floating equality may still seek; joins retain their conservative
approximate-numeric scan policy. Mapper restrictions and key exclusions remain
deferred with #1007/#1008.

## Temporal value identity (#1099)

`TIMESTAMP` and `TIMESTAMPTZ` each have exactly one normalization rule.
Comparison (predicates, join conditions, `ORDER BY`, `DISTINCT`, `GROUP BY`,
`MIN`/`MAX`), index keys, seek bounds, and `UNIQUE`/`PRIMARY KEY` enforcement
with its key locks all follow it. A secondary-index seek, a primary-key seek and
a scan therefore return the same rows for every predicate.

| Type | Identity (compared, ordered and keyed by) | Not part of identity | Stored and returned |
|---|---|---|---|
| `TIMESTAMP` (`DateTime`) | Wall-clock ticks | `DateTimeKind` (`Unspecified`, `Utc`, `Local`) | The written kind |
| `TIMESTAMPTZ` (`DateTimeOffset`) | Instant (UTC ticks) | Offset | The written offset |

- `TIMESTAMP` is a timestamp without time zone. Values with equal ticks are equal
  whatever their kind, and no time-zone conversion happens: a `Local` 12:00, a
  `Utc` 12:00 and an `Unspecified` 12:00 are one value, ordered by those ticks.
  A `UNIQUE` or `PRIMARY KEY` column rejects equal ticks that differ only in
  kind.
- `TIMESTAMPTZ` compares by instant, as ISO/IEC 9075 specifies for `TIMESTAMP
  WITH TIME ZONE`: `12:00+00:00`, `15:00+03:00` and `06:30-05:30` are one value,
  and values order by instant, never by local wall-clock time. A `UNIQUE` or
  `PRIMARY KEY` column rejects the same instant at another offset.
- Rows keep what was written: a read returns the kind or offset the row was
  inserted or last updated with. Index keys hold only the identity, so updating a
  row to an equal value in another kind or offset does not collide with itself.
- `=`, `<`, `<=`, `>`, `>=` and `BETWEEN` on either type seek an applicable
  index (alone, or after an equality prefix of a composite key), and two-table
  joins on equal temporal types can use index assistance.
- Parameters keep their kind and offset across the wire protocol, so these
  rules hold identically in-process and through `SqlDatabaseServer` and
  `Sql.Client`.

**Indexes written before this rule.** The rule changes the on-disk key format:
data-storage format 3 (written through 10.0.0-preview.1) stored the kind and
the offset inside index keys, and format 4 does not. Format 5 (#1194) keeps
format 4's rows and key encoding but stores index trees in B-tree page format 2,
whose entries are ordered by key, entry reference and writer, so a format-4
database is refused too. Format 6 (#1241) keeps format 5's rows and index trees
but records each table's dropped-column positions in the catalog and decodes
rows through them, so a format-5 database is refused too. The engine has no
upgrade path (#1152). It refuses to
open a database on any format but 6 — older or newer — with an error that names
the database, the format it found and the format it supports. The check reads
only the database's catalog and runs before the engine opens the data files, so
a refused open never touches them. A cleanly
closed database is left byte-identical. A crashed one keeps its journals: the
catalog files get only the storage layer's own crash recovery, which does not
depend on the format. To move an older database to the current engine, export
its data with the engine that wrote it (which can still open it), drop the
database (`DropDatabaseAsync`), create it again and reload the data. A database
whose creation was interrupted before its format was recorded reads as format 1
and needs only the drop and create. Upgrading databases across format versions
is tracked by #1152. New databases are created on format 6.

From format 4 on, the catalog stores the format marker in a record that engines
before format 4 do not recognize, so those engines refuse to open a format-4
database instead of writing index keys that this rule's seeks and `UNIQUE`
checks would miss.

This closes the key-identity gap behind the mapper's `COHMAP003` key exclusion;
the mapper's own review stays with #1008. No external conformance suite applies;
the rule is the declared dialect contract above.

## Arithmetic and numeric faults (#1069)

`+`, `-`, `*`, `/`, and `%` propagate NULL. For non-null operands the operand
families choose the computation:

| Operands | Computation | Result type |
|---|---|---|
| Both exact signed integers (`TINYINT`, `SMALLINT`, `INT`, `BIGINT`, integer literals) | 64-bit signed, overflow-checked | `Int64` |
| Any `DECIMAL`, `REAL`, or `DOUBLE` operand | Both operands promoted to `System.Decimal` | `Decimal` |

Integer `/` truncates toward zero and `%` takes the dividend's sign
(`-7 / 2 = -3`, `-7 % 3 = -1`). Narrower integers compute in BIGINT, so
`2147483647 + 1` is the exact BIGINT `2147483648`, not a fault. `x % -1` is 0
for every integer, including the BIGINT minimum. Approximate operands enter
Decimal arithmetic through the runtime floating-point conversion, as before
(about 7 significant digits for REAL and 15 for DOUBLE). A nonzero REAL or
DOUBLE smaller in magnitude than Decimal's smallest step (1e-28) converts to 0
like any other digit the conversion drops, so `@tiny + 1` is `1`. As a divisor
it has no Decimal quotient and reports `COHSQLE002`, not a division by zero.

An arithmetic fault fails the statement. No operator result wraps, saturates, or
becomes NULL. A sign applied to a value that is not a number is a fault of the
same kind, with its own code:

| Code | ISO SQLSTATE | Raised when |
|---|---|---|
| `COHSQLE001` | 22012, division by zero | The right operand of `/` or `%` is zero: integer, decimal, or approximate (either signed zero), whether it is a literal, column, computed value, or bound parameter. |
| `COHSQLE002` | 22003, numeric value out of range | A BIGINT result leaves -9223372036854775808..9223372036854775807, including `-x`, `ABS(x)`, and `x / -1` for the minimum; a Decimal result exceeds `System.Decimal`; a REAL or DOUBLE operand is NaN, infinite, or beyond Decimal's range, or is a nonzero divisor below Decimal's smallest step; a numeric literal does not fit its type (see Literals); `SUM` or `AVG` overflows or meets a non-finite value; a value does not fit the common type of `CASE`/`COALESCE` branches; or a value does not fit the integer or `DECIMAL` column it is stored into. |
| `COHSQLE003` | 42804, datatype mismatch | The operand of unary `+` or `-` is not a number: `+'abc'`, `-TRUE`, `+name` on a string column. Raised while planning when the operand's type is already known or the operand is a parameter bound to a non-number, so it fails over an empty table too, and while evaluating when only a row's value reveals it. See Signs under [Statement completeness](#statement-completeness-1068). |

The code leads the message, for example
`COHSQLE001: Division by zero: the right operand of '/' is zero.` In process the
statement throws a `DatabaseException` with that message. Over the wire the server
reports `ExecutionFailure` with the same text, and the SQL client raises
`SqlClientException` with `Kind = ExecutionFailure` and `ConnectionUsable = true`.
An evaluation fault never terminates a session. That covers the coded faults above
and uncoded query errors raised while rows are evaluated, such as `ORDER BY` keys
the value order cannot compare (`Cannot compare values of types ...`).

- **Atomicity:** every expression of a DML statement, including CHECK
  constraints, is evaluated before its first write, so a faulting `INSERT`,
  `UPDATE`, `DELETE`, or `INSERT ... SELECT` applies nothing, not even rows
  evaluated before the faulting one. `ALTER TABLE ... ADD CONSTRAINT ... CHECK`
  that faults on an existing row publishes no constraint.
- **Transactions:** an auto-commit statement rolls back. Inside `BEGIN`, the fault
  follows the rule for any failed statement: the transaction stays active with its
  earlier statements' work, and both `COMMIT` and `ROLLBACK` remain available.
- **Evaluation point:** faults are raised where an expression is evaluated. A
  predicate over an empty table evaluates nothing and raises nothing. The one
  exception is `COHSQLE003` for a sign whose operand type the plan already knows,
  or whose operand is a parameter bound to a non-number, which is raised while
  planning. A constant
  comparand that faults during index-seek planning is not used for the seek, and
  the scan faults on the first row that evaluates it. `LIMIT` and `OFFSET`
  expressions are evaluated during planning and fault there.
- **Short-circuit:** `AND` and `OR` evaluate their left operand first and skip the
  right one when the left decides the result (`FALSE AND x`, `TRUE OR x`). A guard
  such as `d <> 0 AND x / d > 1` therefore never divides by zero; a guard written
  after the division, or a left operand that is NULL, does not protect it. A chain
  of terms (`a AND b AND c ...`, one node of any length since #1151) runs its terms
  first to last and stops at the first that decides the result, which is the order
  and the stopping point of the nested operators it replaces, so a guard anywhere
  before a division protects it. `CASE`
  evaluates only the selected branch, `COALESCE` stops at the first non-NULL
  argument, and `IN` stops at the first list value that matches. Every other
  operator and function evaluates all of its operands.
- **Store assignment:** a value an integer or `DECIMAL` column cannot hold, such as
  `UPDATE t SET i = i + 1` on an `INT` at 2147483647, reports `COHSQLE002`
  (`value '2147483648' does not fit column 'i' of type Int32.`). A value that is not
  a number at all keeps the uncoded `Cannot convert value ...` error, and a REAL
  column stores a finite DOUBLE beyond REAL's range as an infinity.
- **CAST:** arithmetic inside a CAST operand keeps its own code
  (`CAST(1 / 0 AS INT)` reports `COHSQLE001`). A value or literal the target cannot
  represent remains a CAST failure under the conversion contract below, with its
  `CAST ... failed` message.

## VALUES and counts have no column scope (#1165)

A row of `INSERT ... VALUES` is evaluated once, against no table row: ISO SQL
forbids a column reference in an INSERT's table value constructor. `LIMIT` and
`OFFSET` counts are evaluated once too, while planning. Their expressions are
literals, parameters, and operators, signs, `CAST`, `COLLATE`, `CASE`, predicates
and scalar functions over them. A column reference anywhere in one of them,
qualified or not and however deeply nested, reports `COHSQLE005` (ISO SQLSTATE
class 42, syntax error or access rule violation) while planning, so nothing
executes:

```text
COHSQLE005: Column reference 'id' is not allowed in INSERT ... VALUES, which has no columns in scope. Use literals, parameters and expressions over them, or INSERT ... SELECT to read values from a table.
```

For a count the clause is `LIMIT` or `OFFSET` and the message ends after
"expressions over them.". Every row is checked before the first is written, so
`VALUES (1, 1), (2, id)` inserts nothing. In process the statement throws a
`DatabaseException` with that message; over the wire it is `ExecutionFailure` with
the same text, the session stays ready, and an explicit transaction stays active.
`INSERT INTO u (id, a) VALUES (id, 1)` used to resolve `id` against the target table
and fail during evaluation with a runtime error that ended a wire session with
`Internal`. A count with a column reference reported `Unknown column` without a
code, or, inside a subquery, came back as an error result carrying `COHDBL001`'s
correlated-subquery diagnosis instead of throwing (`ExecutionFailure` on the wire
either way). A subquery's count follows the same rule as the top level's at every
nesting level, whether the reference names the subquery's own columns or the outer
query's: ISO SQL's fetch-first count is a simple value specification, so no column
of any scope is visible to it. An explicit outer qualifier there, as in
`(SELECT id FROM w LIMIT v.a)`, is still the parser's `COHDBL001`, reported before
planning. To insert values read from a table, use `INSERT ... SELECT`.

The same clauses reject an aggregate (`Aggregate functions are not allowed in
INSERT ... VALUES.`, and likewise for `LIMIT` and `OFFSET`) and a bare `*` (`'*' is
not allowed in INSERT ... VALUES.`, likewise for `LIMIT` and `OFFSET`, where it used
to report `Expression 'SqlStarExpression' is not supported by the executor yet.`)
while planning, and a sign over an operand the plan already knows is not a number
reports `COHSQLE003` there too. A column reference is reported first, so
`VALUES (SUM(a))` and `VALUES (-a)` name the column. `DEFAULT` as a VALUES item is
not in the dialect: it parses as a column reference named `DEFAULT` and reports
`COHSQLE005`. Omit the column from the column list to store its default.

`CURRENT_DATE`, `CURRENT_TIME` and `CURRENT_TIMESTAMP` take no parentheses, so the
parser reads each as a name. In these clauses an unqualified name spelled as one of
them is the recognized function, not a column, and reports
`Function 'CURRENT_TIMESTAMP' is not supported by the executor yet.` while
planning, the message `NOW()` reports during evaluation; nothing executes. The
parser does not keep quoting, so a delimited `"CURRENT_TIMESTAMP"` reports the same
message. Elsewhere the name still binds as a column and reports `Unknown column`
when the source has none.

## Grouping and aggregate functions (#1020)

Grouping is a separate execution stage over a stored table, a virtual system
relation, or the supported two-table `INNER JOIN ... ON`. `WHERE` first filters
individual input rows. `GROUP BY` then forms one row per distinct tuple of one
or more scalar expressions. Aggregate functions consume the surviving rows in
each group; `HAVING` filters the completed groups and retains only TRUE results.
`ORDER BY`, `LIMIT`, and `OFFSET` operate on the resulting groups. This order
applies equally through `SqlDatabaseServer` and the SQL client.

`COUNT(*)`, `COUNT(expr)`, `SUM(expr)`, `AVG(expr)`, `MIN(expr)`, and `MAX(expr)`
work with and without `GROUP BY`, including multiple aggregate projections and
scalar expressions containing aggregate results. A column reference outside
an aggregate must be a grouping expression, or part of an expression derived
from grouped columns. An ungrouped source column in a projection, `HAVING`, or
`ORDER BY` is a planning error, even when the input is empty; the executor never
chooses an arbitrary row's value. Aggregate arguments cannot contain another
aggregate, and aggregates cannot occur in `WHERE`, `JOIN ... ON`, or `GROUP BY`.

`UPDATE` and `DELETE` act on one row at a time, so an aggregate in `UPDATE ... SET`
or in their `WHERE` fails while planning, as PostgreSQL rejects one in those clauses
(SQLSTATE 42803), with `Aggregate functions are not allowed in UPDATE SET.` or
`Aggregate functions are not allowed in WHERE.`. The statement fails
the same way over an empty table as over a populated one and changes no row. Until
the #1189 review it succeeded over an empty table and failed per row over a
populated one, with an uncoded `... is not supported by the executor yet.` message.
A wrong argument count is checked first, so `UPDATE t SET a = SUM()` reports
`COHSQLE006`. (A subquery in either statement is outside the dialect and reports
`COHDBL001` at parse time.)

| Aggregate | NULL and empty-input behavior | Result type |
|---|---|---|
| `COUNT(*)` | Counts every input row, including rows containing only NULLs. Empty input returns zero. | Nonnullable `Int64` |
| `COUNT(expr)` | Counts only non-NULL evaluated arguments. All-NULL or empty input returns zero. | Nonnullable `Int64` |
| `SUM(expr)` | Ignores NULL arguments. All-NULL or empty input returns NULL. | Nullable `Decimal` |
| `AVG(expr)` | Ignores NULL arguments in both the sum and divisor. All-NULL or empty input returns NULL. | Nullable `Decimal`, including integer input |
| `MIN(expr)` / `MAX(expr)` | Ignore NULL arguments. All-NULL or empty input returns NULL. | Nullable argument type |

An ungrouped aggregate has one implicit group, so an empty input produces one
row containing the zero/NULL results above, subject to `HAVING` and pagination.
An explicit `GROUP BY` over empty input produces no groups and no result rows.
NULL grouping keys compare equal for grouping. String grouping equality and
hashing use the same effective collation. Binary groups keep `Alice` and `alice`
distinct; case-insensitive groups combine them while preserving a member's
original spelling in the result.

`SUM` and `AVG` accept signed integers, Decimal, Float32, and Float64. Numeric
arguments are converted to `System.Decimal` before accumulation; approximate
inputs follow the runtime floating-point-to-Decimal conversion. Non-numeric
arguments, non-finite approximate values, and numeric overflow are errors;
the last two report `COHSQLE002` (see the arithmetic fault contract above).
`SUM` always returns Decimal. `AVG` divides the Decimal sum by the non-NULL
Int64 count using `System.Decimal` division: the nearest representable Decimal,
with midpoint ties rounded to even and up to 28 fractional digits. Thus integer
inputs 2 and 5 have Decimal average 3.5; integer truncation is never used.
Neither operation promises an arbitrary-precision result or fixed output scale.
The in-process and wire result metadata use the same base types as these values.
Numeric `CASE`/`COALESCE` alternatives in grouped projections use a common
numeric result type; incompatible nonnumeric alternatives are planning errors.

Aggregate expressions, output aliases (including inside scalar expressions),
and select-list ordinals can be used as `ORDER BY` keys in grouped/aggregate
queries under the ordering contract above. Aliases in `GROUP BY` or `HAVING`
remain outside this subset; use the source/grouping expression or repeat the
aggregate. GROUP BY ordinals report `COHDBL001`.

`DISTINCT` and explicit `ALL` inside a call, aggregate `FILTER`, `ORDER BY` inside a
call, empty grouping sets (`GROUP BY ()`), `GROUPING SETS`, `ROLLUP`, `CUBE`, `GROUPING`/`GROUPING_ID`, window
functions and clauses, and ordered-set `WITHIN GROUP` aggregates are excluded
and report `COHDBL001`. Which names are aggregates is the engine's function catalog to
say, so the parser reports `DISTINCT`, `ALL` and `ORDER BY` inside any call, an
application's aggregate included; PostgreSQL refuses them on a function that is not an
aggregate. Top-level `SELECT DISTINCT` remains available. These
boundaries do not add named clauses to the 49-clause denominator.

## Subqueries and query-source inserts (#1021)

Subqueries are separate planned queries whose results are materialized before
the containing relational plan executes. Supported forms are uncorrelated
`IN (SELECT ...)`, `NOT IN (SELECT ...)`, `EXISTS (SELECT ...)`,
`NOT EXISTS (SELECT ...)`, and scalar `(SELECT ...)` expressions. They compose
with the supported SELECT projection, WHERE, INNER JOIN/ON, GROUP BY/HAVING,
ORDER BY, LIMIT, and OFFSET surface, including nested queries and wire execution.
Each SELECT still requires a FROM relation. Scalar and IN queries must return
exactly one column; EXISTS accepts any valid select list.

| Form | Result semantics |
|---|---|
| `IN` | TRUE for an equal non-NULL member. Without a match, a NULL member or a NULL operand against a nonempty result yields UNKNOWN. Empty results produce FALSE, even for a NULL operand. |
| `NOT IN` | FALSE for an equal non-NULL member. A NULL-containing result produces UNKNOWN for otherwise nonmatching operands, so WHERE retains no rows. An empty result produces TRUE, including for a NULL operand. |
| `EXISTS` / `NOT EXISTS` | Test whether the result has any rows; projected NULLs count as rows. Empty results produce FALSE / TRUE. |
| Scalar subquery | One row supplies its value, no rows supply NULL, and more than one row raises a cardinality error. An arbitrary first row is never selected. |

Every child query uses the containing statement's MVCC snapshot and transaction
context; it never creates another read view. Inner and outer stored rows
therefore observe the same instant. A child that reads a virtual system relation
uses the statement's catalog snapshot. Materialization retains the output type
and collation, including NULL and empty scalar results.

`INSERT ... SELECT` maps the result columns to the specified destination list,
or to all destination columns when the list is omitted. Planning validates the
column count and declared type compatibility even when the source returns no
rows. Destination coercion and the literal-insert path enforce defaults for
omitted columns, nullability, CHECK, primary/unique keys, and foreign keys for
every selected row. Any failure leaves none of the statement's rows inserted.
Reading from the destination table is supported: the entire source is
materialized under the statement snapshot before inserts begin, so newly
inserted rows cannot feed back into the source query.

Correlation is excluded with `COHDBL001`: a nested column must bind to that
query's local FROM/JOIN scope. The parser diagnoses explicit outer qualifiers;
catalog binding rejects unresolved local columns, including unqualified outer
references. A column reference in a subquery's `LIMIT` or `OFFSET` that the parser
lets through, outer or local, reports `COHSQLE005` instead, because counts have no
column scope (#1165; see [VALUES and counts have no column scope](#values-and-counts-have-no-column-scope-1165)).
Local aliases may shadow outer aliases. Query nesting permits at
most **32 expression-subquery levels** below the top-level SELECT (or INSERT's
source SELECT); level 33 reports `COHDBL001` before recursive parsing continues.
Each subquery is also one level of the statement's expression tree, and its
clauses nest below it, so the [expression nesting limit](#expression-nesting-limit-1151)
(256 levels by default) bounds the statement as a whole, subqueries included. The
32-level subquery limit is fixed; it does not follow the configured expression limit.

`ANY`/`ALL`/`SOME` quantified comparisons, derived tables in FROM or JOIN, CTEs,
lateral joins, and subqueries in UPDATE/DELETE, INSERT VALUES, CHECK/DEFAULT,
or LIMIT/OFFSET expressions remain excluded with `COHDBL001`. Ordinary numeric
LIMIT/OFFSET clauses on queries containing subqueries do execute. CHECK remains
a deterministic row-local expression and still rejects every subquery form.
These subset boundaries do not add named clauses to the 49-clause denominator.

## Expression nesting limit (#1151)

ISO SQL leaves nesting limits implementation-defined. The dialect takes a middle
course between SQL Server, which fixes small limits on particular constructs, and
PostgreSQL, which bounds only the stack (owner decision of 2026-10-01): it counts
**genuine nesting only**, against **one documented, configurable limit**, and keeps
a **stack check as the backstop** for whatever the limit admits.

- **The limit.** An expression nests at most **256 levels** by default. An engine
  sets its own within **32..4096** (`SqlDatabaseEngineOptions.ExpressionNestingLimit`,
  or `ExpressionNestingLimit` on the engine builder) and refuses a value outside that
  range with `ArgumentOutOfRangeException` when it is created. A parser takes the
  limit from `SqlQueryParserOptions.ExpressionNestingLimit` and checks the range when
  it is constructed; any other parser options, or none, give the default.
- **Tree depth counts.** Every node of the expression tree is a level: an
  arithmetic, concatenation or comparison operator, a predicate (`IS NULL`,
  `BETWEEN`, `IN`, `LIKE`), `NOT`, a sign, `CASE`, `CAST`, `COLLATE`, a function call
  (its arguments sit one level below it), an `IN` list (its values sit one level
  below it), a subquery, and the leaf at the bottom (a literal, column, parameter or
  `*`, the `*` of `COUNT(*)` included). Depth is measured on the tree, so an
  arithmetic chain counts every link: `1 + 1 + ... + 1` with N terms is N levels
  deep, and so are N - 1 signs over a column or N - 1 nested `ABS(...)` calls. A
  subquery's clauses count below the subquery, so the limit spans the whole
  statement: `SELECT (SELECT <255-level expression> FROM u) FROM t` is at the
  default limit.
- **Parenthesis nesting counts.** Grouping parentheses are not nodes, so they add no
  level to the tree, but at most as many pairs as the limit may enclose any point of
  an expression. A subquery's, call's, `IN` list's or `CAST`'s own parentheses are
  part of that construct, not grouping.
- **A flat `AND` or `OR` chain does not.** A run of terms joined by `AND` (or by
  `OR`) at one level is one node however many terms it has, as in PostgreSQL:
  `a = 1 OR a = 2 OR ... OR a = 10000` is three levels deep (the `OR`, the
  comparisons, their operands), and it parses, plans and executes, in process and
  over the wire. A parenthesized chain that opens a chain of the same operator is
  part of it (`(a AND b) AND c` is the chain `a AND b AND c`); one in a later
  position is a nested chain and a level (`a AND (b AND c)`). Evaluation is
  unchanged: the terms run first to last, the first that decides the result ends
  the chain, and three-valued logic gives the result the nested binary operators
  gave (see Short-circuit under [Arithmetic and numeric faults](#arithmetic-and-numeric-faults-1069)).

Deeper text reports `SQL0006` at the token where the limit was crossed: the link of
a chain that made it too deep, the operand that would be one level too deep, the
operator of an `AND`/`OR` chain that would be one level too deep, or the first `(`
past the limit. The message names the limit (`Expression nesting exceeds the
supported limit of 256 levels.`). The rest of the statement is not parsed, so the
parser reports nothing further, and the statement keeps only its command type, so it
never executes: the text-execute seam throws `DatabaseParseException`
(`ParseFailure` on the wire, with the connection still usable), and a typed request
returns an error result carrying the diagnostic. Diagnostics the parser reported
before that point, such as a `COHDBL001` earlier in the statement, are kept, and so
are those of the checks that scan the whole text before parsing: an unterminated
literal or comment, a character outside the dialect, and an unsupported clause.
Before #1151 nothing bounded nesting: a 200,000-term expression overflowed the
stack, which .NET cannot catch, so one statement ended the process, and over the
wire every session of the server with it.

**The engine's limit holds on every seam.** The engine parses statement text with
its own limit, which covers every statement a wire client sends. A typed request was
parsed by its caller, perhaps with a higher limit, so the parser records how deep
each statement nests (`SqlQueryStatement.ExpressionNestingDepth`, the greater of its
deepest tree and its deepest parentheses), and the engine refuses a request that
nests deeper than its limit with an error result carrying `SQL0006`
(`Expression nesting of 300 levels exceeds this engine's limit of 256 levels.`),
exactly what parsing the text itself would have decided. A request over a query the
parser did not return as a statement, such as a subquery taken out of a parsed
statement, has no parser's measure and is held to the depth of its own expression
tree, which is what the engine's walks recurse through. `SqlQueryRequest.FromSql`
parses at the default limit; a caller targeting an engine configured with another
limit passes it through the overload that takes `SqlQueryParserOptions`, so the
typed path accepts exactly what the engine accepts as text.

| Written | Levels | Result under the default limit |
|---|---|---|
| `1 + 1 + ... + 1`, 256 terms | 256 | Parses and executes |
| `1 + 1 + ... + 1`, 257 terms | 257 | `SQL0006` at the 256th `+` |
| `- - ... - a`, 255 signs | 256 | Parses and executes |
| `ABS(ABS(... 1 ...))`, 256 calls | 257 | `SQL0006` at the innermost `1` |
| `((( ... 1 ... )))`, 256 pairs | 1 | Parses and executes |
| `((( ... 1 ... )))`, 257 pairs | 1 | `SQL0006` at the 257th `(` |
| `a = 1 AND a = 2 AND ...`, 10,000 comparisons | 3 | Parses and executes |
| `a = 1 OR (a = 2 OR (... OR (a = 256)))`, 256 comparisons | 257 | `SQL0006` at the 255th `OR` |
| `a IN (1, 2, ..., 10000)` | 2 | Parses and executes |

**Compared with SQL Server and PostgreSQL.**

| | SQL Server | PostgreSQL | This dialect |
|---|---|---|---|
| Long `AND`/`OR` chains | Thousands of terms; an expression holds at most 65,535 identifiers and constants (error 8632) | One n-ary `BoolExpr` per chain, bounded by the stack alone | One n-ary `SqlLogicalExpression` per chain, one level of the limit |
| Fixed construct limits | `CASE` nests at most 10 levels (error 125); subqueries at most 32 | None | Subqueries at most 32 (`COHDBL001`); every other construct only by the expression limit |
| Expression nesting | No documented limit; a statement too deep for the server's stack fails (error 8631) | None beyond `max_stack_depth` (2 MB by default) | A documented limit, 256 by default, configurable within 32..4096 (`SQL0006`) |
| Stack backstop | Error 8631 | `check_stack_depth()`: SQLSTATE 54001, stack depth limit exceeded | Stack checks in the parser and every walker: `COHSQLE004`, SQLSTATE 54001 |

From SQL Server the dialect takes a fixed, documented limit that a client can rely
on and an operator can tune; from PostgreSQL it takes n-ary chains, which make long
generated predicates cheap, and the stack check as the last line of defense rather
than the only one.

**Stack.** The limit bounds how deep a statement nests, not how much stack the
thread that runs it has. Each level of parentheses, call arguments, `CASE` or `CAST`
costs the parser about 1 to 2 KB of stack in a release build, depending on how far
the JIT has optimized it, and about 6 KB in a debug build. In a release build the
deepest text the default limit accepts, 256 pairs of parentheses around 255 nested
calls, parses on a 1 MB thread, and a default .NET thread has more (1.5 MB on
Windows); a debug build needs about 3 MB for it. A higher configured limit admits
text no default thread can parse: 4,000 nested parentheses need several MB. Wherever
the stack runs out first, the statement fails instead of the process. The parser
checks the stack each time it recurses and reports `SQL0007` (the text is within
the dialect and parses on a thread with more stack). An engine reports a statement
whose parse, or any later walk, runs out of stack as `COHSQLE004` (ISO SQLSTATE
54001, statement too complex): a `DatabaseException` in process, `ExecutionFailure`
on the wire, with the session and any open transaction intact as for any failed
statement. A statement within the limit therefore executes or fails with
`COHSQLE004`; it never ends the process.

**Persisted definitions stay openable.** `CREATE TABLE` and `ALTER TABLE` parse
with the engine's limit, so a stored `CHECK` or `DEFAULT` is never deeper than the
limit of the engine that stored it. Its canonical text (see [Persisted definitions
are canonical](#persisted-definitions-are-canonical)) has the same tree, and the
renderer adds at most one pair of parentheses around a node, so the stored text also
nests its parentheses no deeper than that tree. The engine reads persisted
definitions back at the highest limit, 4096, because the limit decides which
statements an engine accepts, not which databases it opens: a definition stored
under one engine's limit opens, and is enforced, under every other, including one
that would refuse the same text as a statement. `SqlExpressionRenderer` refuses a
tree deeper than 4096 levels with `NotSupportedException`, because no parser would
read its text back. An `AND`/`OR` chain renders term by term to the text the nested
binary form rendered (`a AND b AND c`, `a AND (b AND c)`), so canonical text stored
before chains were n-ary reads back to the same chain and renders unchanged. A
database opened on a thread too small to read its deepest definition back fails to
open with an error that says so, that the catalog is not damaged, and to open it on
a thread with a larger stack. A statement that reads a definition back on such a
thread, such as a DDL proving its canonical text, fails with `COHSQLE004` instead.

**The engine checks its stack too.** Every recursive walk over a statement (the
session's system-relation scan, planning, evaluation, CHECK validation, and
binding a persisted definition) checks the stack before it descends, and recurses
once per level of the tree, never once per term of an `AND`/`OR` chain, whose terms
it iterates. A statement that runs any walk out of stack fails with `COHSQLE004` as
described above. `LIKE` matching recurses once per `%` it backtracks through, so
its depth follows the values rather than the text: a match that needs more stack
than the thread has, such as a pattern of 200,000 `%a` segments over a value of
200,000 characters, fails the same way with `COHSQLE004`.

## System-view matrix (C1)

The SQL engine exposes the following virtual relations through ordinary `SELECT`,
including the wire protocol. View names require their schema and are
case-insensitive. This is the MVP subset of ISO 9075-11 column names and type
conventions, not the complete ISO view layouts. Columns below are listed in
`SELECT *` order. No Cohesion-only columns are appended to the ISO relations.

| Relation | Columns, in order | Rows |
|---|---|---|
| `INFORMATION_SCHEMA.TABLES` | `TABLE_CATALOG`, `TABLE_SCHEMA`, `TABLE_NAME`, `TABLE_TYPE` | Stored tables in the current database; `TABLE_TYPE = 'BASE TABLE'` |
| `INFORMATION_SCHEMA.COLUMNS` | `TABLE_CATALOG`, `TABLE_SCHEMA`, `TABLE_NAME`, `COLUMN_NAME`, `ORDINAL_POSITION`, `COLUMN_DEFAULT`, `IS_NULLABLE`, `DATA_TYPE`, `CHARACTER_MAXIMUM_LENGTH`, `CHARACTER_OCTET_LENGTH`, `NUMERIC_PRECISION`, `NUMERIC_PRECISION_RADIX`, `NUMERIC_SCALE`, `DATETIME_PRECISION` | One row per live table column; ordinals start at one and have no gap where a column was dropped |
| `INFORMATION_SCHEMA.TABLE_CONSTRAINTS` | `CONSTRAINT_CATALOG`, `CONSTRAINT_SCHEMA`, `CONSTRAINT_NAME`, `TABLE_CATALOG`, `TABLE_SCHEMA`, `TABLE_NAME`, `CONSTRAINT_TYPE`, `IS_DEFERRABLE`, `INITIALLY_DEFERRED` | Primary keys, unique indexes/constraints, foreign keys, and explicit checks; both deferral fields are `NO` |
| `INFORMATION_SCHEMA.KEY_COLUMN_USAGE` | `CONSTRAINT_CATALOG`, `CONSTRAINT_SCHEMA`, `CONSTRAINT_NAME`, `TABLE_CATALOG`, `TABLE_SCHEMA`, `TABLE_NAME`, `COLUMN_NAME`, `ORDINAL_POSITION` | One row per primary, unique, or foreign-key column, in constraint order |
| `INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS` | `CONSTRAINT_CATALOG`, `CONSTRAINT_SCHEMA`, `CONSTRAINT_NAME`, `UNIQUE_CONSTRAINT_CATALOG`, `UNIQUE_CONSTRAINT_SCHEMA`, `UNIQUE_CONSTRAINT_NAME`, `MATCH_OPTION`, `UPDATE_RULE`, `DELETE_RULE` | Foreign keys with the referenced key identity; `MATCH_OPTION = 'NONE'`, `UPDATE_RULE = 'RESTRICT'`, delete rule `RESTRICT` or `CASCADE` |
| `INFORMATION_SCHEMA.CHECK_CONSTRAINTS` | `CONSTRAINT_CATALOG`, `CONSTRAINT_SCHEMA`, `CONSTRAINT_NAME`, `CHECK_CLAUSE` | Explicit checks; `CHECK_CLAUSE` is the persisted canonical predicate (see [Persisted definitions are canonical](#persisted-definitions-are-canonical)) |
| `COHESION_SCHEMA.INDEXES` | `TABLE_CATALOG`, `TABLE_SCHEMA`, `TABLE_NAME`, `INDEX_NAME`, `COLUMN_NAME`, `ORDINAL_POSITION`, `IS_UNIQUE`, `IS_PRIMARY_KEY` | Cohesion extension: one row per index key column |
| `COHESION_SCHEMA.OBJECT_OWNERSHIP` | `TABLE_CATALOG`, `TABLE_SCHEMA`, `TABLE_NAME`, `OBJECT_TYPE`, `OBJECT_NAME`, `OWNER`, `OWNING_SCHEMA` | Cohesion extension: one row per table or index; `OWNER` is `Adhoc` or `Schema` |
| `COHESION_SCHEMA.FUNCTIONS` | `FUNCTION_NAME`, `FUNCTION_KIND`, `PARAMETER_TYPES`, `PARAMETER_COUNT`, `RETURN_TYPE`, `VOLATILITY`, `NULL_BEHAVIOR`, `IS_BUILT_IN` | Cohesion extension (E2): one row per function overload of the engine's catalog, built-ins first in registration order, then one row per special form. `FUNCTION_KIND` is `SCALAR`, `AGGREGATE` or `SPECIAL FORM`; `PARAMETER_TYPES` lists the parameter types (`TEXT ...` for a variadic tail, `*` for an aggregate called as `name(*)`, empty for none); `VOLATILITY` is `IMMUTABLE`, `STABLE` or `VOLATILE` for a scalar and NULL for an aggregate, which is never folded or admitted in a CHECK; `NULL_BEHAVIOR` is `RETURNS NULL ON NULL INPUT` or `CALLED ON NULL INPUT`. A special form's type, count, volatility and NULL-behavior columns are NULL. The rows are the engine's, the same in every database of it |

Identifier, descriptive, expression, and `YES`/`NO` columns have shared type
`String`. Ordinals, lengths, precision, radix, and scale have shared type `Int64`;
their values are nonnegative, with one-based ordinals. `IS_NULLABLE`,
`IS_DEFERRABLE`, `INITIALLY_DEFERRED`, `IS_UNIQUE`, and `IS_PRIMARY_KEY` use
strings `YES`/`NO`. `OBJECT_TYPE` is `TABLE` or `INDEX`; `OWNING_SCHEMA` is the
compiled schema name, distinct from the SQL namespace in `TABLE_SCHEMA`, and is
null for ad-hoc objects. Every non-null catalog-name column names the current
database.

`DATA_TYPE` uses canonical names for catalog type identities; it does not retain
the original alias spelling. For example, `INT`/`INTEGER` report `INTEGER`,
`DECIMAL`/`NUMERIC` report `NUMERIC`, and `CHAR`/`VARCHAR`/`TEXT` report
`CHARACTER VARYING`. The remaining canonical names are `BOOLEAN`, `TINYINT`,
`SMALLINT`, `BIGINT`, `REAL`, `DOUBLE PRECISION`, `BINARY VARYING`, `DATE`,
`TIME`, `TIMESTAMP`, `TIMESTAMP WITH TIME ZONE`, `INTERVAL`, `UUID`, `JSON`, and
`JSONB`. Type parameters appear separately where catalog metadata retains them.
Unknown or inapplicable values are null; `CHARACTER_OCTET_LENGTH` is null because
the catalog stores no character-set byte bound. `COLUMN_DEFAULT` is the persisted
canonical literal as declared, including escaped quotes for string defaults (`DEFAULT
'it''s'` reports `'it''s'`, `DEFAULT +5` reports `5`), or null when absent.

Projection, aliases, parameters, `WHERE`, `ORDER BY`, `DISTINCT`, aggregation,
`LIMIT`, and `OFFSET` follow the engine's existing SELECT surface.
The supported subsets above apply equally to these relations. A client
can issue, for example:

```sql
SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME = 'orders'
ORDER BY ORDINAL_POSITION;
```

All relations are read-only. DML and supported table/index DDL targeting them,
including colliding `CREATE TABLE` statements and `IF [NOT] EXISTS` forms, fail
with `System view '<SCHEMA>.<VIEW>' is read-only.` The wire error code is
`ExecutionFailure`. User-defined `CREATE VIEW` / `DROP VIEW` remain unsupported.
The snapshot rules, ISO-subset limitations, and the rationale for the two
extension views are recorded in the
[SQL engine design](../../Assimalign.Cohesion.Database.Sql/docs/DESIGN.md#virtual-system-relations-c1).

## Expressions

Precedence, low to high: `OR` < `AND` < `NOT` < comparison (`=`, `<>`, `<`, `>`,
`<=`, `>=`, `IS [NOT] NULL`, `[NOT] BETWEEN`, `[NOT] IN`, `[NOT] LIKE`) < additive
(`+`, `-`, `||`) < multiplicative (`*`, `/`, `%`) < unary signs (`-`, `+`) <
`COLLATE` < primary. Executable primary forms include literals, parameters (`@name`, `$1`),
column references, supported function calls, simple/searched `CASE`, and
parenthesized expressions and `CAST` within the conversion contract below.
Uncorrelated scalar subqueries and subquery predicates execute within the contract above.
SQL aggregates follow the grouping and aggregate contract below. Arithmetic result
types and the `COHSQLE001`/`COHSQLE002`/`COHSQLE003` faults follow the arithmetic
contract above.
`IS` takes only `[NOT] NULL`, and `LIKE` has no `ESCAPE` clause; the other forms
report `SQL0003` (see Statement completeness). An expression nests at most 256
levels by default (configurable per engine within 32..4096), and its parentheses at
most as many pairs; a chain of `AND` (or `OR`) terms is one level however long, and
deeper text reports `SQL0006` (see [Expression nesting limit](#expression-nesting-limit-1151)).

`~` is recognized but not part of the dialect, and is rejected at parse time with
one `COHDBL001` at the operator (#1101). In prefix position (`~a`, bitwise NOT)
the message names the prefix `~` operator. That covers every operand position,
including a rule that takes a primary directly, such as a `LIKE` pattern
(`LIKE ~'x'`) or a column `DEFAULT ~1`, and a run of prefix operators (`~ ~a`) is
one diagnostic. In infix position (`a ~ 'x'`, PostgreSQL's regular-expression
match) it names the infix operator as written. The infix form is recognized
wherever an additive operator may follow an operand, and after a predicate that
completes on its own (`a IS NULL ~ 'x'`, `a IN (1, 2) ~ 'x'`, `a LIKE 'x' ~ 'y'`,
`BETWEEN 1 AND 2 ~ 3`). PostgreSQL's operators built on `~` are one operator each:
`~*`, `!~` and `!~*` (regular-expression match) and `~~`, `~~*`, `!~~` and `!~~*`
(LIKE match). `~` used to parse as bitwise NOT, and the evaluator returned NULL
for a NULL operand and threw per row otherwise. So `SELECT ~NULL;` and `~` over an
empty table succeeded, and an infix `~` fell to the leftover-token check with a
generic message. None of them executes now, on either session seam or over the
wire.

## Literals

Names may be delimited with double quotes, including reserved words, spaces and
literal dots. The parser removes delimiters from identifier values for catalog
binding while retaining their identifier token classification. Schema/table
qualification uses a dot **outside** the delimiters: `"dbo"."order details"` is
qualified, while `"order.details"` names one table. This applies to DML target
columns, expression references, aliases and DDL table/index/constraint names.
Embedded double quotes inside an identifier are outside the current lexer subset;
the mapper diagnoses such retained names before emitting SQL.

| Form | Examples | AST literal type |
|---|---|---|
| String | `'it''s'` (doubled-quote escape) | `String` |
| Integer | `42` | `Integer` |
| Float | `3.14`, `.5`, `1e10` | `Float` |
| Boolean | `TRUE`, `FALSE` | `Boolean` |
| Null | `NULL` | `Null` |

The engine evaluates an integer literal as BIGINT and a fractional or exponent
literal as an exact `System.Decimal`, whatever the target. A literal its type cannot
hold reports `COHSQLE002` and names itself, for example
`integer literal 9223372036854775808 exceeds BIGINT.`: an integer literal above
9223372036854775807 (write `12345678901234567890.0` for a larger exact value), or
a fractional literal Decimal cannot hold exactly, with more than 28 decimal places
or beyond Decimal's range (`1e-30`, `1e40`), even when it is stored into a REAL or
DOUBLE column. Bind such approximate values as parameters. A negated integer
literal is read as one signed literal, so `-9223372036854775808` is the BIGINT
minimum. Inside `CAST` an unrepresentable literal stays a CAST failure.

## Type names (the `SqlTypeNames` table)

| SQL names | Shared type identity |
|---|---|
| `BOOLEAN`, `BOOL` | `Boolean` |
| `TINYINT` | `Int8` |
| `SMALLINT`, `INT2` | `Int16` |
| `INT`, `INTEGER`, `INT4` | `Int32` |
| `BIGINT`, `INT8` | `Int64` |
| `REAL`, `FLOAT4` | `Float32` |
| `FLOAT`, `FLOAT8`, `DOUBLE` | `Float64` |
| `DECIMAL[(p[,s])]`, `NUMERIC[(p[,s])]` | `Decimal` (single argument = precision) |
| `CHAR[(n)]`, `CHARACTER[(n)]`, `VARCHAR[(n)]`, `TEXT` | `String` |

In DDL, `n`, `p` and `s` are unsigned integer literals within the Int32 range;
any other argument, or a third one, reports `SQL0003` at parse time (#1068).
| `BINARY`, `VARBINARY`, `BLOB`, `BYTEA` | `Binary` |
| `DATE` / `TIME` / `TIMESTAMP`, `DATETIME` / `TIMESTAMPTZ` / `INTERVAL` | `Date` / `Time` / `DateTime` / `DateTimeOffset` / `TimeSpan` |
| `UUID`, `GUID` | `Guid` |
| `JSON` / `JSONB` | `Json` / `JsonBinary` |

Coercion rules are an engine concern (planner/executor); the language layer
guarantees only that declared names resolve to shared type identities so every
model orders and stores values identically.

## Explicit conversion (`CAST`, #1022)

Targets resolve through `SqlTypeNames` into `DatabaseTypeInfo`; the executor uses
that shared identity, never a second SQL-name table. All aliases in the type-name
table for Boolean, Int8, Int16, Int32, Int64, Decimal, and String are accepted.
The complete non-null source/target pair set is:

| Evaluated source | Allowed targets |
|---|---|
| Signed `sbyte`, `short`, `int`, `long`, or `decimal` | Any signed integer width, Decimal, String |
| String | Any signed integer width, Decimal, Boolean, String |
| Boolean | Boolean, String |

All other pairs are rejected, including floating-point (`float`/`double`),
unsigned integers, binary, temporal, GUID and JSON targets. Numeric/boolean
conversion is not supported. Sources are evaluated values: SQL integer literals
are Int64 and SQL fractional literals are Decimal; parameters and columns retain
their runtime types. SQL fractional literals permit exponent and leading decimal
point syntax (such as `1e2` and `.5`) but must also fit Decimal exactly; literal
underflow/rounding is rejected before conversion. Insignificant zeros may be
normalized. Identity conversions in the table still enforce target bounds.

- **Text:** surrounding whitespace is trimmed when reading numbers or booleans.
  Numeric text must match `[+-]?[0-9]+(\.[0-9]+)?`; grouping, exponent notation,
  empty strings, and invalid text (including `'abc' AS INT`) are errors. Numeric
  text must be exactly representable as `System.Decimal` before narrowing.
  Boolean text accepts case-insensitive `TRUE`/`FALSE` only. String output uses
  invariant numeric formatting and uppercase `TRUE`/`FALSE`; string-to-string
  preserves the original text, including whitespace.
- **NULL:** returns NULL for every supported target, with the target's result
  type metadata even when every row is null or the result has no rows. Invalid
  target names/modifiers are still errors with a NULL operand.
- **Integers:** signed ranges are -128..127, -32768..32767, -2147483648..2147483647,
  and -9223372036854775808..9223372036854775807. Overflow and nonzero fractional
  parts are errors; there is no rounding, truncation, wrapping, or zero fallback.
- **Decimal:** the implementation uses `System.Decimal` (96-bit coefficient,
  scale 0..28), not arbitrary precision. Bare DECIMAL/NUMERIC has no additional
  bound. `DECIMAL(p)` means scale 0; `DECIMAL(p,s)` requires `1 <= p <= 28` and
  `0 <= s <= p`, `abs(value) < 10^(p-s)`, and no nonzero digits beyond scale s.
  Excess precision/scale errors; trailing fractional zeros may be discarded
  without changing value. Text too precise for the runtime errors before it can
  be rounded. The output does not promise padding to the declared scale.
- **Strings:** all four string aliases permit an optional positive Int32 length
  n; bare aliases have no CAST length limit. Length counts UTF-16 code units.
  Longer results error; CHAR/CHARACTER do not pad, and no alias truncates.
  Integer and Boolean targets take no parameters.
- **Diagnostics and metadata:** unknown names produce parse error `SQL0004`;
  recognized unsupported targets and invalid target
  arguments produce `SQL0005`; malformed CAST syntax produces `SQL0003`. Conversion failures throw `DatabaseException`
  naming CAST and its target, surfaced as query errors over the wire. The AST
  retains full `DatabaseTypeInfo`; in-process result columns expose the base
  `DatabaseType` and conservative nullability. The existing wire/client contract
  exposes the base type, with a matching boxed runtime value. Neither result
  contract has length/precision/scale fields; the client has no nullability field.

This is a deliberate ISO/IEC 9075 subset: approximate numerics and other type
families are excluded; lossy narrowing raises an error, and fixed character
padding is not implemented. CAST in DEFAULT and CHECK is rejected by planning.
CAST values in INSERT/UPDATE are converted before the existing destination-column
storage coercion; nested arithmetic therefore sees the converted numeric value.

## Builtin functions

The profile's function list is lexical vocabulary, not an execution claim and
not part of the 49-clause denominator. What executes is the engine's function catalog
(E2): the standard library below, and the functions the application registered on the
engine builder (`SqlDatabaseEngineBuilder.Functions`), which every database of the engine
calls exactly as it calls the built-ins. Executable built-in scalars are `UPPER`, `LOWER`,
`LENGTH` and `ABS`, beside the special form `COALESCE`; built-in aggregates are `COUNT`,
`SUM`, `AVG`, `MIN` and `MAX`, under the contract below. The planner checks every call
against its function's overloads before it reads a row, the evaluator checks again before
it computes one, and opening a database checks every stored CHECK (#1189). A call outside
them reports `COHSQLE006` (see Function arguments under
[Statement completeness](#statement-completeness-1068)):

| Function | Kind | Arguments | Accepted call forms |
|---|---|---|---|
| `COALESCE` | Scalar | 1 or more | `COALESCE(value [, value ...])` |
| `UPPER`, `LOWER`, `LENGTH` | Scalar | Exactly 1 | `UPPER(value)`, `LOWER(value)`, `LENGTH(value)` |
| `ABS` | Scalar | Exactly 1 | `ABS(numeric)` |
| `COUNT` | Aggregate | Exactly 1, or `*` | `COUNT(*)`, `COUNT(value)` |
| `SUM`, `AVG` | Aggregate | Exactly 1 | `SUM(numeric)`, `AVG(numeric)` |
| `MIN`, `MAX` | Aggregate | Exactly 1 | `MIN(value)`, `MAX(value)` |

`*` is an argument only of `COUNT`: `SUM(*)` and `UPPER(*)` report `COHSQLE006`.
**`COALESCE` takes one or more operands, a recorded deviation from ISO.** ISO/IEC
9075-2's `<case abbreviation>` spells `COALESCE` with two or more value expressions;
PostgreSQL's grammar (`COALESCE '(' expr_list ')'`) accepts one or more, and the
dialect follows PostgreSQL. `COALESCE(x)` is `x`, so accepting it changes no result
and keeps PostgreSQL text running; `COALESCE()` is an error in all three. The
argument-type, result-type and NULL rules of the T1 functions (#1120) extend these
same signatures. `ABS` accepts every numeric
storage type: integer arguments return BIGINT (so `ABS` of the INT minimum is
exact), REAL returns REAL, DOUBLE returns DOUBLE, and DECIMAL returns DECIMAL.
`ABS` of the BIGINT minimum reports `COHSQLE002`.
`COALESCE`, `NULLIF`, `CASE`, `CAST` and `EXTRACT` are grammar, not catalog functions:
`COALESCE` evaluates its operands lazily, so one after the first non-NULL operand never
runs, and an application cannot register a function under any of those names or under a
keyword.

**Registered functions.** An application function has a name (an identifier), parameter
types, a result type, a volatility and a NULL rule. A name and parameter-type list is
registered once, and a name is either scalar or aggregate. A built-in cannot be replaced: an
overload of a built-in's name is accepted only for a number of arguments the built-in does
not take (`upper(TEXT, BIGINT)` beside `UPPER(value)`). A name the profile lists above but
that does not execute yet (`TRIM`, `NOW`, `ROUND`, ...), a type name (`INT`, `DATE`), `ANY`,
`SOME`, `LOCALTIME` and `LOCALTIMESTAMP` cannot be registered. An argument converts to its
parameter's type along INT8 → INT16 → INT32 → INT64 → NUMERIC → DOUBLE, or REAL → DOUBLE;
nothing converts from text, so a DATE or UUID parameter takes a column or a parameter of
its type, not a string literal. Inside a grouping, a grouping key or an aggregate result
has its type for that choice (`describe(COUNT(*))` calls `describe(BIGINT)`). A strict
function (the default) returns NULL over a NULL argument without being called, and a strict
aggregate skips the row. An `IMMUTABLE` call whose arguments are constants or parameters is
computed once, while the statement is planned. A `CHECK` admits only `IMMUTABLE` functions,
so a function registered with the default `VOLATILE` volatility is refused there, naming its
volatility; one returning BOOLEAN is a predicate by itself, and DDL refuses a CHECK that
compares an application function's result with a value of a type it does not compare with,
or uses one that is not a number in arithmetic. What a function throws fails the statement
as `COHSQLE007`, which names it, and so does a result of another type than the function
declares (a type that widens to the declared one is converted); the statement writes
nothing and an explicit transaction stays usable, as for every coded failure. A sign over a
constant is itself a constant, so `ABS(-5)` is computed once too.

**A stored CHECK whose function is gone.** A CHECK stores its SQL text, not the identity of
the functions it calls, so a later engine build may no longer register one, register it
with parameter types that no longer accept the stored call, or with a result type that no
longer fits where the CHECK uses it (a BOOLEAN predicate, a comparison). The database still opens and
its reads proceed; every write that would evaluate the CHECK (an `INSERT`, an `UPDATE`, or a
DDL backfill over existing rows) fails with `COHSQLE009`, naming the constraint, the table and
the call as the CHECK makes it, on a session or connection that stays usable. A `DELETE`, an
unrelated `DROP COLUMN` and `ALTER TABLE ... DROP CONSTRAINT` still run, and dropping the
constraint lets the table's writes through again. An engine that declares the database (its
builder's `AddDatabase`) fails its build with the same code instead, before it accepts work:

```text
COHSQLE009: CHECK constraint 'ck_email' on table 'dbo.customers' calls function 'is_email(TEXT)', which this engine does not register, so the constraint cannot be evaluated and the write is refused. Register the function on the engine's builder (SqlDatabaseEngineBuilder.Functions), or drop the constraint.
```

A call a built-in takes always resolves to it. A stored call of a built-in's name that no
built-in takes (`upper(email, 2)`) can only have called an application's overload of the
name, so it is a function the engine no longer registers, as above; only `COALESCE`'s wrong
arity is the #1189 case under
[Persisted definitions are canonical](#persisted-definitions-are-canonical), which still
fails the open.

A call to a name outside the catalog and the recognized list fails at plan time with
`Unknown function '<name>'.`, before any row is read (#1068). A recognized name
outside the executable set still parses and plans, then fails during evaluation,
so it fails only when a row reaches it; #1103 rejects those names at parse time.
Do not infer that a call works from a supported `SELECT`. Recognized names include aggregates `COUNT`,
`SUM`, `AVG`, `MIN`, `MAX`; null handling `COALESCE`, `NULLIF`; strings `TRIM`,
`LTRIM`, `RTRIM`, `UPPER`, `LOWER`, `SUBSTRING`, `LENGTH`, `REPLACE`, `CONCAT`;
numeric `ABS`, `CEILING`, `FLOOR`, `ROUND`, `POWER`, `SQRT`, `MOD`; date/time
`NOW`, `CURRENT_DATE`, `CURRENT_TIME`, `CURRENT_TIMESTAMP`, `EXTRACT`. Window
function names are lexed but not supported (see the statement matrix).

## Diagnostics

| Code | Severity | Meaning |
|---|---|---|
| `COHDBL001` | Error | Recognized clause, keyword or operator is not supported by the SQL model surface, including prefix and infix `~` (#1101); the message names the construct |
| `SQL0001` | Error | Empty query text |
| `SQL0002` | Error | Unknown command (recognized unsupported clauses use `COHDBL001`) |
| `SQL0003` | Error | Malformed syntax: text after a complete statement or after its terminating `;`; a missing expression, closing token, keyword, name or `VALUES` row; an unterminated string, quoted identifier or block comment; a character outside the dialect, such as `?`, `#`, `^` or a non-ASCII digit, which binds no alias or column (#1101); a numeric literal whose exponent has no digits; an `IS` form other than `[NOT] NULL`; `NOT` after an operand without `BETWEEN`, `IN` or `LIKE`; `LIKE ... ESCAPE`; `LIMIT` after `OFFSET`; type arguments other than one or two unsigned integer literals; an incomplete `IF [NOT] EXISTS`; `ALTER` without `TABLE`; an unsupported or incomplete `ALTER TABLE` action; and malformed transaction-control, JOIN, GROUP BY, HAVING, CAST, COLLATE, or constraint/DDL syntax |
| `SQL0004` | Error | Unknown CAST target type |
| `SQL0005` | Error | Unsupported CAST target or invalid target parameters |
| `SQL0006` | Error | Expression nesting exceeds the limit (256 levels by default, configurable within 32..4096), or parentheses nest deeper than the limit; an `AND`/`OR` chain counts one level. The rest of the statement is not parsed. An engine also reports it for a typed request whose statement nests deeper than the engine's limit (#1151) |
| `SQL0007` | Error | The statement is within the nesting limit, but the thread parsing it has too little stack left to recurse that deep; the same text parses on a thread with more stack. An engine parsing statement text reports this as `COHSQLE004` (#1151) |
| `SQL0100` | Information | Statement does not end with `;` |
| `COHSQLE001` | Error | Division by zero during evaluation (ISO SQLSTATE 22012) |
| `COHSQLE002` | Error | Numeric value out of range during evaluation or store assignment (ISO SQLSTATE 22003) |
| `COHSQLE003` | Error | Unary `+` or `-` over a non-numeric operand (ISO SQLSTATE 42804) |
| `COHSQLE004` | Error | Statement too complex: parsing it or a walk over it needs more stack than the executing thread has left, which a statement within a high configured nesting limit, a deeply backtracking `LIKE` match or a thread created with a small stack can reach (ISO SQLSTATE 54001, #1151) |
| `COHSQLE005` | Error | Column reference in a clause with no columns in scope: an `INSERT ... VALUES` row or a `LIMIT`/`OFFSET` count, raised while planning (ISO SQLSTATE class 42, #1165) |
| `COHSQLE006` | Error | Function call whose arguments no overload of its function accepts: a count outside every overload's (`ABS(1, 2)`, `UPPER()`, `COALESCE()`, `COUNT(a, b)`, `SUM()`), `*` for a function other than a parameterless aggregate such as `COUNT`, or argument types no overload takes (`SUM(name)` over text). Raised while planning, in every expression position, `CHECK` and `DEFAULT` included; by the evaluator for a call that reaches it unplanned; and by opening a database whose catalog stores a CHECK holding `COALESCE` with no operand (ISO SQLSTATE class 42; PostgreSQL's 42883, undefined function, #1189) |
| `COHSQLE007` | Error | A function threw while the statement ran: anything but a `DatabaseException`, a cancellation or an exhausted stack or memory, which is the inner exception; or it returned a value of another type than it declares (one that widens to the declared type is converted), or NULL when it declares it never returns NULL. The message names the function (ISO SQLSTATE 38000, external routine exception; E2) |
| `COHSQLE008` | Error | A function call that more than one overload accepts equally well, such as an overloaded function over a NULL literal. Raised while planning; a `CAST` on an argument chooses (ISO SQLSTATE 42725, ambiguous function; E2) |
| `COHSQLE009` | Error | A write would evaluate a stored CHECK that calls a function the engine does not register, registers as an aggregate, registers with no overload, or more than one, that accepts the stored call, or registers with a result that no longer fits where the CHECK uses it. The database opens and its reads proceed; the message names the constraint, the table and the call. An engine build that declares the database fails with it (ISO SQLSTATE 42883, undefined function, as PostgreSQL raises at use for a missing implementation; E2, owner decision 65) |

Positions are absolute character offsets into the statement text; line/column
presentation is computed by tooling from the source (offset → line mapping), not
carried per node.

**Keyword disposition (#1101).** Every keyword of the profile, and every entry of
the internal recognized-unsupported table (`SqlUnsupportedVocabulary`), either
parses inside a supported clause or reports exactly one `COHDBL001` naming its
construct. The table holds the clause keywords the preflight scan rejects,
`UNION` through `USING` in the statement matrix. It also holds every other word or
operator outside the profile's keywords that a dedicated rule recognizes and
rejects with `COHDBL001`: `WITH`, `SELECT ALL`, `CREATE`/`DROP VIEW`,
`CREATE COLLATION`, `CREATE FULLTEXT`, `NULLS FIRST`/`LAST`, `GROUPING SETS` and
the `GROUPING`, `ROLLUP`, `CUBE` and `GROUPING_ID` calls, `FILTER (...)` and
`WITHIN GROUP` after a call, the `ANY`/`SOME` (and `ALL`) quantified comparisons,
`LATERAL`, and `~`. Profile keywords that start an unsupported form by another
rule, such as `LEFT` or `CROSS`, need no entry, and function names such as the
window functions are not entries: shared-diagnostics extends the table with them.
A recognized construct is skipped once it is reported, so the text after it adds
no second diagnostic: `= ANY (SELECT ...)` no longer parses as a call to a
function named `ANY`, `LATERAL` is no longer bound as a table name, and `WITHIN`
is no longer read as a column alias. `SqlKeywordDispositionTests` (Sql.Language)
holds one case per word. It fails when a keyword or table entry is added without
one, so the item that adds a word adds its case in the same change. It also fails
when an entry's diagnostic does not name the construct the table records for it.

Plan-time rejections, such as `Unknown column '<name>'.` and
`Unknown function '<name>'.`, are `DatabaseException` messages without a code
(`ExecutionFailure` on the wire). #1103 gives planner rejections structured codes.
While planning, the engine raises `COHSQLE003` for a sign over an operand it knows
is not a number, `COHSQLE005` for a column reference where no columns are in
scope, `COHSQLE006` for a function call whose arguments no overload of its function
accepts, and `COHSQLE008` for one several accept equally well; a `LIMIT`/`OFFSET` count
is evaluated while planning, so its arithmetic
faults (`COHSQLE001`, `COHSQLE002`) surface there too, and `COHSQLE004` can come
from any planner walk.

The `COHSQLE` codes are engine execution diagnostics, not parser diagnostics, and
carry no position. They lead the `DatabaseException` message in process and the
`ExecutionFailure` message on the wire; the arithmetic fault contract above
defines when each of `COHSQLE001`–`COHSQLE003` is raised, the expression
nesting limit when `COHSQLE004` is, the column-scope rule for VALUES and
counts when `COHSQLE005` is, the function-argument and overload rules under Statement
completeness when `COHSQLE006` and `COHSQLE008` are, and the registered-function rules
under Builtin functions when `COHSQLE007` and `COHSQLE009` are. The engine's transaction-state codes,
`COHSQLT001`–`COHSQLT003`, its offline-database code, `COHSQLT004`, and its aborted-transaction
code, `COHSQLT005`, are documented in the SQL engine design.
