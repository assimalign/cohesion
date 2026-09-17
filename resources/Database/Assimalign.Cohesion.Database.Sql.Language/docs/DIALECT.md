# The Declared SQL Dialect

The contract for what the Cohesion SQL language accepts today. Anything not listed
as **Supported** is out of the dialect: the parser either rejects it (`SQL0002`) or
recognizes its tokens without statement support (listed under *Recognized, not
supported* so diagnostics stay precise). The dialect grows deliberately — extending
it means updating the parser, this matrix, and the conformance corpus in the same
change (open/closed: new statement kinds extend the parser's keyword dispatch and
new expression kinds extend the precedence ladder; existing AST shapes stay stable
for planners and tooling).

## Statement matrix

The B1 measurement was **21 of 48 declared clauses**. B2 implements **32 of 48**:
the same 21 plus `CONSTRAINT`, `FOREIGN KEY`, `REFERENCES`, `CHECK`, `UNIQUE`,
`CASCADE`, `RESTRICT`, `BEGIN`, `COMMIT`, `ROLLBACK`, and optional `TRANSACTION`.
The remaining **16** are set operations (`UNION`, `INTERSECT`, `EXCEPT`), CTEs
(`WITH`, `RECURSIVE`), window clauses (`WINDOW`, `OVER`, `PARTITION`), views
(`CREATE VIEW`, `DROP VIEW`), `NATURAL`, `USING`, `TOP`, `ALL`, `FETCH`, and `RETURNING`.

| Statement | Status | Notes |
|---|---|---|
| `SELECT` | Supported | `DISTINCT`, column lists with `AS`/implicit aliases, `FROM` with schema-qualified names + aliases, `INNER/LEFT [OUTER]/RIGHT [OUTER]/FULL [OUTER]/CROSS JOIN ... ON`, `WHERE`, `GROUP BY` (multi), `HAVING`, `ORDER BY ASC/DESC` (multi), `LIMIT`, `OFFSET`, scalar/`IN`/`EXISTS` subqueries |
| `INSERT` | Supported | optional column list, multi-row `VALUES`, `INSERT ... SELECT` |
| `UPDATE` | Supported | multi-column `SET`, `WHERE` |
| `DELETE` | Supported | optional `WHERE` |
| `CREATE TABLE` | Supported | `IF NOT EXISTS`, column definitions with parameterized types, `NOT NULL`/`NULL`, `DEFAULT <literal>`, column and table `PRIMARY KEY`, `REFERENCES`/`FOREIGN KEY`, `CHECK`, and `UNIQUE`; optional `CONSTRAINT <name>` |
| `ALTER TABLE` | Supported | `ADD [COLUMN] <definition>`, `DROP [COLUMN] <name>`, `ADD [CONSTRAINT <name>] <constraint>`, `DROP CONSTRAINT <name>` |
| `DROP TABLE` | Supported | `IF EXISTS` |
| `CREATE INDEX` | Supported | `CREATE [UNIQUE] INDEX [IF NOT EXISTS] <name> ON <table> (<column> [, ...])` — plain column lists only (no `ASC`/`DESC`, expressions, or `INCLUDE`; each is an additive extension) |
| `DROP INDEX` | Supported | `DROP INDEX [IF EXISTS] <name> ON <table>` — the `ON <table>` qualifier is required: index names are scoped per table |
| `TOP` / `SELECT ALL` / `FETCH` | Recognized, not supported | row-limit and select modifiers rejected with `COHDBL001` |
| DML `RETURNING` | Recognized, not supported | rejected with `COHDBL001` |
| `NATURAL JOIN` / `JOIN ... USING` | Recognized, not supported | rejected with `COHDBL001` |
| `UNION` / `INTERSECT` / `EXCEPT` | Recognized, not supported | keywords lexed; rejected with `COHDBL001` |
| `WITH` / `WITH RECURSIVE` (CTEs) | Recognized, not supported | rejected with `COHDBL001` |
| Window functions / `OVER` / `WINDOW` | Recognized, not supported | function names lexed; clauses rejected with `COHDBL001` |
| `CREATE VIEW` / `DROP VIEW` | Recognized, not supported | rejected with `COHDBL001` |
| `CONSTRAINT` / `FOREIGN KEY` / `REFERENCES` / `CHECK` / `UNIQUE` constraints | Supported | column and table declarations normalize into constraint definitions; `UNIQUE` lowers to a unique catalog index |
| `ON DELETE CASCADE` / `ON DELETE RESTRICT` | Supported | omitted deletion action defaults to `RESTRICT`; `DROP TABLE ... CASCADE` is not supported |
| `ON UPDATE` | Recognized, not supported | absent from the profile; rejected with `COHDBL001` |
| `BEGIN [TRANSACTION]` / `COMMIT [TRANSACTION]` / `ROLLBACK [TRANSACTION]` | Supported | session-scoped transactions through the existing MVCC coordinator; `TRANSACTION` alone is not a statement |
| `MERGE`, `TRUNCATE`, `GRANT` | Not in the dialect | `SQL0002` |

## Expressions

Precedence, low to high: `OR` < `AND` < `NOT` < comparison (`=`, `<>`, `<`, `>`,
`<=`, `>=`, `IS [NOT] NULL`, `[NOT] BETWEEN`, `[NOT] IN`, `[NOT] LIKE`) < additive
(`+`, `-`, `||`) < multiplicative (`*`, `/`, `%`) < unary (`-`, `~`, `NOT`) <
primary. Primary forms: literals, parameters (`@name`, `$1`), column references up
to `schema.table.column`, function calls (including `COUNT(*)`), `CASE` (simple and
searched), `CAST(x AS TYPE[(args)])`, parenthesized expressions, subqueries.

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

## Builtin functions

Parsed as function calls today; evaluation support lands with the executor and is
tracked per function there. Declared names: aggregates `COUNT` (incl. `COUNT(*)`),
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
| `SQL0003` | Error | Malformed transaction-control or constraint/DDL syntax |
| `SQL0100` | Information | Statement does not end with `;` |

Positions are absolute character offsets into the statement text; line/column
presentation is computed by tooling from the source (offset → line mapping), not
carried per node.
