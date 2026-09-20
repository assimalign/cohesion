# Assimalign.Cohesion.Database.Graph.Language

This package parses the executable Cohesion subset of ISO/IEC 39075 GQL into an AST. It depends on
`Database.Language` for the lexer, parser lifecycle, analyzer pipeline, and diagnostic contracts.
The Graph engine consumes the AST for planning and execution in the session's bound database.

```csharp
var parser = new GqlQueryParser();
var statement = (GqlQueryStatement)parser.Parse(
    "MATCH (a:Person {name: 'Alice'})-[r:KNOWS]->(b) RETURN b.name");
// Inspect statement.Diagnostics before consuming statement.GqlExpression.
```

The supported surface is finite `MATCH` paths, including named paths such as
`MATCH p = (a)-[r:KNOWS]->(b) RETURN p`, scalar comparison/conjunction filters, variable or
property projection, `INSERT`, `CREATE` as an insertion compatibility extension, and restricted or
cascading deletion. Unsupported features produce `COHDBL001`; malformed supported syntax produces
stable `GQL` diagnostics. The parser does not throw for malformed query text.

Named path assignment is supported only in `MATCH`. The engine's path request API projects one
bound node, relationship or named path; deleting a path variable or accessing a path property is
rejected by binding. Path results retain the actual traversal entities rather than scalar rows.

Read [DESIGN.md](DESIGN.md) for the standard decision, exact clause matrix, AST model, bounds,
diagnostic codes, and the conformance corpus's relationship to ISO GQL.
