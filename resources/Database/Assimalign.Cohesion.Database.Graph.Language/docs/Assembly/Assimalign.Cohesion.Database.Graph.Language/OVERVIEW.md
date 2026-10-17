# GQL public API

`GqlQueryParser` derives from the shared `QueryParser` and returns `GqlQueryStatement`, whose
diagnostics describe malformed or unsupported input. `GqlLanguageProfile.Instance` advertises
only executable clauses. `GqlClauses` names lexical capability keys, including deferred clauses.

`GqlQueryExpression` groups finite match paths, a predicate, insertions, deletion variables and
projections. `GqlPathPattern` holds ordered nodes and relationships; their immutable metadata
captures labels, direction, type and scalar properties. Each pattern's `LabelExpression` is a
`GqlLabelExpression` tree (`GqlLabelName`, `GqlLabelWildcard`, `GqlLabelNegation`,
`GqlLabelConjunction`, `GqlLabelDisjunction`), and `GqlPatternDirection.LeftOrRight` is the ISO
`<-[]->` / `<->` direction. The conjunction and disjunction are n-ary: each holds an `Operands`
list of any length, and the records compare structurally. `GqlProjection` selects a bound element
or property with an optional alias. Literal, property, comparison/arithmetic/concatenation
(`GqlBinaryExpression`), n-ary AND/OR/XOR (`GqlLogicalExpression` with `GqlLogicalOperator`)
and labeled-predicate (`GqlLabeledPredicate`) classes describe scalar expressions.
`GqlUnaryExpression` holds NOT or a numeric sign. `GqlIsNullExpression` and
`GqlTruthTestExpression` hold null and Boolean truth tests; a null `TruthValue` means UNKNOWN.
`GqlStringPredicateExpression` uses `GqlStringPredicateOperator` for ordinal prefix, suffix or
substring tests. `GqlInExpression` has an `Operand` and a `GqlListExpression` whose copied,
read-only `Items` are supported only on IN's right side. Programmatically constructed ASTs
receive planner shape validation too.

See [DESIGN.md](../../DESIGN.md) for ISO scope, source locations, diagnostic codes and conformance.
