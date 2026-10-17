# Cohesion GQL dialect

The target is ISO/IEC 39075:2024 with the subset and extensions recorded in [DESIGN.md](DESIGN.md).
The supported-clause matrix there is authoritative; RETURN projects value expressions,
named bindings or all bindings with `*`.

## RETURN

RETURN accepts optional DISTINCT/ALL and either `*` or comma-separated expressions with optional
AS aliases. ALL is the default. Standalone `RETURN 1 + 1 AS two` returns one row containing 2.
RETURN uses WHERE's value expressions, including named parameters, arithmetic, concatenation,
IS null/truth tests and labeled predicates. An unaliased column uses normalized source text:
comments removed and token spacing normalized, preserving spelling, quoted text and grouping.
This naming policy is the owner's default; the available ISO 14.11 preview does not establish
implicit computed names (see [the standards check](DESIGN.md#return-expressions-and-duplicate-elimination-1141)).

RETURN * lists named bindings in first-declaration order, omitting anonymous and repeated
elements; `(a)-[r]->(b)` yields a, r, b. With no named binding, it is GQL0002. DISTINCT removes
duplicate projected tuples, treating NULLs as equal, numbers by exact value across runtime types,
strings ordinally, entities by ID and paths by ordered entity identities. The retained row keeps
its values' runtime types. Comparisons and IN use the same exact numeric values: 0.1m = 0.1d
is FALSE, 1 = 1.0 is TRUE, and mixed integer/Double comparisons above 2^53 retain every
integer bit. Signed zeros compare equal; NULL comparisons remain UNKNOWN while DISTINCT
groups NULLs. NaN and infinities remain COHDBG003 at planning. Arithmetic retains its promotion rules.
Bare binding references also permit entity equality and IS NULL tests in value expressions;
entity/path equality compares identity across independent materializations.

The wire Execute gate uses static planned projection kinds before data access or mutation.
`MATCH (a) RETURN *` now parses and fails with ExecutionFailure containing ExecutePaths
(previously ParseFailure). `INSERT ... RETURN a` and `INSERT ... RETURN *` fail before writing.
Computed scalar columns such as `a.age + 1` pass; EXPLAIN returns scalar descriptions even for
entity projections. ExecutePaths accepts one bound variable, DISTINCT included, or star over one
named binding. Computed/multiple columns or star over several bindings are COHDBG001. The
GraphPathResult and negotiated completion diagnostics keep their existing wire 2.1 shape.

Unbound projection variables/parameters are COHDBG001; `p.name` on a path is COHDBG003.
`RETURN a {.name}` is COHDBL001 naming MAP PROJECTION. NEXT, WITH (including WITH DISTINCT)
and return-after-delete remain COHDBL001. Collections and CASE remain outside this item.

## Named scalar parameters

Node/relationship property maps in MATCH, INSERT and CREATE, WHERE operands and RETURN expressions accept
`$name` and `@name`. `$name` is the ISO/Cypher spelling; `@name` is a Cohesion extension.
Names bind by their exact, case-sensitive bare name; one leading sigil on a map key is ignored.
`$1` binds key `"1"`, never a positional slot. Supplied NULL is bound; unused values are ignored.
Normalized-key collisions fail with COHDBG014, missing bindings with COHDBG001, and unsupported
or non-finite values with COHDBG003, all while planning. EXPLAIN applies the same checks.
Runtime scalar types are preserved (including Int32 and Decimal); schema type matching uses the
typed graph API's promotion. List/map values remain unsupported. See
[DESIGN.md](DESIGN.md#named-scalar-parameters-1102-1138) for the complete contract and hand-offs.
`$$name` and `(n $props)` remain COHDBL001; bare sigils and parameters in
identifier/DELETE positions are GQL0002. #1102's `?` and `:name` diagnostics are unchanged.

## WHERE expressions

WHERE accepts AND, OR, XOR, NOT, comparisons, bare Boolean properties, IS [NOT]
NULL/TRUE/FALSE/UNKNOWN, numeric + - * /, % (Cypher MOD spelling), `||`, string +,
labeled predicates and parentheses. IN [items], STARTS WITH, ENDS WITH and CONTAINS
are Cohesion extensions. String predicates are ordinal, case-sensitive and UNKNOWN unless
both operands are strings. Lists are restricted to IN's right operand. LIKE, BETWEEN, =~,
EXISTS, quantified predicates and nested property access remain COHDBL001; IN (1, 2)
is GQL0002 naming the bracketed form.

WHERE keeps TRUE only. Logical operators use three-valued logic. Incomparable non-null values
are unequal; UNKNOWN is a Boolean literal represented by NULL. Incomparable ordering is UNKNOWN.
IN evaluates every list item before testing membership; a later value error raises COHDBG013
even when an earlier item matches. IN over an empty list is FALSE even for NULL;
otherwise a match wins, then a NULL operand or item yields UNKNOWN, otherwise FALSE.
Thus `1 IN [1, NULL]` is TRUE and `1 IN [2, NULL]` is UNKNOWN.
IS truth tests accept Boolean/NULL and return Boolean. Evaluated non-Boolean logical values
raise COHDBG013.

Precedence from weakest to strongest; binary runs associate left:

| Level | Operators |
| --- | --- |
| 1 | OR, XOR |
| 2 | AND |
| 3 | NOT |
| 4 | Comparisons, IN, string predicates, optional IS null/truth tests |
| 5 | `\|\|` |
| 6 | Binary +, - |
| 7 | *, /, % |
| 8 | Unary +, - |
| 9 | Primary expressions and parentheses |

`TRUE OR TRUE XOR TRUE` is FALSE; `TRUE XOR TRUE OR TRUE` is TRUE.
The [GQL grammar](https://github.com/opengql/grammar/blob/main/GQL.g4#L2062) defines the shared
OR/XOR level; [Neo4j's GE07 entry](https://neo4j.com/docs/cypher-manual/current/appendix/gql-conformance/supported-optional/)
documents Cypher's different precedence.

Arithmetic promotion selects Double for any floating operand, otherwise Decimal for decimal/ulong,
otherwise checked Int64. Integer division truncates toward zero. Integral remainder by -1
is 0, including Int64.MinValue; Int64.MinValue / -1 raises COHDBG013. Zero divisors, overflow,
non-finite results and invalid arithmetic types raise COHDBG013; concatenation requires two
strings. Structured engine diagnostics carry code-free messages; protocol 1.0 sends
ExecutionFailure `COHDBG013: message` before ResultHeader.

Expressions have no count limit. Recursive depth follows the thread's stack: GQL0009 during
parsing, COHDBG008 in the engine. See [DESIGN.md](DESIGN.md#scalar-where-expressions-1140)
for the grammar, precedence, promotion decisions, comments and reference evidence.

## Wire diagnostics (#1105)

On negotiated protocol 2.1 with the diagnostics capability, Warning and Error diagnostics retain their complete code, severity, message and source span over the model wire. Information diagnostics are omitted. A negotiated 2.0 connection retains the existing Error text and result frames. Language admission and execution semantics are unchanged.
