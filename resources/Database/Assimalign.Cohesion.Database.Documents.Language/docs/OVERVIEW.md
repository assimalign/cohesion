# Assimalign.Cohesion.Database.Documents.Language

This package parses the Documents engine's executable OQL surface into typed statement and
expression trees. It provides `OqlLanguageProfile`, stable `OqlClauses` names, `OqlQueryParser`,
`OqlQueryStatement`,
`OqlSelectExpression`, `OqlCreateIndexExpression`, `OqlDropIndexExpression`, and expression nodes
for projection, filtering, grouping, aggregates, ordering, nested properties, and array access.

```csharp
var parser = new OqlQueryParser();
var statement = (OqlQueryStatement)parser.Parse(
    "SELECT p.name, p.orders[0].total AS total FROM people p WHERE p.age >= $minimum ORDER BY p.name");

// Check statement.Diagnostics for errors before passing its AST to planning.
var query = statement.OqlExpression;
```

The package references only the shared `Assimalign.Cohesion.Database.Language` package. The
Documents engine supplies catalog binding, planning, and execution. Queries and index DDL address
one collection in the session's database. `CREATE INDEX` and `DROP INDEX` use the same
parse-plan-execute path as `SELECT`; document data mutations use the existing collection API.
`DEFINE`, `ELEMENT`, `FLATTEN`, and subqueries are reserved but unsupported and produce
`COHDBL001`.

See [DESIGN.md](DESIGN.md) for the supported-clause matrix, statement/expression grammar,
diagnostics, scope restrictions, and extension rules. Co-located Shouldly tests provide the
parser and lexer conformance corpora.
