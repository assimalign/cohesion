# GQL public API

`GqlQueryParser` derives from the shared `QueryParser` and returns `GqlQueryStatement`, whose
diagnostics describe malformed or unsupported input. `GqlLanguageProfile.Instance` advertises
only executable clauses. `GqlClauses` names lexical capability keys, including deferred clauses.

`GqlQueryExpression.Stages` is the source-ordered execution input. `Segments` groups those stages at NEXT boundaries, excluding NEXT itself. The `GqlStage` family contains sealed `GqlMatchStage`, `GqlLetStage`, `GqlFilterStage`, `GqlForStage` (also UNWIND), `GqlInsertStage` (also CREATE), `GqlDeleteStage`, `GqlReturnStage` (also WITH) and `GqlNextStage` records. LET adds value variables; FILTER retains TRUE rows; FOR expands lists. RETURN/NEXT and WITH carry referenceable projections in order, using decoded variable names. DISTINCT or aggregation restricts WITH WHERE to projected names; otherwise incoming bindings remain visible to that predicate. Aggregates in WHERE are rejected, while projected aggregate aliases are usable.

`GqlSetStage.Items` contains `GqlSetPropertyItem(Variable, Property, Value)`,
`GqlSetPropertiesItem(Variable, Properties, Merge)` or `GqlSetLabelsItem(Variable, Labels, IsColon)`.
`GqlRemoveStage.Items` contains `GqlRemovePropertyItem(Variable, Property)` or
`GqlRemoveLabelsItem(Variable, Labels, IsColon)`. Both families retain source locations,
have closed abstract bases, and target entity bindings; labels require a node.
`GqlQueryExpression.SetItems` and `RemoveItems` flatten their stages for inspection.
Map operands remain literal-only; NULL property values remove keys. SET/REMOVE are advertised
profile clauses and may finish a modifying NEXT segment.

The original single-segment facade remains available for hand-built ASTs.
`GqlQueryExpression.MatchClauses` groups source-ordered `GqlMatchClause(Paths, Predicate, Optional)`
records, followed by insertions, deletion variables and
projections, `SetQuantifier` (GqlSetQuantifier.All or Distinct), and `ReturnStar`.
`GqlFunctionCallExpression(Function, Arguments)` represents scalar calls resolved to
`GqlScalarFunction`; niladic CURRENT_DATE/CURRENT_TIMESTAMP have empty arguments and IsNiladic.
`GqlCastExpression(Operand, Target)` represents explicit conversion to a `GqlCastType` scalar.
Both nodes retain source locations and immutable children, and receive planner validation
when constructed through the public AST.
`GqlAggregateExpression(Function, Operand, Distinct)` distinguishes aggregates from scalar
expressions. `GqlAggregateFunction` names Count, Sum, Avg, Min, Max and CollectList; collect
parses as CollectList. A null operand means COUNT(*). `GroupBy` is null for implicit grouping,
or an explicit ISO list equal to the non-aggregate RETURN keys. Aggregates skip NULL values;
count is Int64, sum/avg have documented numeric promotion, and collect returns a list. Grouping
runs before projection, DISTINCT and ordering. Invalid placement or scope is COHDBG001.
`OrderBy` contains `GqlSortItem(Expression, Direction, NullOrdering)` records with
`GqlSortDirection.Ascending`/`Descending` and `GqlNullOrdering.Default`/`First`/`Last`.
Default placement is NULL last ascending, first descending. `Offset` (also spelled SKIP)
and `Limit` hold non-negative integer literal or named parameter expressions. These members
apply after RETURN projection and DISTINCT to both rows and paths. Invalid hand-built tails
receive the same planner shape/value diagnostics as parsed statements.
The original constructor still creates the single mandatory clause; `Matches` and `Predicate`
expose that view only and return empty/null for optional or multiple clauses. The engine reads
Stages and null-extends every new binding of an unmatched optional clause. Each clause's
predicate sees its own and preceding variables. Optional-only path projections are COHDBG001.
`GqlPathPattern` holds ordered node/relationship chains or an Elements list of GqlPathNode, GqlPathRelationship and GqlParenthesizedPathPattern records; GqlQuantifier(Min, Max) supplies inclusive relationship/subpath repetition bounds. A parenthesized path has empty chain lists; contradictory representations are COHDBG001. Quantified bindings are source-ordered lists. The chain facade holds ordered nodes and relationships; their immutable metadata
captures labels, direction, type and scalar properties. Each pattern's `LabelExpression` is a
`GqlLabelExpression` tree (`GqlLabelName`, `GqlLabelWildcard`, `GqlLabelNegation`,
`GqlLabelConjunction`, `GqlLabelDisjunction`), and `GqlPatternDirection.LeftOrRight` is the ISO
`<-[]->` / `<->` direction. The conjunction and disjunction are n-ary: each holds an `Operands`
list of any length, and the records compare structurally. `GqlProjection` now carries a
`GqlExpression` and an optional alias, a breaking change from its variable/property tuple.
`GqlVariableExpression` references a bound node, relationship, path or value variable. LET/FOR values may hold scalars, maps, lists or entities; property/member access accepts maps and entities and preserves the accessed value shape. Literal, property, comparison/arithmetic/concatenation
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
or supply a row-valued scalar expression. MATCH entries act as predicates once every referenced pattern variable is bound, including the element itself. INSERT/CREATE entries may reference only the incoming scope. This is a breaking AST change.

See [DESIGN.md](../../DESIGN.md) for ISO scope, source locations, diagnostic codes and conformance.
