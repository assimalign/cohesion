# Assimalign.Cohesion.Database.Documents.Language

This package parses the Documents engine's executable SQL++ profile, retaining the OQL name, into typed statement and
expression trees. It provides `OqlLanguageProfile`, stable `OqlClauses` names, `OqlQueryParser`,
`OqlQueryStatement`,
`OqlSelectExpression`, `OqlInsertExpression`, `OqlUpsertExpression`, `OqlUpdateExpression`, `OqlUpdateAssignment`, `OqlDeleteExpression`, `OqlMetaExpression`, `OqlObjectExpression`, `OqlCreateCollectionExpression`, `OqlDropCollectionExpression`,
`OqlCreateIndexExpression`, `OqlDropIndexExpression`, and expression nodes
for projection, filtering, grouping, aggregates, ordering, nested properties, and array access.
One immutable function table declares META, five aggregates and 34 SQL++ scalars, with parse-time
arity checks, OQL0008 for unknown calls, and static result types consumed by the Documents engine.
SELECT LIMIT/OFFSET accept signed integer literals or parameters in either order, with non-negative
Int64 values and OQL0007 for invalid counts. LIMIT and OFFSET are reserved field names; quoting
or a preceding dot keeps them usable, and AS labels still accept them.
Chainable INNER/LEFT UNNEST, ANY/SOME/EVERY ... SATISFIES ... END and array-value IN execute.
`OqlSelectExpression.Unnest`, `OqlUnnestExpression` and `OqlQuantifiedExpression` retain the
bindings. The new words remain contextual; END retains its existing reservation. See the
[array contract](DESIGN.md#array-bindings-and-quantified-predicates-1136) for scope and truth rules.

```csharp
var parser = new OqlQueryParser();
var statement = (OqlQueryStatement)parser.Parse(
    "SELECT p.name, p.orders[0].total AS total FROM people p WHERE p.age >= $minimum ORDER BY p.name");

// Check statement.Diagnostics for errors before passing its AST to planning.
var query = statement.OqlExpression;
```

The package references only the shared `Assimalign.Cohesion.Database.Language` package. The
Documents engine supplies catalog binding, planning, and execution. Queries and index DDL address
one collection in the session's database. `CREATE COLLECTION [IF NOT EXISTS]`,
`DROP COLLECTION [IF EXISTS]`, `CREATE INDEX`, and `DROP INDEX` use the same
parse-plan-execute path as `SELECT`. `INSERT INTO ... (KEY, VALUE) VALUES` and INSERT SELECT
create externally keyed documents. UPSERT uses the same forms to insert or wholly replace. `META([alias]).id` and `.version` expose stored identities
and versions independently of JSON content. `DELETE FROM collection [AS? alias] [WHERE expression]`
deletes snapshot-selected documents and retains the collection and indexes. EXPLAIN accepts
SELECT, INSERT, UPSERT, UPDATE and DELETE. UPDATE accepts SET and/or UNSET over nested properties and
array indexes; SET-to-MISSING removes a property. USE KEYS, RETURNING, FOR comprehensions and
UPDATE/DELETE LIMIT remain deferred.
`DEFINE` is recognized statement-initially, and `ELEMENT` / `FLATTEN` in call position, to
produce `CK00001`; all three remain usable field names. Subqueries remain unsupported.
MISSING has its own AST node, distinct from NULL, and `IS [NOT] MISSING` / `IS [NOT] VALUED`
are executable presence predicates. NIL remains a NULL synonym.

See [DESIGN.md](DESIGN.md) for the supported-clause matrix, statement/expression grammar,
diagnostics, scope restrictions, and extension rules. Co-located Shouldly tests provide the
parser and lexer conformance corpora.

## Access estimates (#1527)

The engine costs competing equality-prefix/range paths using bounded snapshot-visible B+Tree
probes and may intersect selected identity sets before JSON reads. EXPLAIN retains its existing
grammar and nonexecuting contract, naming IndexSeek/IndexIntersection with entry estimates or
explicit bounds in the existing nullable count columns. actual_rows remains NULL; EXPLAIN ANALYZE
and standalone ANALYZE remain unsupported in OQL. No persisted statistics or language AST changes.