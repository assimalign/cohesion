# Cohesion GQL dialect

The target is ISO/IEC 39075:2024 with the subset and extensions recorded in [DESIGN.md](DESIGN.md).
The supported-clause matrix there is authoritative; RETURN projects value expressions,
named bindings or all bindings with `*`.

## MATCH and OPTIONAL MATCH

MATCH and OPTIONAL MATCH clauses execute in source order. Each WHERE belongs to its own
clause and may reference variables introduced by that clause or earlier ones. MATCH keeps
matching rows; OPTIONAL MATCH also keeps each unmatched input row, preserving earlier bindings
and binding every newly declared node, relationship and named path to NULL. Its WHERE filters
inside that optional match. A leading optional starts from one empty binding, so an empty match
returns one all-null row. Comma-separated paths belong to one clause and succeed or null-extend
together. Trail uniqueness is per path, including every quantified repetition; another comma-separated
path or a later clause may reuse a relationship. This differs from Neo4j's clause-wide uniqueness.

Quantified relationships accept ISO `->{m,n}`, `{m}`, `{m,}`, `{,n}`, `{,}`, `->+` and `->*` in every
supported direction, and Cypher `-[r:T*m..n]->` before the property map. Bracketed `*` and `*..n`
start at 1; ISO postfix `*` and `{,n}` start at 0. Cypher `[*0]` is a zero-length path; ISO `{0}`
is GQL0002. Parenthesized bodies repeat as a unit: `(a) ((x)-[:T]->(y)){2} (b)`, with adjoining
iteration endpoints constrained together. Zero repetitions test both outer nodes on the same node;
unknown inner names warn without removing that row. A bare nullable subpath also anchors on one
graph node, unlike Neo4j. Mixing Cypher lengths and ISO subpaths in one clause is accepted.

The sum of upper bounds, or lower bounds for unbounded segments, times body hop counts is at most
64 (GQL0005 for text, COHDBG001 for direct ASTs). Unbounded segments use implicit trails, a deliberate
ISO deviation from the requirement for a mode/selector. An eligible 65th relationship after type
and direction, bound-identity and own-property filtering raises COHDBG004; LIMIT and endpoint predicates do not hide it. The
100,000-binding and 1,000,000-expansion budgets also apply. Results materialize before wire output.

Variables inside quantifiers are lists in source order, including under RETURN * and WITH *.
FOR/UNWIND preserve the singleton entity kind; list expressions and aggregates can consume them.
Using a group as a singleton, accessing `r.w`, deleting the list, or referencing a group in a map
inside a repeated segment is COHDBG003. Unnegotiated composite projections are COHDBG016;
negotiated Execute returns a provably empty Max=0 group as `[]` and retains COHDBG001
for possible nonempty lists of nodes or relationships. Group provenance survives result-preserving
expressions and container access. Project scalar values
after FOR/UNWIND, or project a full named path through ExecutePaths. No wire format is added.

Node and INSERT/CREATE quantifiers are GQL0002. Questioned paths after an edge or subpath,
subpath assignments, inner WHERE and nested quantifiers remain named COHDBL001. Explicit TRAIL
remains COHDBL001. A touching `--` after `}`, `+`, `*` or a subpath's `)` is GQL0008.
Subpath parsing uses GQL0009 on stack exhaustion, with no separate text nesting cap; direct AST
nesting beyond 128, cycles and repeated path instances in one walk are COHDBG001.

A NULL binding in a later pattern matches nothing. Property access, labeled predicates and
ordinary comparisons over NULL yield NULL/UNKNOWN; IS NULL is TRUE. RETURN expressions and
DISTINCT reuse the existing exact numeric and NULL rules. DELETE ignores NULL and counts only
entities actually deleted. INSERT with a NULL endpoint fails with COHDBG003 and rolls back.
Unknown optional labels/types yield NULL bindings and read-only COHDBG010/COHDBG011 warnings.

ExecutePaths rejects a projected variable bound only by optional clauses with COHDBG001 while
planning, even when every row would match. A mandatory binding remains projectable. Scalar
Execute already represents NULL without a wire change. EXPLAIN describes ordered clause joins
and optional filters/null extension. ISO `OPTIONAL { ... }` and `OPTIONAL ( ... )` block operands
remain recognized but outside T1 (COHDBL001 naming OPTIONAL BLOCK OPERAND). MANDATORY MATCH
remains COHDBL001. MATCH after INSERT or DELETE is a supported linear stage.

## RETURN

RETURN accepts optional DISTINCT/ALL and either `*` or comma-separated expressions with optional
AS aliases. ALL is the default. Standalone `RETURN 1 + 1 AS two` returns one row containing 2.
RETURN uses WHERE's value expressions, including named parameters, arithmetic, concatenation,
IS null/truth tests and labeled predicates. An unaliased column uses normalized source text:
comments removed and token spacing normalized, preserving spelling, quoted text and grouping.
This naming policy is the owner's default; the available ISO 14.11 preview does not establish
implicit computed names (see [the standards check](DESIGN.md#return-expressions-and-duplicate-elimination-1141)).

RETURN * lists named bindings in first-declaration order, omitting anonymous and repeated
elements; `(a)-[r]->(b)` yields a, r, b. Quantified variables are included as lists. With no named binding, it is GQL0002. DISTINCT removes
duplicate projected tuples, treating NULLs as equal, numbers by exact value across runtime types,
strings ordinally, entities by ID and paths by ordered entity identities. The retained row keeps
its values' runtime types. Comparisons and IN use the same exact numeric values: 0.1m = 0.1d
is FALSE, 1 = 1.0 is TRUE, and mixed integer/Double comparisons above 2^53 retain every
integer bit. Signed zeros compare equal; NULL comparisons remain UNKNOWN while DISTINCT
groups NULLs. Overflowing numeric text such as `1e999` is a parse error, GQL0004.
NaN and infinities supplied as AST literals or parameters fail planning with COHDBG003.
Arithmetic retains its promotion rules.
Bare binding references also permit entity equality and IS NULL tests in value expressions;
entity/path equality compares identity across independent materializations.

The wire Execute gate uses static planned projection kinds before data access or mutation.
`MATCH (a) RETURN *` now parses and fails with ExecutionFailure containing ExecutePaths
(previously ParseFailure). `INSERT ... RETURN a` and `INSERT ... RETURN *` fail before writing.
Computed scalar columns such as `a.age + 1` pass; EXPLAIN returns scalar descriptions even for
entity projections. ExecutePaths accepts one bound variable, DISTINCT included, or star over one
named binding. Computed/multiple columns or star over several bindings are COHDBG001. The
GraphPathResult and negotiated completion diagnostics keep their existing wire 2.1 shape.

Final column names, both implicit and explicit, must be unique ordinally and case-sensitively.
Duplicates raise planning diagnostic COHDBG015 before reads or mutations, even under EXPLAIN
or on empty matches. Path-request shape errors retain precedence. Thus `RETURN 1 AS x, 2 AS x`
fails, while `RETURN 1 AS x, 2 AS X` succeeds.
Unbound projection variables/parameters are COHDBG001; `p.name` on a path is COHDBG003.
`RETURN a {.name}` is COHDBL001 naming MAP PROJECTION. Immediate return-after-delete remains COHDBL001. NEXT and WITH, including WITH DISTINCT, execute as described below. CASE and composite values are described below.

## LET, FILTER, FOR/UNWIND, NEXT and WITH

A statement is an ordered binding-table pipeline. LET binds one or more new variables (`LET x = 1, y = x + 1`); FILTER and FILTER WHERE keep TRUE only. FOR x IN list and Cypher UNWIND list AS x expand lists and preserve NULL elements. A NULL list produces zero rows. Non-lists, including strings and byte[], give COHDBG013. This deliberately differs from Neo4j's scalar UNWIND, which produces one row. FOR WITH ORDINALITY/OFFSET is outside T1 (COHDBL001).

RETURN ... NEXT and Cypher WITH project a new scope using the same expressions, aliases, star and DISTINCT/ALL as final RETURN. Only aliases and bare variables are referenceable afterward; unaliased computed columns are allowed but unreferenceable, an explicit superset divergence from Cypher. WITH DISTINCT compares tuples with RETURN's exact numeric comparer. RETURN * includes LET/FOR variables and expands carried columns in projection order. NEXT and FOR are also accepted as variable names and AS aliases.

WITH supports GROUP BY, ORDER BY, OFFSET/SKIP, LIMIT and WHERE. Without DISTINCT/grouping, ORDER BY and WHERE can read incoming variables as well as aliases; aliases shadow the incoming scope. With DISTINCT/grouping they read only projected/grouped values. WHERE filters after ordering and paging. Int64 COUNT metadata, entity kinds, optionality and possible composite values survive projection boundaries.

NEXT requires RETURN on a read segment, and cannot trail a statement. A modifying segment without RETURN passes one empty binding and no variables to NEXT. INSERT and DELETE run in source order; deleted variables leave scope (a later reference is COHDBG003). Statements see their own writes, so INSERT (:T {k:1}) MATCH (t:T) RETURN t.k works even for a new label. INSERT maps evaluate scalar expressions against each row, enabling FOR name IN $names INSERT (:Person {name:name}); composite properties remain unsupported.

LET/FOR cannot rebind a name in scope (COHDBG003). Dropped or unaliased variables are unbound (COHDBG001). Standalone ordering/page stages remain COHDBL001, including after the new clauses. Immediate RETURN after DELETE remains COHDBL001. FOREACH, CALL, NEXT YIELD and UNION remain excluded. Every stage materializes eagerly under the shared 100,000-row bound. The final projection alone drives scalar/composite wire admission; read-only composed entity/path results use ExecutePaths, while any write stage rejects a path request.

## Aggregates and GROUP BY

RETURN accepts COUNT(*), COUNT(expr), SUM, AVG, MIN, MAX and COLLECT_LIST, with optional
DISTINCT or ALL inside the parentheses. Cypher collect is an exact alias of COLLECT_LIST.
COUNT(DISTINCT *) and COUNT(ALL *) are GQL0002. Every non-aggregated RETURN item is an implicit grouping key;
ISO `GROUP BY` must list exactly those items' names, in any order; equivalent input expressions
are also accepted. Plain variables resolve to RETURN names first; aggregate aliases are COHDBG001.
`GROUP BY ()` denotes no keys and raises the usual mismatch error if non-aggregate items exist. Duplicate RETURN keys
count once. Mixed aggregate expressions may compose grouping keys or read grouped entities' properties,
as described in [DESIGN.md](DESIGN.md#aggregates-and-grouping-1147). GROUP BY without an
aggregate eliminates duplicate keys. RETURN aliases bind plain grouping names and the later
ORDER BY stage, where they shadow input variables; RETURN expressions retain input scope.

NULLs are skipped. COUNT(*) counts rows and COUNT(expr) counts non-null values. Count is
always Int64, including zero and wire column metadata. SUM returns Int64 for integral
inputs, Double when any retained input is floating, and Decimal when any is Decimal or
UInt64 (Decimal takes precedence). Exact Decimal sums retain the largest input scale, with
integral inputs at scale zero. Mixed integer/floating sums promote the exact integer total once.
AVG returns Double for integers/floats and Decimal when
any retained input is Decimal. Numeric errors use COHDBG013. MIN/MAX preserve the chosen
input's type and use ORDER BY's exact numeric and composite ordering. DISTINCT inside an
aggregate and grouping use RETURN's exact equality, including NULLs inside lists/maps.
COLLECT_LIST returns a list of non-null inputs, preserving duplicates and input order unless
DISTINCT is specified. Wire lists use the negotiated graph.composite-values capability.

Empty/all-NULL input yields count 0, sum 0, avg/min/max NULL and collect an empty list.
An aggregate without grouping keys always returns one row; an empty keyed match returns
none. Grouping precedes projection, RETURN DISTINCT, ORDER BY, OFFSET/SKIP and LIMIT,
so ordering by an aggregate alias sees the computed group value. Neither paging nor grouping
reduces the input match bound. Nested aggregates, aggregates in WHERE/GROUP BY, unequal
GROUP BY keys and ungrouped ORDER BY references fail at planning with COHDBG001.
ExecutePaths retains its existing COHDBG001 projection message. HAVING, STDDEV_SAMP,
STDDEV_POP, PERCENTILE_CONT and PERCENTILE_DISC are COHDBL001; WITH uses the same aggregation stage.

## ORDER BY, OFFSET, SKIP and LIMIT

After RETURN or WITH, `ORDER BY expression [ASC|ASCENDING|DESC|DESCENDING] [NULLS FIRST|LAST]` accepts multiple keys,
RETURN aliases and projected expressions. ASC is the default. NULL sorts last in ASC and
first in DESC (Cypher); explicit placement overrides it. This differs from Cohesion SQL's
NULL-first ASC. NULLS, FIRST and LAST are contextual words and remain available as names.
Strings sort ordinally and numbers compare exactly. The ascending family order is map,
node, relationship, list, path, string, Boolean, number, NULL. Lists are lexicographic;
maps compare count, sorted ordinal keys, then corresponding values. Nested NULL sorts last.
Equal keys retain input order. CASE, COALESCE and composite values can supply keys.

Without DISTINCT, keys may use unprojected bindings; with DISTINCT they must reference
projected values (COHDBG001 otherwise). DISTINCT precedes sorting. OFFSET (Cypher SKIP)
then drops rows and LIMIT truncates them. OFFSET/SKIP must precede LIMIT; LIMIT followed by OFFSET/SKIP, duplicate
OFFSET/SKIP/LIMIT and ordering after paging are GQL0002. Counts are unsigned Int64 literals
or `$name`/`@name` parameters. Invalid literal forms are GQL0002 and overflow is GQL0004.
Parameters must hold non-negative integral values in the signed 64-bit range; invalid
graph values, including NULL and floating types, fail COHDBG013 before reads or writes. Non-finite and unsupported CLR values fail COHDBG003 during binding.

Paging never reduces mutation inputs. Full materialization still applies: LIMIT 1 over
more than 100,000 matches raises COHDBG004. Row and path requests share this stage.
Standalone ORDER BY/page statements before RETURN remain COHDBL001. WITH and aggregate ordering execute through the same per-stage pipeline. See [ordering design](DESIGN.md#ordering-and-paging-1142).

## CASE and composite values

Simple and searched CASE select the first TRUE comparison/predicate; WHEN NULL never
matches. No match without ELSE yields NULL. Selected branches evaluate lazily, preserving
their runtime type. COALESCE returns the first non-NULL argument; NULLIF returns NULL for
TRUE equality, otherwise its first argument. These are ISO case abbreviations, not functions.

Lists [1, a.name, [2]] and maps {name:a.name, n:1} work in WHERE, IN and RETURN.
Parameters accept IList (object arrays and List<long>, excluding binary byte[]) or
IReadOnlyDictionary<string, object?> maps, and preserve scalar element types. Results use
object arrays and read-only ordinal maps. Negative indexes count from the end; out-of-range
indexes and missing fields yield NULL. {k:{j:1}}.k.j and $m.k access fields; $l[0] indexes
a list. Invalid index/key/container types use COHDBG013. Stored a.b.c, comprehensions and
slicing remain COHDBL001. INSERT/CREATE composite properties stay unsupported: syntax is
COHDBL001 naming graph storage, parameters are COHDBG003. (n $props) remains COHDBL001.

Equality is structural and three-valued: differing lengths/key sets or a FALSE field yields
FALSE; otherwise a NULL field comparison yields UNKNOWN. IN, simple CASE and NULLIF
reuse this rule and exact numbers (0.1m differs from 0.1d, 1 equals 1.0). DISTINCT groups
corresponding NULLs structurally. Lists order lexicographically; maps compare count, sorted
ordinal keys, then values. Encountered NULL/incomparable values yield UNKNOWN in predicates.
ORDER BY uses the total order above. CASE/composite values containing possible
entities fail the static wire Execute gate before mutations, including on empty matches.

Simple CASE accepts comma-separated WHEN operands, comparison parts (=, <>, !=, <, <=, >, >=),
and IS NULL / IS NOT NULL. Its operand evaluates once; comparisons use the same exact rule as WHERE.
Entity expressions support scalar property reads through (a).name, COALESCE(b,a).name and a['name'].
Constructed values exceeding depth 64, 65,536 values or 16 MiB fail with COHDBG013 inside the
statement transaction, including in process; the statement's writes roll back.

Wire composite values require graph.composite-values under existing protocol 2.1 negotiation.
Without it, projections that may return composites fail statically with COHDBG016 before writer locking and parameters are
refused locally. The codec bounds are depth 64, 65536 total values and 16777216 bytes.
Malformed components are ProtocolViolation. Path result encoding is unchanged.

## Named scalar parameters

Node/relationship property maps in MATCH, INSERT and CREATE, WHERE operands and RETURN expressions accept
`$name` and `@name`. `$name` is the ISO/Cypher spelling; `@name` is a Cohesion extension.
Names bind by their exact, case-sensitive bare name; one leading sigil on a map key is ignored.
`$1` binds key `"1"`, never a positional slot. Supplied NULL is bound; unused values are ignored.
Normalized-key collisions fail with COHDBG014, missing bindings with COHDBG001, and unsupported
or non-finite values with COHDBG003, all while planning. EXPLAIN applies the same checks.
Runtime scalar types are preserved (including Int32 and Decimal); schema type matching uses the
typed graph API's promotion. Lists/maps are admitted in value positions; pattern property maps remain scalar-only. See
[DESIGN.md](DESIGN.md#named-scalar-parameters-1102-1138) for the complete contract and hand-offs.
`$$name` and `(n $props)` remain COHDBL001; bare sigils and parameters in
identifier/DELETE positions are GQL0002. #1102's `?` and `:name` diagnostics are unchanged.

## WHERE expressions

WHERE accepts AND, OR, XOR, NOT, comparisons, bare Boolean properties, IS [NOT]
NULL/TRUE/FALSE/UNKNOWN, numeric + - * /, % (Cypher MOD spelling), `||`, string +,
labeled predicates and parentheses. IN [items], STARTS WITH, ENDS WITH and CONTAINS
are Cohesion extensions. String predicates are ordinal, case-sensitive and UNKNOWN unless
both operands are strings. IN accepts a list-valued expression, including parameters. LIKE, BETWEEN, =~,
EXISTS, quantified predicates and nested property access remain COHDBL001; IN (1, 2)
is GQL0002 naming a list value.

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
strings. Structured engine diagnostics carry code-free messages; ExecutionFailure text is
`COHDBG013: message`, with [negotiated diagnostics](#wire-diagnostics-1105) before Error.

Expressions have no count limit. Recursive depth follows the thread's stack: GQL0009 during
parsing, COHDBG008 in the engine. See [DESIGN.md](DESIGN.md#scalar-where-expressions-1140)
for the grammar, precedence, promotion decisions, comments and reference evidence.

## Wire diagnostics (#1105)

On negotiated protocol 2.1 with the diagnostics capability, Warning and Error diagnostics retain their complete code, severity, message and source span over the model wire. Information diagnostics are omitted. A negotiated 2.0 connection retains the existing Error text and result frames. Language admission and execution semantics are unchanged.
