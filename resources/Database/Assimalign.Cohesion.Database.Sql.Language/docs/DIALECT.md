# The Declared SQL Dialect

The contract for the Cohesion SQL surface that executes today. **Supported** means
the documented subset has been measured through a live engine, including correct
results, state changes, or intended semantic errors. Recognized clauses without
execution support report `COHDBL001`; unknown commands report `SQL0002`. Extending
the profile requires updating the parser, this matrix, and the engine's
`SqlLanguageConformanceTests` execution-case table in the same change. That test
enumerates the profile and fails if any advertised clause lacks a passing case.

## Statement matrix

Phase 15 measures **29 of 48 declared clauses** against the live SQL engine.
The earlier 32/48 figure included `JOIN`, `GROUP BY`, `HAVING`, and `SUBQUERY`,
removed in Phase 12e (#1019–#1021), plus a no-op `CAST` removed in Phase 13.
Phase 14 restores `CAST` with actual conversion, type metadata, and wire execution
coverage (#1022). Phase 15 restores `JOIN` for two stored-table inner joins with
an `ON` predicate, including server/client execution (#1019).

The **19 excluded clauses** are `GROUP BY`, `HAVING`, and `SUBQUERY` plus set operations (`UNION`,
`INTERSECT`, `EXCEPT`), CTEs (`WITH`, `RECURSIVE`), window clauses (`WINDOW`,
`OVER`, `PARTITION`), views (`CREATE VIEW`, `DROP VIEW`), `NATURAL`, `USING`,
`TOP`, `ALL`, `FETCH`, and `RETURNING`. Counts describe named clauses, not
complete ISO SQL support; the boundaries below are part of the contract.

| Statement | Status | Notes |
|---|---|---|
| `SELECT` | Supported subset, measured | One stored table or virtual system relation, or a two stored-table `INNER JOIN ... ON`; `DISTINCT`, projections and aliases, scalar expressions, `WHERE`, multi-expression `ORDER BY ASC/DESC`, nonnegative integer `LIMIT`/`OFFSET`. The only aggregate shape is a lone `COUNT(*)`, optionally aliased and filtered; `COUNT(column)`, `COUNT(DISTINCT ...)`, `SUM`/`AVG`/`MIN`/`MAX`, and mixed/nested aggregate projections do not execute (#1020). `SELECT` without `FROM` is rejected by the planner. |
| `INSERT` / `VALUES` | Supported subset, measured | Optional column list and multi-row literal/scalar `VALUES`; `INSERT ... SELECT` reports `COHDBL001` (#1021). |
| `UPDATE` | Supported | multi-column `SET`, `WHERE` |
| `DELETE` | Supported | optional `WHERE` |
| `CREATE TABLE` | Supported | `IF NOT EXISTS`, column definitions with parameterized types, `NOT NULL`/`NULL`, `DEFAULT <literal>`, column and table `PRIMARY KEY`, `REFERENCES`/`FOREIGN KEY`, `CHECK`, and `UNIQUE`; optional `CONSTRAINT <name>` |
| `ALTER TABLE` | Supported subset, measured | ADD/DROP COLUMN and ADD/DROP CONSTRAINT execute. ADD COLUMN without a default preserves old rows with null in the new nullable column; literal defaults apply to subsequent inserts only. Existing rows are **not backfilled** with the literal default. Nonliteral ADD COLUMN defaults are silently discarded for both old and new rows; this form is not supported. These measured default gaps remain MVP work (#1023). |
| `DROP TABLE` | Supported | `IF EXISTS` |
| `CREATE INDEX` | Supported | `CREATE [UNIQUE] INDEX [IF NOT EXISTS] <name> ON <table> (<column> [, ...])` — plain column lists only (no `ASC`/`DESC`, expressions, or `INCLUDE`; each is an additive extension) |
| `DROP INDEX` | Supported | `DROP INDEX [IF EXISTS] <name> ON <table>` — the `ON <table>` qualifier is required: index names are scoped per table |
| `CASE` | Supported, measured | Simple and searched forms, multiple branches, `ELSE`, implicit null result, and row expressions; branch expressions remain limited to the executable scalar subset. |
| `ORDER BY` | Supported subset, measured | Multiple source-column/scalar-expression keys with ASC/DESC execute. Projection aliases are not resolved and produce an unknown-column error. Integer keys are evaluated as constants, **not select-list ordinals**; `ORDER BY 1 DESC` does not sort by the first projection. Alias/ordinal ordering remains MVP work (#1024). |
| `CAST` | Supported subset, measured | Exact signed integer, decimal, boolean, and string conversions in projections, predicates, ordering, and DML expressions, including the SQL server/client. See the exact pair and error contract below. CAST in DEFAULT or CHECK remains rejected. |
| `JOIN` | Supported subset, measured | Two stored-table `INNER JOIN ... ON` or bare `JOIN ... ON`, with index assistance where the mandatory equality predicate matches an applicable secondary-index prefix. `LEFT [OUTER]`, `RIGHT [OUTER]`, `FULL [OUTER]`, `CROSS`, additional joins beyond two tables, joins without `ON`, comma joins, and joins of virtual system relations report `COHDBL001`. See the precise contract below. |
| `GROUP BY` / `HAVING` | Recognized, not supported | `COHDBL001`; grouping and broader aggregates remain #1020. |
| Subqueries / `INSERT ... SELECT` | Recognized, not supported | Scalar, `IN`/`NOT IN`, `EXISTS`/`NOT EXISTS`, derived-table, correlated, and insert-source queries report `COHDBL001` (#1021). Literal `IN` lists remain supported. |
| `TOP` / `SELECT ALL` / `FETCH` | Recognized, not supported | row-limit and select modifiers rejected with `COHDBL001` |
| DML `RETURNING` | Recognized, not supported | rejected with `COHDBL001` |
| `NATURAL JOIN` / `JOIN ... USING` | Recognized, not supported | rejected with `COHDBL001` |
| `UNION` / `INTERSECT` / `EXCEPT` | Recognized, not supported | keywords lexed; rejected with `COHDBL001` |
| `WITH` / `WITH RECURSIVE` (CTEs) | Recognized, not supported | rejected with `COHDBL001` |
| Window functions / `OVER` / `WINDOW` | Recognized, not supported | function names lexed; clauses rejected with `COHDBL001` |
| `CREATE VIEW` / `DROP VIEW` | Recognized, not supported | rejected with `COHDBL001` |
| `CONSTRAINT` / `FOREIGN KEY` / `REFERENCES` / `CHECK` / `UNIQUE` constraints | Supported subset, measured | Column and table declarations normalize into constraint definitions; UNIQUE lowers to a unique catalog index. NULL is an equal index key: a second NULL violates a single-column UNIQUE constraint. Foreign-key NULL values are allowed; CHECK accepts UNKNOWN and rejects FALSE. CHECK expressions must be deterministic Boolean row expressions with the supported scalar functions; parameters and aggregates are excluded. |
| `ON DELETE CASCADE` / `ON DELETE RESTRICT` | Supported | omitted deletion action defaults to `RESTRICT`; `DROP TABLE ... CASCADE` is not supported |
| `ON UPDATE` | Recognized, not supported | absent from the profile; rejected with `COHDBL001` |
| `BEGIN [TRANSACTION]` / `COMMIT [TRANSACTION]` / `ROLLBACK [TRANSACTION]` | Supported | session-scoped transactions through the existing MVCC coordinator; `TRANSACTION` alone is not a statement |
| `MERGE`, `TRUNCATE`, `GRANT` | Not in the dialect | `SQL0002` |

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
composes with the existing scalar projections, `WHERE`, `DISTINCT`, `ORDER BY`,
`LIMIT`, and `OFFSET` surface. Without `ORDER BY`, row order is unspecified;
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
Compatible Boolean, String, Json, Date, Time, TimeSpan, Guid and exact signed
integer/Decimal key comparisons can use this path. Approximate numeric,
DateTime, and DateTimeOffset comparisons retain scanning because their evaluator
equality can be broader than their encoded index keys. Predicates without a
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
ISO/IEC 9075 subset boundaries, not additional named clauses in the 48-clause count.

## System-view matrix (C1)

The SQL engine exposes the following virtual relations through ordinary `SELECT`,
including the wire protocol. View names require their schema and are
case-insensitive. This is the MVP subset of ISO 9075-11 column names and type
conventions, not the complete ISO view layouts. Columns below are listed in
`SELECT *` order. No Cohesion-only columns are appended to the ISO relations.

| Relation | Columns, in order | Rows |
|---|---|---|
| `INFORMATION_SCHEMA.TABLES` | `TABLE_CATALOG`, `TABLE_SCHEMA`, `TABLE_NAME`, `TABLE_TYPE` | Stored tables in the current database; `TABLE_TYPE = 'BASE TABLE'` |
| `INFORMATION_SCHEMA.COLUMNS` | `TABLE_CATALOG`, `TABLE_SCHEMA`, `TABLE_NAME`, `COLUMN_NAME`, `ORDINAL_POSITION`, `COLUMN_DEFAULT`, `IS_NULLABLE`, `DATA_TYPE`, `CHARACTER_MAXIMUM_LENGTH`, `CHARACTER_OCTET_LENGTH`, `NUMERIC_PRECISION`, `NUMERIC_PRECISION_RADIX`, `NUMERIC_SCALE`, `DATETIME_PRECISION` | One row per table column; ordinals start at one |
| `INFORMATION_SCHEMA.TABLE_CONSTRAINTS` | `CONSTRAINT_CATALOG`, `CONSTRAINT_SCHEMA`, `CONSTRAINT_NAME`, `TABLE_CATALOG`, `TABLE_SCHEMA`, `TABLE_NAME`, `CONSTRAINT_TYPE`, `IS_DEFERRABLE`, `INITIALLY_DEFERRED` | Primary keys, unique indexes/constraints, foreign keys, and explicit checks; both deferral fields are `NO` |
| `INFORMATION_SCHEMA.KEY_COLUMN_USAGE` | `CONSTRAINT_CATALOG`, `CONSTRAINT_SCHEMA`, `CONSTRAINT_NAME`, `TABLE_CATALOG`, `TABLE_SCHEMA`, `TABLE_NAME`, `COLUMN_NAME`, `ORDINAL_POSITION` | One row per primary, unique, or foreign-key column, in constraint order |
| `INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS` | `CONSTRAINT_CATALOG`, `CONSTRAINT_SCHEMA`, `CONSTRAINT_NAME`, `UNIQUE_CONSTRAINT_CATALOG`, `UNIQUE_CONSTRAINT_SCHEMA`, `UNIQUE_CONSTRAINT_NAME`, `MATCH_OPTION`, `UPDATE_RULE`, `DELETE_RULE` | Foreign keys with the referenced key identity; `MATCH_OPTION = 'NONE'`, `UPDATE_RULE = 'RESTRICT'`, delete rule `RESTRICT` or `CASCADE` |
| `INFORMATION_SCHEMA.CHECK_CONSTRAINTS` | `CONSTRAINT_CATALOG`, `CONSTRAINT_SCHEMA`, `CONSTRAINT_NAME`, `CHECK_CLAUSE` | Persisted explicit check expressions |
| `COHESION_SCHEMA.INDEXES` | `TABLE_CATALOG`, `TABLE_SCHEMA`, `TABLE_NAME`, `INDEX_NAME`, `COLUMN_NAME`, `ORDINAL_POSITION`, `IS_UNIQUE`, `IS_PRIMARY_KEY` | Cohesion extension: one row per index key column |
| `COHESION_SCHEMA.OBJECT_OWNERSHIP` | `TABLE_CATALOG`, `TABLE_SCHEMA`, `TABLE_NAME`, `OBJECT_TYPE`, `OBJECT_NAME`, `OWNER`, `OWNING_SCHEMA` | Cohesion extension: one row per table or index; `OWNER` is `Adhoc` or `Schema` |

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
the catalog stores no character-set byte bound. `COLUMN_DEFAULT` is SQL literal
text, including escaped quotes for string defaults, or null when absent.

Projection, aliases, parameters, `WHERE`, `ORDER BY`, `DISTINCT`, a lone
`COUNT(*)`, `LIMIT`, and `OFFSET` follow the engine's existing SELECT surface.
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
(`+`, `-`, `||`) < multiplicative (`*`, `/`, `%`) < unary (`-`, `~`, `NOT`) <
primary. Executable primary forms include literals, parameters (`@name`, `$1`),
column references, supported function calls, simple/searched `CASE`, and
parenthesized expressions and `CAST` within the conversion contract below.
Subqueries retain syntax trees for tooling but carry `COHDBL001` and cannot execute. SQL aggregate support is limited to a
lone `COUNT(*)` projection. `~` is parsed but not evaluated; it is outside the
executable scalar subset.

## Literals

| Form | Examples | AST literal type |
|---|---|---|
| String | `'it''s'` (doubled-quote escape) | `String` |
| Integer | `42` | `Integer` |
| Float | `3.14`, `.5`, `1e10` | `Float` |
| Boolean | `TRUE`, `FALSE` | `Boolean` |
| Null | `NULL` | `Null` |

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
not part of the 48-clause denominator. Executable scalar functions are `COALESCE`,
`UPPER`, `LOWER`, `LENGTH`, and `ABS`; aggregate support is only a lone `COUNT(*)`.
Other parsed calls can still fail in planning/evaluation and must not be inferred
to work from a supported `SELECT`. Recognized names include aggregates `COUNT`,
`SUM`, `AVG`, `MIN`, `MAX`; null handling `COALESCE`, `NULLIF`; strings `TRIM`,
`LTRIM`, `RTRIM`, `UPPER`, `LOWER`, `SUBSTRING`, `LENGTH`, `REPLACE`, `CONCAT`;
numeric `ABS`, `CEILING`, `FLOOR`, `ROUND`, `POWER`, `SQRT`, `MOD`; date/time
`NOW`, `CURRENT_DATE`, `CURRENT_TIME`, `CURRENT_TIMESTAMP`, `EXTRACT`. Window
function names are lexed but not supported (see the statement matrix).

## Diagnostics

| Code | Severity | Meaning |
|---|---|---|
| `COHDBL001` | Error | Recognized clause is not supported by the SQL model surface |
| `SQL0001` | Error | Empty query text |
| `SQL0002` | Error | Unknown command (recognized unsupported clauses use `COHDBL001`) |
| `SQL0003` | Error | Malformed transaction-control, JOIN, CAST, or constraint/DDL syntax |
| `SQL0004` | Error | Unknown CAST target type |
| `SQL0005` | Error | Unsupported CAST target or invalid target parameters |
| `SQL0100` | Information | Statement does not end with `;` |

Positions are absolute character offsets into the statement text; line/column
presentation is computed by tooling from the source (offset → line mapping), not
carried per node.
