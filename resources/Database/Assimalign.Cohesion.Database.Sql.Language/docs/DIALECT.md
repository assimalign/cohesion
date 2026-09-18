# The Declared SQL Dialect

The contract for the Cohesion SQL surface that executes today. **Supported** means
the documented subset has been measured through a live engine, including correct
results, state changes, or intended semantic errors. Recognized clauses without
execution support report `COHDBL001`; unknown commands report `SQL0002`. Extending
the profile requires updating the parser, this matrix, and the engine's
`SqlLanguageConformanceTests` execution-case table in the same change. That test
enumerates the profile and fails if any advertised clause lacks a passing case.

## Statement matrix

Phase 18 measures **33 of 49 declared clauses** against the live SQL engine.
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

The **16 excluded clauses** are set operations (`UNION`,
`INTERSECT`, `EXCEPT`), CTEs (`WITH`, `RECURSIVE`), window clauses (`WINDOW`,
`OVER`, `PARTITION`), views (`CREATE VIEW`, `DROP VIEW`), `NATURAL`, `USING`,
`TOP`, `ALL`, `FETCH`, and `RETURNING`. Counts describe named clauses, not
complete ISO SQL support; the boundaries below are part of the contract.

| Statement | Status | Notes |
|---|---|---|
| `SELECT` | Supported subset, measured | One stored table or virtual system relation, or a two stored-table `INNER JOIN ... ON`; `DISTINCT`, projections and aliases, scalar expressions, `WHERE`, grouping, multi-expression `ORDER BY ASC/DESC`, nonnegative integer `LIMIT`/`OFFSET`. `COUNT(*)`, `COUNT(expr)`, `SUM`, `AVG`, `MIN`, and `MAX` execute in grouped and ungrouped queries. See the aggregate contract below. `SELECT` without `FROM` is rejected by the planner. |
| `INSERT` / `VALUES` | Supported subset, measured | Optional column list, multi-row literal/scalar `VALUES`, and transactional `INSERT ... SELECT` with the same destination coercion, defaults, and constraints. Subqueries inside `VALUES` are excluded; use `INSERT ... SELECT`. |
| `UPDATE` | Supported | multi-column `SET`, `WHERE` |
| `DELETE` | Supported | optional `WHERE` |
| `CREATE TABLE` | Supported | `IF NOT EXISTS`, column definitions with parameterized types, `COLLATE <name>`, `NOT NULL`/`NULL`, `DEFAULT <literal>`, column and table `PRIMARY KEY`, `REFERENCES`/`FOREIGN KEY`, `CHECK`, and `UNIQUE`; optional `CONSTRAINT <name>` |
| `ALTER TABLE` | Supported subset, measured | ADD/DROP COLUMN and ADD/DROP CONSTRAINT execute. ADD COLUMN without a default preserves old rows with null in the new nullable column; literal defaults apply to subsequent inserts only. Existing rows are **not backfilled** with the literal default. Nonliteral ADD COLUMN defaults are silently discarded for both old and new rows; this form is not supported. These measured default gaps remain MVP work (#1023). |
| `DROP TABLE` | Supported | `IF EXISTS` |
| `CREATE INDEX` | Supported | `CREATE [UNIQUE] INDEX [IF NOT EXISTS] <name> ON <table> (<column> [, ...])` — plain column lists only (no `ASC`/`DESC`, expressions, or `INCLUDE`; each is an additive extension) |
| `DROP INDEX` | Supported | `DROP INDEX [IF EXISTS] <name> ON <table>` — the `ON <table>` qualifier is required: index names are scoped per table |
| `CASE` | Supported, measured | Simple and searched forms, multiple branches, `ELSE`, implicit null result, and row expressions; branch expressions remain limited to the executable scalar subset. |
| `ORDER BY` | Supported subset, measured | Multiple source-column/scalar-expression keys with ASC/DESC execute. Grouped/aggregate queries also accept aggregate expressions and standalone projection aliases. Other projection aliases, including aliases nested inside ordering expressions, are not resolved. Integer keys are evaluated as constants, **not select-list ordinals**; `ORDER BY 1 DESC` does not sort by the first projection. General alias/ordinal ordering remains MVP work (#1024). |
| `CAST` | Supported subset, measured | Exact signed integer, decimal, boolean, and string conversions in projections, predicates, ordering, and DML expressions, including the SQL server/client. See the exact pair and error contract below. CAST in DEFAULT or CHECK remains rejected. |
| `COLLATE` | Supported subset, measured | Column and expression overrides: `binary`, `case_insensitive`, `case_accent_insensitive`, plus compatibility `invariant` with scan execution only. Effective collation governs comparisons, `LIKE`, ordering, grouping, `DISTINCT`, and unique keys. See the collation contract below. |
| `JOIN` | Supported subset, measured | Two stored-table `INNER JOIN ... ON` or bare `JOIN ... ON`, with index assistance where the mandatory equality predicate matches an applicable secondary-index prefix. `LEFT [OUTER]`, `RIGHT [OUTER]`, `FULL [OUTER]`, `CROSS`, additional joins beyond two tables, joins without `ON`, comma joins, and joins of virtual system relations report `COHDBL001`. See the precise contract below. |
| `GROUP BY` / `HAVING` | Supported subset, measured | One or more grouping expressions; `WHERE` filters input rows and `HAVING` filters groups after aggregation. Composes with supported two-table inner joins, `ORDER BY`, `LIMIT`, and `OFFSET`, including server/client execution. Ungrouped, unaggregated projected columns are errors. `DISTINCT`/`ALL` aggregate modifiers, grouping extensions, windows, and ordered-set aggregates report `COHDBL001`. |
| Subqueries / `INSERT ... SELECT` | Supported subset, measured | Uncorrelated scalar, `IN`/`NOT IN`, and `EXISTS`/`NOT EXISTS` queries in supported SELECT expressions, plus transactional insert-source queries. All use the outer statement snapshot; nesting is limited to 32 subquery levels. Correlated queries, derived tables, quantified comparisons, subqueries in UPDATE/DELETE, VALUES, CHECK/DEFAULT, and LIMIT/OFFSET expressions report `COHDBL001`. See the subquery contract below. |
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
ISO/IEC 9075 subset boundaries, not additional named clauses in the 49-clause count.

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
arguments, non-finite approximate values, and numeric overflow are errors.
`SUM` always returns Decimal. `AVG` divides the Decimal sum by the non-NULL
Int64 count using `System.Decimal` division: the nearest representable Decimal,
with midpoint ties rounded to even and up to 28 fractional digits. Thus integer
inputs 2 and 5 have Decimal average 3.5; integer truncation is never used.
Neither operation promises an arbitrary-precision result or fixed output scale.
The in-process and wire result metadata use the same base types as these values.
Numeric `CASE`/`COALESCE` alternatives in grouped projections use a common
numeric result type; incompatible nonnumeric alternatives are planning errors.

Aggregate expressions and standalone output aliases can be used as `ORDER BY`
keys in grouped/aggregate queries. Aliases inside larger ordering expressions,
aliases in `GROUP BY` or `HAVING`, and select-list ordinal ordering are outside
this subset; use the source/grouping expression or repeat the aggregate.

`DISTINCT` and explicit `ALL` inside aggregates, aggregate `FILTER`, in-aggregate
`ORDER BY`, empty grouping sets (`GROUP BY ()`), `GROUPING SETS`, `ROLLUP`, `CUBE`, `GROUPING`/`GROUPING_ID`, window
functions and clauses, and ordered-set `WITHIN GROUP` aggregates are excluded
and report `COHDBL001`. Top-level `SELECT DISTINCT` remains available. These
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
references. Local aliases may shadow outer aliases. Query nesting permits at
most **32 expression-subquery levels** below the top-level SELECT (or INSERT's
source SELECT); level 33 reports `COHDBL001` before recursive parsing continues.

`ANY`/`ALL`/`SOME` quantified comparisons, derived tables in FROM or JOIN, CTEs,
lateral joins, and subqueries in UPDATE/DELETE, INSERT VALUES, CHECK/DEFAULT,
or LIMIT/OFFSET expressions remain excluded with `COHDBL001`. Ordinary numeric
LIMIT/OFFSET clauses on queries containing subqueries do execute. CHECK remains
a deterministic row-local expression and still rejects every subquery form.
These subset boundaries do not add named clauses to the 49-clause denominator.

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
(`+`, `-`, `||`) < multiplicative (`*`, `/`, `%`) < unary (`-`, `~`, `NOT`) <
primary. Executable primary forms include literals, parameters (`@name`, `$1`),
column references, supported function calls, simple/searched `CASE`, and
parenthesized expressions and `CAST` within the conversion contract below.
Uncorrelated scalar subqueries and subquery predicates execute within the contract above.
SQL aggregates follow the grouping and aggregate contract below. `~` is parsed but not evaluated; it is outside the
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
not part of the 49-clause denominator. Executable scalar functions are `COALESCE`,
`UPPER`, `LOWER`, `LENGTH`, and `ABS`; supported aggregates are `COUNT`, `SUM`,
`AVG`, `MIN`, and `MAX`, under the contract below.
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
| `SQL0003` | Error | Malformed transaction-control, JOIN, GROUP BY, HAVING, CAST, COLLATE, or constraint/DDL syntax |
| `SQL0004` | Error | Unknown CAST target type |
| `SQL0005` | Error | Unsupported CAST target or invalid target parameters |
| `SQL0100` | Information | Statement does not end with `;` |

Positions are absolute character offsets into the statement text; line/column
presentation is computed by tooling from the source (offset → line mapping), not
carried per node.
