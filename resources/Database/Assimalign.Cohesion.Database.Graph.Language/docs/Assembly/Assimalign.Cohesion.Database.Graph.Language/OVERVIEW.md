# GQL public API

`GqlQueryParser` derives from the shared `QueryParser` and returns `GqlQueryStatement`, whose
diagnostics describe malformed or unsupported input. `GqlLanguageProfile.Instance` advertises
only executable clauses. `GqlClauses` names lexical capability keys, including deferred clauses.

`GqlQueryExpression.MatchClauses` groups source-ordered `GqlMatchClause(Paths, Predicate, Optional)`
records, followed by insertions, deletion variables and
projections, `SetQuantifier` (GqlSetQuantifier.All or Distinct), and `ReturnStar`.
`OrderBy` contains `GqlSortItem(Expression, Direction, NullOrdering)` records with
`GqlSortDirection.Ascending`/`Descending` and `GqlNullOrdering.Default`/`First`/`Last`.
Default placement is NULL last ascending, first descending. `Offset` (also spelled SKIP)
and `Limit` hold non-negative integer literal or named parameter expressions. These members
apply after RETURN projection and DISTINCT to both rows and paths. Invalid hand-built tails
receive the same planner shape/value diagnostics as parsed statements.
The original constructor still creates the single mandatory clause; `Matches` and `Predicate`
expose that view only and return empty/null for optional or multiple clauses. The engine reads
MatchClauses and null-extends every new binding of an unmatched optional clause. Each clause's
predicate sees its own and preceding variables. Optional-only path projections are COHDBG001.
`GqlPathPattern` holds ordered nodes and relationships; their immutable metadata
captures labels, direction, type and scalar properties. Each pattern's `LabelExpression` is a
`GqlLabelExpression` tree (`GqlLabelName`, `GqlLabelWildcard`, `GqlLabelNegation`,
`GqlLabelConjunction`, `GqlLabelDisjunction`), and `GqlPatternDirection.LeftOrRight` is the ISO
`<-[]->` / `<->` direction. The conjunction and disjunction are n-ary: each holds an `Operands`
list of any length, and the records compare structurally. `GqlProjection` now carries a
`GqlExpression` and an optional alias, a breaking change from its variable/property tuple.
`GqlVariableExpression` references a bound node, relationship or path. Literal, property, comparison/arithmetic/concatenation
(`GqlBinaryExpression`), n-ary AND/OR/XOR (`GqlLogicalExpression` with `GqlLogicalOperator`)
and labeled-predicate (`GqlLabeledPredicate`) classes describe scalar expressions.
`GqlUnaryExpression` holds NOT or a numeric sign. `GqlIsNullExpression` and
`GqlTruthTestExpression` hold null and Boolean truth tests; a null `TruthValue` means UNKNOWN.
`GqlStringPredicateExpression` uses `GqlStringPredicateOperator` for ordinal prefix, suffix or
substring tests. `GqlInExpression` has an `Operand` and a `List` of type `GqlExpression`, allowing any list-valued expression.
`GqlListExpression.Items` is a copied, read-only sequence supported in every value position.
`GqlMapExpression` holds ordinal entries; `GqlSubscriptExpression` and `GqlMemberExpression` read values.
`GqlCaseExpression` holds the optional operand, ordered `GqlCaseAlternative` records and ELSE.
Each alternative's `Comparison` defaults to equality and also represents comparison parts and null tests;
comma operands expand to alternatives sharing THEN. `GqlCoalesceExpression` and `GqlNullIfExpression`
represent the case abbreviations. Programmatically constructed ASTs
receive planner shape validation too.

`GqlParameterExpression` stores a bare, case-sensitive `Name` and the complete reference's
`Location` for `$name` and `@name`. Node and relationship pattern `Properties` now take
`IReadOnlyDictionary<string, GqlExpression>`; wrap each constant in `GqlLiteralExpression`
or supply a parameter expression. This is a breaking AST change.

See [DESIGN.md](../../DESIGN.md) for ISO scope, source locations, diagnostic codes and conformance.
