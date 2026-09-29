# GQL public API

`GqlQueryParser` derives from the shared `QueryParser` and returns `GqlQueryStatement`, whose
diagnostics describe malformed or unsupported input. `GqlLanguageProfile.Instance` advertises
only executable clauses. `GqlClauses` names lexical capability keys, including deferred clauses.

`GqlQueryExpression` groups finite match paths, a predicate, insertions, deletion variables and
projections. `GqlPathPattern` holds ordered nodes and relationships; their immutable metadata
captures labels, direction, type and scalar properties. `GqlProjection` selects a bound element
or property with an optional alias. Literal, property and binary expression classes describe the
bounded comparison grammar. Programmatically constructed ASTs receive planner validation too.

See [DESIGN.md](../../DESIGN.md) for ISO scope, source locations, diagnostic codes and conformance.
