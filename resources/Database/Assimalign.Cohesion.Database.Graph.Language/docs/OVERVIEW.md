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

The supported surface is ordered MATCH and OPTIONAL MATCH clauses with clause-local WHERE and
null extension, and finite paths including named paths such as
`MATCH p = (a)-[r:KNOWS]->(b) RETURN p`, ISO label expressions such as `(n:(A|B)&!C)`, `(n:%)` and
`-[r:T|U]->`, every directed ISO edge in full and abbreviated form (`->`, `<-`, `-`, `<->`), scalar
Boolean, arithmetic, null/truth, list-membership and string filters and labeled predicates (`n IS NOT LABELED A`, `n:A|B`),
CASE, COALESCE, NULLIF, list/map literals and parameters, subscripts and map/entity property access,
RETURN expressions and aliases, DISTINCT/ALL, RETURN * and standalone RETURN, `INSERT`, `CREATE` as an insertion compatibility extension, and restricted or
cascading deletion. Unsupported features, including the undirected tilde edges, produce
`COHDBL001`; malformed supported syntax produces stable `GQL` diagnostics, and a Cypher arrow such
as `(a)-->(b)`, which GQL reads as a comment, produces `GQL0008`. The parser does not throw for
malformed query text. As in Neo4j, label expressions and predicates have no length or nesting
limit: `:A:B:...`, `A|B|...` and AND/OR/XOR runs are each one n-ary node of any length, and
parentheses nest as deep as the parsing thread's stack allows; deeper text reports `GQL0009`
instead of overflowing it.

Named path assignment is supported only in `MATCH`. The engine's path request API projects one
bound node, relationship or named path; deleting a path variable or accessing a path property is
rejected by binding. Path results retain the actual traversal entities rather than scalar rows.

Property maps, WHERE operands and RETURN expressions accept `$name` and the Cohesion spelling `@name`.
Both produce `GqlParameterExpression` with a bare name and source span; property-map values
are literal or parameter expressions. See [DIALECT.md](DIALECT.md#named-scalar-parameters) for binding rules.

Read [DESIGN.md](DESIGN.md) for the standard decision, exact clause matrix, AST model, bounds,
diagnostic codes, and the conformance corpus's relationship to ISO GQL.
