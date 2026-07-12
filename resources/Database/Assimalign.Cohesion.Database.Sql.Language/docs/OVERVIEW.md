# Assimalign.Cohesion.Database.Sql.Language — Overview

The SQL language front-end of the Cohesion Data Platform: a recursive-descent
parser (`SqlQueryParser`) producing a full clause-level AST over the shared lexer
infrastructure (`Database.Language`), the declared dialect contract
([DIALECT.md](DIALECT.md)), and the type-name translation table (`SqlTypeNames`)
binding SQL DDL/CAST type names to the shared type system (`Database.Types`).

## Scope

- **Parser** — `SELECT` (joins, grouping, ordering, limits, subqueries), `INSERT`
  (multi-row, `INSERT ... SELECT`), `UPDATE`, `DELETE`, `CREATE/ALTER/DROP TABLE`,
  and a full expression grammar (precedence, `CASE`, `CAST`, predicates,
  parameters). Error-tolerant: malformed input yields diagnostics, never
  exceptions.
- **AST** — sealed statement/expression node families under
  `SqlQueryStatement`/`SqlQueryExpression`; the raw statement text is stamped on
  the root after parsing.
- **Dialect contract** — [DIALECT.md](DIALECT.md) is the authoritative supported /
  recognized / rejected matrix, with the diagnostics table (`SQL0001/0002/0100`).
- **Types and builtins** — `SqlTypeNames` resolves declared type names (with
  length/precision/scale) to `DatabaseType` identities; builtin function names are
  declared in the lexer tables and the dialect doc.

## Dependencies

`Database.Language` (lexer, parser base, diagnostics) and `Database.Types`
(type identities). Consumed by `Database.Sql` (the engine) and, later, the SQL
catalog/planner and SDK schema compiler.

## Usage

```csharp
var parser = new SqlQueryParser();
var statement = (SqlQueryStatement)parser.Parse("SELECT id FROM users WHERE age >= 21;");

var select = (SqlSelectExpression)statement.SqlExpression;
```

See [DESIGN.md](DESIGN.md) for the parser's shape and the decisions behind it.
