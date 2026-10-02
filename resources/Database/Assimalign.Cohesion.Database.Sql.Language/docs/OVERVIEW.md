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
  the root after parsing. An `AND` or `OR` chain is one n-ary
  `SqlLogicalExpression` of any length.
- **Nesting limit** — `SqlQueryParserOptions.ExpressionNestingLimit` (256 by
  default, 32..4096) bounds how deep an expression and its parentheses nest
  (`SQL0006`); a flat `AND`/`OR` chain counts one level. The statement records
  how deep it nests (`SqlQueryStatement.ExpressionNestingDepth`). See
  [DIALECT.md](DIALECT.md#expression-nesting-limit-1151).
- **Dialect contract** — [DIALECT.md](DIALECT.md) is the authoritative supported /
  recognized / rejected matrix. `SqlLanguageProfile.Instance` carries its lexical
  tables and implemented clause set; recognized clauses outside that set report
  `COHDBL001` rather than an undifferentiated parse failure.
- **Types and builtins** — `SqlTypeNames` resolves declared type names (with
  length/precision/scale) to `DatabaseType` identities; builtin function names are
  declared in the lexer tables and the dialect doc.
- **Canonical rendering** — `SqlExpressionRenderer` turns a parsed expression or
  `SELECT` back into canonical SQL text that parses to the same tree. It is the
  form in which the engine persists every stored definition (CHECK, DEFAULT, and
  later expression defaults and views); see
  [DIALECT.md](DIALECT.md#persisted-definitions-are-canonical).

## Dependencies

`Database.Language` (lexer, parser base, diagnostics) and `Database.Types`
(type identities). Consumed by `Database.Sql` (the engine) and, later, the SQL
catalog/planner and SDK schema compiler.

## Usage

```csharp
var parser = new SqlQueryParser();
var statement = (SqlQueryStatement)parser.Parse("SELECT id FROM users WHERE age >= 21;");

var select = (SqlSelectExpression)statement.SqlExpression;

// Canonical text: "age >= 21"
string canonical = SqlExpressionRenderer.Render(select.Where!);

// A stricter nesting limit than the default 256 levels.
var strict = new SqlQueryParser(new SqlQueryParserOptions { ExpressionNestingLimit = 64 });
```

See [DESIGN.md](DESIGN.md) for the parser's shape and the decisions behind it.
