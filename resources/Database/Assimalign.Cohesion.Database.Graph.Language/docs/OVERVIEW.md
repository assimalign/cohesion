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
null extension, quantified relationships/subpaths under trail semantics (ISO {m,n}, +, *, Cypher *m..n), and named paths such as
`MATCH p = (a)-[r:KNOWS]->(b) RETURN p`, ISO label expressions such as `(n:(A|B)&!C)`, `(n:%)` and
`-[r:T|U]->`, every directed ISO edge in full and abbreviated form (`->`, `<-`, `-`, `<->`), scalar
Boolean, arithmetic, null/truth, list-membership and string filters and labeled predicates (`n IS NOT LABELED A`, `n:A|B`),
core element/string/math functions, CAST, Cypher conversions, transaction-start CURRENT_DATE/CURRENT_TIMESTAMP,
CASE, COALESCE, NULLIF, list/map literals and parameters, subscripts and map/entity property access,
LET and FILTER, FOR (Cypher UNWIND), RETURN ... NEXT and Cypher WITH,
RETURN expressions and aliases, DISTINCT/ALL, RETURN * and standalone RETURN,
COUNT, SUM, AVG, MIN, MAX and COLLECT_LIST (Cypher collect), with implicit or ISO GROUP BY,
ORDER BY with ASC/DESC and NULLS FIRST/LAST, OFFSET (Cypher SKIP) and LIMIT,
`INSERT`, `CREATE` as an insertion compatibility extension, SET/REMOVE properties and node labels,
literal-map replacement and D3 merge (`+=`), and restricted or
cascading deletion. Unsupported features, including the undirected tilde edges, produce
`CK00001`; malformed supported syntax produces stable `GQL` diagnostics, and a Cypher arrow such
as `(a)-->(b)`, which GQL reads as a comment, produces `CG00008`. The parser does not throw for
malformed query text. As in Neo4j, label expressions and predicates have no length or nesting
limit: `:A:B:...`, `A|B|...` and AND/OR/XOR runs are each one n-ary node of any length, and
parentheses nest as deep as the parsing thread's stack allows; deeper text reports `CG00009`
instead of overflowing it.

Named path assignment is supported only in `MATCH`. The engine's path request API projects one
bound node, relationship or named path; deleting a path variable or accessing a path property is
rejected by binding. Path results retain the actual traversal entities rather than scalar rows.

Property maps, WHERE operands and RETURN expressions accept `$name` and the Cohesion spelling `@name`.
Both produce `GqlParameterExpression` with a bare name and source span; property-map values
are scalar value expressions, evaluated against the incoming row. See [DIALECT.md](DIALECT.md#named-scalar-parameters) for binding rules.

Read [DESIGN.md](DESIGN.md) for the standard decision, exact clause matrix, AST model, bounds,
diagnostic codes, and the conformance corpus's relationship to ISO GQL.

#1533 adds GraphQueryLimits (optional lower MemoryBytes/WorkUnits) for embedded query requests.
The engine defaults are 256 MiB retained memory and 1,000,000 physical work units. Internal read
bindings stream and an unordered LIMIT can stop upstream work; final results remain materialized.
This changes no grammar or wire bytes; remote execution uses the server engine ceilings.

Standalone START TRANSACTION, COMMIT and ROLLBACK parse into GqlTransactionCommand and run through the existing session transaction API, including over Execute (#1540).

## Graph diagnostic conventions (#1470)

Exception and diagnostic templates use the project's build-generated `ErrorMessages` resources.
Parameterized templates format with the current culture; abort/cancellation templates used in
normal cleanup are cached. The [Database error-code registry](../../Assimalign.Cohesion.Database/docs/ERROR-CODES.md)
assigns this family its two-letter prefix and categorized five-digit identities.
Existing SQLSTATE meanings, numeric wire error kinds, frame layouts and disk formats are preserved.
Structured diagnostics keep their code outside the message. Compiler diagnostic identifiers and
standard .NET argument guards retain their own contracts.

## Debugger inspection (#1470)

AST debugger views use stored properties without building query stages, traversing caller collections or initializing parser profiles.

Eligible types follow the [Database conventions](../../Assimalign.Cohesion.Database/docs/CONVENTIONS.md) debugger display/proxy rules. Views are private and read-only; inspection does not change the public API, execution paths, error contracts, storage formats or protocol bytes.
