# Cohesion GQL dialect

The target is ISO/IEC 39075:2024 with the subset and extensions recorded in [DESIGN.md](DESIGN.md).
The supported-clause matrix there is authoritative; RETURN projects value expressions,
named bindings or all bindings with `*`.

GQL retains `<-` as its incoming relationship arrow (#1647), enabled explicitly by its
language profile. `<-[r]-` and `<-` are incoming; `<-[r]->` and `<->` are left-or-right;
`-[r]->` and `->` are outgoing. `GqlLeftArrowConformanceTests` pins these forms.

Double-quoted identifiers double an embedded `"`: `"a""b"` names `a"b` in variables,
labels, relationship types, property keys and aliases. Rendered label expressions and
source-free projection names use the same escape. A missing closing quote is CG00003.
Catalog metadata must fit one 8,092-byte encoded record; an oversized name or schema name
fails with CG02009 and preserves the session (#1235).

Unsigned property parameters retain their CLR types at storage. Type checking classifies
byte as Int16, UInt16 as Int32, UInt32 as Int64 and UInt64 as Decimal without loss; it does
not rewrite persisted values. Negotiated graph.composite-values preserves those unsigned
runtime types on the wire, while unnegotiated projections fail with CG07016. This is the
documented owner default for #1235, preserving #1146's value contract.

## Storage integrity errors (#1160)

A read that detects storage corruption fails with `CK06003`, naming the database and
page and recommending a verified backup. It never succeeds with the damaged data omitted.
The error is an execution failure over the wire; the connection remains usable. A
legitimately reclaimed location retains its existing absence semantics. The engine's
storage-integrity conformance tests cover corrupted media through the executing language.

Malformed catalog records and ordinary index/storage failures report `CG06006`.
Catalog write conflicts abort with `CG04007`; disposal during execution or at the wire
statement's lifetime guard reports `CG09019`. These are ExecutionFailure responses and
leave the wire session ready. An aborted explicit transaction requires rollback before
further statements. The engine's `GraphEngineFailureWireTests` supplies the fault corpus.

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
is CG00002. Parenthesized bodies repeat as a unit: `(a) ((x)-[:T]->(y)){2} (b)`, with adjoining
iteration endpoints constrained together. Zero repetitions test both outer nodes on the same node;
unknown inner names warn without removing that row. A bare nullable subpath also anchors on one
graph node, unlike Neo4j. Mixing Cypher lengths and ISO subpaths in one clause is accepted.

The sum of upper bounds, or lower bounds for unbounded segments, times body hop counts is at most
64 (CG00005 for text, CG05001 for direct ASTs). Unbounded segments use implicit trails, a deliberate
ISO deviation from the requirement for a mode/selector. An eligible 65th relationship after type
and direction, bound-identity and own-property filtering raises CG05004; endpoint predicates do not hide a visited 65th hop. An unordered read LIMIT may stop before visiting that branch (D4). The
256 MiB default retained-memory and 1,000,000-unit physical-work budgets also apply. Results materialize before wire output.

Variables inside quantifiers are lists in source order, including under RETURN * and WITH *.
FOR/UNWIND preserve the singleton entity kind; list expressions and aggregates can consume them.
Using a group as a singleton, accessing `r.w`, deleting the list, or referencing a group in a map
inside a repeated segment is CG03003. Unnegotiated composite projections are CG07016;
negotiated Execute returns a provably empty Max=0 group as `[]` and retains CG05001
for possible nonempty lists of nodes or relationships. Group provenance survives result-preserving
expressions and container access. Project scalar values
after FOR/UNWIND, or project a full named path through ExecutePaths. No wire format is added.

Node and INSERT/CREATE quantifiers are CG00002. Questioned paths after an edge or subpath,
subpath assignments, inner WHERE and nested quantifiers remain named CK00001. Explicit TRAIL
remains CK00001. A touching `--` after `}`, `+`, `*` or a subpath's `)` is CG00008.
Subpath parsing uses CG00009 on stack exhaustion, with no separate text nesting cap; direct AST
nesting beyond 128, cycles and repeated path instances in one walk are CG05001.

A NULL binding in a later pattern matches nothing. Property access, labeled predicates and
ordinary comparisons over NULL yield NULL/UNKNOWN; IS NULL is TRUE. RETURN expressions and
DISTINCT reuse the existing exact numeric and NULL rules. DELETE ignores NULL and counts only
entities actually deleted. INSERT with a NULL endpoint fails with CG03003 and rolls back.
Unknown optional labels/types yield NULL bindings and read-only CG01010/CG01011 warnings.

ExecutePaths rejects a projected variable bound only by optional clauses with CG05001 while
planning, even when every row would match. A mandatory binding remains projectable. Scalar
Execute already represents NULL without a wire change. EXPLAIN describes ordered clause joins
and optional filters/null extension. ISO `OPTIONAL { ... }` and `OPTIONAL ( ... )` block operands
remain recognized but outside T1 (CK00001 naming OPTIONAL BLOCK OPERAND). MANDATORY MATCH
remains CK00001. MATCH after INSERT or DELETE is a supported linear stage.

## Adjacency work and EXPLAIN (#1532)

Fixed and quantified hops select relationship direction and catalog type IDs before candidate
expansion, including Boolean type expressions and statement-local INSERT edges. A self-loop
matches once in an either-direction hop. Safe relationship and endpoint constraints precede
viable descent; deferred predicates retain their final checks. Physical work counts anchor
candidates plus selected visible relationships and retains its 1,000,000 safety cap even for
dead ends. CG05004 includes actual physical and viable totals when that cap is exceeded;
The 64-hop trail limit remains. #1533 replaces the fixed binding count with a 256 MiB default retained-memory budget (CG05020); explicit engine ceilings and lower request limits are supported.
Incident ranges are read incrementally with cancellation, merging type/direction streams in
relationship identity order. Reaching the expansion cap closes every active parent cursor;
only bounded index batches and one decoded head per selected range are prefetched (#1542).

EXPLAIN Expand details include adjacency=(node,direction,type_id,relationship), effective
seek_direction, type_filter, physical=visited_candidates, viable=accepted_hops and
physical_limit=1000000. These describe counting rules; EXPLAIN executes no hops and has no
runtime totals. The engine's GqlProfileExecutionTests EXPLAIN corpus pins this description.

## SET and REMOVE

SET and REMOVE are source-ordered stages after an incoming clause. Leading SET/REMOVE is
CG00002. `SET n.p = expression` writes a scalar; NULL removes the key, while INSERT retains
explicit NULL properties. `SET n = {map}` replaces all properties, leaving labels unchanged;
Cypher `SET n += {map}` merges. NULL map values remove their keys. Items evaluate left to right,
and each row observes earlier rows' writes: k occurrences of one node with `SET n.x=n.x+1`
increment it k times. The final working copy is published once per updated entity per statement.
Type-and-value-identical assignments, adding an existing label and removing an absent key/label
are no-ops. Integer 1 and floating 1.0 are different stored values.

`SET n IS Admin`, `SET n:Admin` and D3 `SET n:A:B` add node labels; REMOVE accepts the same
label spellings and `REMOVE n.p`. Relationships allow property changes and retain their one
immutable type; setting or removing relationship labels is CG03003. Path, group and scalar
targets are CG03003. OPTIONAL NULL targets do nothing and count zero. SET/REMOVE refresh
all carried occurrences, including entities inside lists, maps and paths. New labels and keys
use INSERT's catalog discovery and schema validation; later MATCH stages see them.

Only literal whole maps are supported: `$p` and `properties(other)` as whole-map operands
are CK00001. Literal composite properties, including parenthesized literals, are CK00001; computed composite property values
are CG03003. MERGE, including both ON CREATE SET and ON MATCH SET, remains one CK00001
naming MERGE. `+=` must touch; GQL combines it into one operator token without changing the
shared lexer. SHOW remains read-only (CG00007).

Affected counts include created entities plus distinct changed pre-existing entities. An entity
created then updated counts once. Mutating RETURN statements report this count too. An entity
still counts and publishes a replacement if a later item restores its original value, preserving
Snapshot write conflicts. Only assignments that were all no-ops skip storage. INSERT followed
by SET stores the final working copy once, with the same identity throughout the statement. ReadCommitted
updates refresh their statement snapshot after acquiring the writer lock; Snapshot updates use
first-updater-wins. ExecutePaths refuses either mutation stage with CG05001.

## RETURN

RETURN accepts optional DISTINCT/ALL and either `*` or comma-separated expressions with optional
AS aliases. ALL is the default. Standalone `RETURN 1 + 1 AS two` returns one row containing 2.
RETURN uses WHERE's value expressions, including named parameters, arithmetic, concatenation,
IS null/truth tests and labeled predicates. An unaliased column uses normalized source text:
comments removed and token spacing normalized, preserving spelling, quoted text and grouping.
This naming policy is the owner's default; the available ISO 14.11 preview does not establish
implicit computed names (see [the standards check](DESIGN.md#return-expressions-and-duplicate-elimination-1141)).

RETURN * lists named bindings in first-declaration order, omitting anonymous and repeated
elements; `(a)-[r]->(b)` yields a, r, b. Quantified variables are included as lists. With no named binding, it is CG00002. DISTINCT removes
duplicate projected tuples, treating NULLs as equal, numbers by exact value across runtime types,
strings ordinally, entities by ID and paths by ordered entity identities. The retained row keeps
its values' runtime types. Comparisons and IN use the same exact numeric values: 0.1m = 0.1d
is FALSE, 1 = 1.0 is TRUE, and mixed integer/Double comparisons above 2^53 retain every
integer bit. Signed zeros compare equal; NULL comparisons remain UNKNOWN while DISTINCT
groups NULLs. Overflowing numeric text such as `1e999` is a parse error, CG00004.
NaN and infinities supplied as AST literals or parameters fail planning with CG03003.
Arithmetic retains its promotion rules.
Bare binding references also permit entity equality and IS NULL tests in value expressions;
entity/path equality compares identity across independent materializations.

The wire Execute gate uses static planned projection kinds before data access or mutation.
`MATCH (a) RETURN *` now parses and fails with ExecutionFailure containing ExecutePaths
(previously ParseFailure). `INSERT ... RETURN a` and `INSERT ... RETURN *` fail before writing.
Computed scalar columns such as `a.age + 1` pass; EXPLAIN returns scalar descriptions even for
entity projections. ExecutePaths accepts one bound variable, DISTINCT included, or star over one
named binding. Computed/multiple columns or star over several bindings are CG05001. The
GraphPathResult and negotiated completion diagnostics keep their existing wire 2.1 shape.

Final column names, both implicit and explicit, must be unique ordinally and case-sensitively.
Duplicates raise planning diagnostic CG01015 before reads or mutations, even under EXPLAIN
or on empty matches. Path-request shape errors retain precedence. Thus `RETURN 1 AS x, 2 AS x`
fails, while `RETURN 1 AS x, 2 AS X` succeeds.
Unbound projection variables/parameters are CG05001; `p.name` on a path is CG03003.
`RETURN a {.name}` is CK00001 naming MAP PROJECTION. Immediate return-after-delete remains CK00001. NEXT and WITH, including WITH DISTINCT, execute as described below. CASE and composite values are described below.

## LET, FILTER, FOR/UNWIND, NEXT and WITH

A statement is an ordered binding-table pipeline. LET binds one or more new variables (`LET x = 1, y = x + 1`); FILTER and FILTER WHERE keep TRUE only. FOR x IN list and Cypher UNWIND list AS x expand lists and preserve NULL elements. A NULL list produces zero rows. Non-lists, including strings and byte[], give CG02013. This deliberately differs from Neo4j's scalar UNWIND, which produces one row. FOR WITH ORDINALITY/OFFSET is outside T1 (CK00001).

RETURN ... NEXT and Cypher WITH project a new scope using the same expressions, aliases, star and DISTINCT/ALL as final RETURN. Only aliases and bare variables are referenceable afterward; unaliased computed columns are allowed but unreferenceable, an explicit superset divergence from Cypher. WITH DISTINCT compares tuples with RETURN's exact numeric comparer. RETURN * includes LET/FOR variables and expands carried columns in projection order. NEXT and FOR are also accepted as variable names and AS aliases.

WITH supports GROUP BY, ORDER BY, OFFSET/SKIP, LIMIT and WHERE. Without DISTINCT/grouping, ORDER BY and WHERE can read incoming variables as well as aliases; aliases shadow the incoming scope. With DISTINCT/grouping they read only projected/grouped values. WHERE filters after ordering and paging. Int64 COUNT metadata, entity kinds, optionality and possible composite values survive projection boundaries.

NEXT requires RETURN on a read segment, and cannot trail a statement. A modifying segment without RETURN passes one empty binding and no variables to NEXT. INSERT and DELETE run in source order; deleted variables leave scope (a later reference is CG03003). Statements see their own writes, so INSERT (:T {k:1}) MATCH (t:T) RETURN t.k works even for a new label. INSERT maps evaluate scalar expressions against each row, enabling FOR name IN $names INSERT (:Person {name:name}); composite properties remain unsupported.

LET/FOR cannot rebind a name in scope (CG03003). Dropped or unaliased variables are unbound (CG05001). Standalone ordering/page stages remain CK00001, including after the new clauses. Immediate RETURN after DELETE remains CK00001. FOREACH, CALL, NEXT YIELD and UNION remain excluded. Read stages stream owned slot bindings; mutation target tables materialize within the shared memory budget. The final projection alone drives scalar/composite wire admission; read-only composed entity/path results use ExecutePaths, while any write stage rejects a path request.

## Aggregates and GROUP BY

RETURN accepts COUNT(*), COUNT(expr), SUM, AVG, MIN, MAX and COLLECT_LIST, with optional
DISTINCT or ALL inside the parentheses. Cypher collect is an exact alias of COLLECT_LIST.
COUNT(DISTINCT *) and COUNT(ALL *) are CG00002. Every non-aggregated RETURN item is an implicit grouping key;
ISO `GROUP BY` must list exactly those items' names, in any order; equivalent input expressions
are also accepted. Plain variables resolve to RETURN names first; aggregate aliases are CG05001.
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
any retained input is Decimal. Numeric errors use CG02013. MIN/MAX preserve the chosen
input's type and use ORDER BY's exact numeric and composite ordering. DISTINCT inside an
aggregate and grouping use RETURN's exact equality, including NULLs inside lists/maps.
COLLECT_LIST returns a list of non-null inputs, preserving duplicates and input order unless
DISTINCT is specified. Wire lists use the negotiated graph.composite-values capability.

Empty/all-NULL input yields count 0, sum 0, avg/min/max NULL and collect an empty list.
An aggregate without grouping keys always returns one row; an empty keyed match returns
none. Grouping precedes projection, RETURN DISTINCT, ORDER BY, OFFSET/SKIP and LIMIT,
so ordering by an aggregate alias sees the computed group value. Neither paging nor grouping
reduces the input match bound. Nested aggregates, aggregates in WHERE/GROUP BY, unequal
GROUP BY keys and ungrouped ORDER BY references fail at planning with CG05001.
ExecutePaths retains its existing CG05001 projection message. HAVING, STDDEV_SAMP,
STDDEV_POP, PERCENTILE_CONT and PERCENTILE_DISC are CK00001; WITH uses the same aggregation stage.

## ORDER BY, OFFSET, SKIP and LIMIT

Queries without ORDER BY have no guaranteed row order. A later-position anchor can change their
result row order while preserving the order of elements within each matched path.

After RETURN or WITH, `ORDER BY expression [ASC|ASCENDING|DESC|DESCENDING] [NULLS FIRST|LAST]` accepts multiple keys,
RETURN aliases and projected expressions. ASC is the default. NULL sorts last in ASC and
first in DESC (Cypher); explicit placement overrides it. This differs from Cohesion SQL's
NULL-first ASC. NULLS, FIRST and LAST are contextual words and remain available as names.
Strings sort ordinally and numbers compare exactly. The ascending family order is map,
node, relationship, list, path, string, Boolean, number, NULL. Lists are lexicographic;
maps compare count, sorted ordinal keys, then corresponding values. Nested NULL sorts last.
Equal keys retain input order. CASE, COALESCE and composite values can supply keys.

Without DISTINCT, keys may use unprojected bindings; with DISTINCT they must reference
projected values (CG05001 otherwise). DISTINCT precedes sorting. OFFSET (Cypher SKIP)
then drops rows and LIMIT truncates them. OFFSET/SKIP must precede LIMIT; LIMIT followed by OFFSET/SKIP, duplicate
OFFSET/SKIP/LIMIT and ordering after paging are CG00002. Counts are unsigned Int64 literals
or `$name`/`@name` parameters. Invalid literal forms are CG00002 and overflow is CG00004.
Parameters must hold non-negative integral values in the signed 64-bit range; invalid
graph values, including NULL and floating types, fail CG02013 before reads or writes. Non-finite and unsupported CLR values fail CG03003 during binding.

Paging never reduces mutation inputs. For reads, LIMIT without ORDER BY can stop upstream work,
while ordered finite pages retain a top set bounded by OFFSET + LIMIT. Grouping consumes its
input incrementally. Final rows and paths remain materialized within the statement memory budget;
the default 256 MiB grant preserves the former 100,000-row envelope for one-property SET,
FOR/INSERT, entity RETURN and QueryPaths. Capacity depends on row width and retained intermediate
state: larger workloads can still fail with CG05020. Raise GraphDatabaseEngineOptions.QueryMemoryBudget
(and QueryWorkBudget when needed) on the server to increase capacity; in-process GraphQueryLimits
can only lower those ceilings. TraverseAsync also uses these grants and now has a default
1,000,000-unit physical-work ceiling (CG05004), where it previously had no cap. Row and path
requests share this stage (#1533, D4).
Standalone ORDER BY/page statements before RETURN remain CK00001. WITH and aggregate ordering execute through the same per-stage pipeline. See [ordering design](DESIGN.md#ordering-and-paging-1142).

## Core functions and CAST

The [complete function table](DESIGN.md#core-scalar-functions-and-cast-1148) records ISO forms,
Cypher aliases and Cohesion extensions, signatures, result types and strict NULL behavior.
Element/path functions include element_id (elementId), labels, type, properties, keys,
startNode/endNode, path_length (length), nodes and relationships (edges). IDs are stable
strings; id() is rejected with guidance to element_id. Labels and keys are ordinal-sorted;
path lists retain traversal order and non-null element proofs through aliases. Entity-valued results fail the static wire Execute gate
before writes; scalar property access through them is legal. labels/keys/properties lists/maps
require the existing graph.composite-values capability.

Strings support lower/upper (toLower/toUpper), trim, substring, replace, char_length/
character_length and size over strings or lists. Character counts and substring indexes count
Unicode code points. Cypher substring is **0-based**, with an optional length. Floating coordinates truncate toward zero before validating
the resulting nonnegative Int32 range; invalid ranges use CG02013, past-end gives empty string. SQL's
1-based SUBSTRING FROM/FOR spelling is not part of this GQL subset. Casing is invariant;
TRIM removes U+0020 spaces, REPLACE is ordinal and bounds expansion before allocation, including
empty-search insertion: replace('abc','','X') yields 'XaXbXcX'.

Math supports abs, ceil/ceiling, floor, round, sqrt, mod, ln and log. abs/ceil/floor/round
preserve the input numeric type; unsigned inputs become Decimal. Neo4j returns Float for
ceil/floor/round, so this differs while preserving exact integer equality above 2^53.
Round ties go **away from zero**; optional precision is nonnegative. sqrt and logarithm
invalid domains fail with CG02013, never NaN. ISO ln(x) and log(base,x) are supported;
Cypher log(x) is an extension returning the natural logarithm. MOD shares % promotion and
zero-divisor behavior. NULL input returns NULL before signature kind/domain checks.

CAST supports INT/INTEGER, INT64/BIGINT, REAL, FLOAT/FLOAT64/DOUBLE [PRECISION], DECIMAL/NUMERIC,
STRING/CHAR/VARCHAR, BOOL/BOOLEAN, DATE, ZONED DATETIME and TIMESTAMP WITH TIME ZONE.
It follows Cohesion SQL's shared conversions: a fractional-to-integer or invalid-value cast
raises CG02013. Nonzero DOUBLE/REAL underflow to zero also raises CG02013; DECIMAL
converts directly through invariant text to REAL, avoiding double rounding. Timestamp-to-string
uses optional fractional digits (SQL FFFFFFF), omitting whole-second fractions. INT32, SMALLINT,
FLOAT32, TIMESTAMP WITHOUT TIME ZONE and LOCAL DATETIME are recognized unsupported CAST
targets (one CK00001 naming the complete type). Cypher toString/toInteger/toFloat/toBoolean are extensions: toInteger truncates
fractions, Boolean converts to 1/0, and unparsable conversion strings give NULL. Numeric
string/Decimal overflow still fails; toInteger saturates finite floating overflow as Neo4j does. CAST renders Boolean strings uppercase; toString renders them lowercase.

CURRENT_DATE and CURRENT_TIMESTAMP are niladic without parentheses, returning UTC DateOnly
and DateTimeOffset. One instant is captured before beginning the transaction: all rows and
stages, and all statements in an explicit API transaction (also ReadCommitted), reuse it.
Every autocommit statement captures its own; START TRANSACTION / COMMIT / ROLLBACK on one
wire connection share the explicit transaction instant.
The clock seam remains internal. Dates and timestamps compare/sort by date or UTC instant;
total ordering places zoned timestamps then dates between paths and strings. Temporal values
cannot become stored graph properties: proven kinds fail planning with CG03003 for MATCH
and INSERT maps; dynamic values fail with CG02013 and the statement rolls back.

Unknown calls use CG00010 naming the function. Scalar/aggregate parsed arity uses CG00002;
hand-built invalid arity and unbound arguments use plan-time CG05001. Provable wrong kinds
use plan-time CG03003; dynamic wrong kinds and value failures use CG02013. EXPLAIN checks
signatures without evaluating calls. toBoolean requires integral numeric kinds; Boolean
numeric CAST is CG03003. Ordinary function names remain valid variables and keys.
CURRENT_DATE, CURRENT_TIMESTAMP, CURRENT_TIME, LOCAL_TIME and LOCAL_TIMESTAMP are ISO reserved
words: every unquoted binding site is CG00002; quoted names bind and are referenced quoted.
CAST is contextual: followed by `(` it starts CAST; elsewhere it is an identifier, as in
`MATCH (cast:Movie) RETURN cast.title`.
Recognized unsupported calls (reduce, temporal constructors, head/last/tail/reverse/range,
ELEMENTS, CARDINALITY, LTRIM/RTRIM/BTRIM, LEFT/RIGHT, LOG10 and Cypher
toStringList/toIntegerList/toFloatList/toBooleanList) keep CK00001 in call position, preserving variables such as date.
CURRENT_TIME/LOCAL_TIME/LOCAL_TIMESTAMP and procedures/CALL also remain CK00001.
Runtime errors precede ResultHeader and retain the pooled session. Every declared function
and alias has live evidence in the profile coverage guard.

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
a list. Invalid index/key/container types use CG02013. Stored a.b.c, comprehensions and
slicing remain CK00001. INSERT/CREATE composite properties stay unsupported: syntax is
CK00001 naming graph storage, parameters are CG03003. (n $props) remains CK00001.

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
Constructed values exceeding depth 64, 65,536 values or 16 MiB fail with CG02013 inside the
statement transaction, including in process; the statement's writes roll back.

Wire composite values require graph.composite-values under existing protocol 2.1 negotiation.
Without it, projections that may return composites fail statically with CG07016 before writer locking and parameters are
refused locally. The codec bounds are depth 64, 65536 total values and 16777216 bytes.
Malformed components are ProtocolViolation. Path result encoding is unchanged.

## Named scalar parameters

Node/relationship property maps in MATCH, INSERT and CREATE, WHERE operands and RETURN expressions accept
`$name` and `@name`. `$name` is the ISO/Cypher spelling; `@name` is a Cohesion extension.
Names bind by their exact, case-sensitive bare name; one leading sigil on a map key is ignored.
`$1` binds key `"1"`, never a positional slot. Supplied NULL is bound; unused values are ignored.
Normalized-key collisions fail with CG01014, missing bindings with CG05001, and unsupported
or non-finite values with CG03003, all while planning. EXPLAIN applies the same checks.
Runtime scalar types are preserved (including Int32 and Decimal); schema type matching uses the
typed graph API's promotion. Lists/maps are admitted in value positions; pattern property maps remain scalar-only. See
[DESIGN.md](DESIGN.md#named-scalar-parameters-1102-1138) for the complete contract and hand-offs.
`$$name` and `(n $props)` remain CK00001; bare sigils and parameters in
identifier/DELETE positions are CG00002. #1102's `?` and `:name` diagnostics are unchanged.

## WHERE expressions

WHERE accepts AND, OR, XOR, NOT, comparisons, bare Boolean properties, IS [NOT]
NULL/TRUE/FALSE/UNKNOWN, numeric + - * /, % (Cypher MOD spelling), `||`, string +,
labeled predicates and parentheses. IN [items], STARTS WITH, ENDS WITH and CONTAINS
are Cohesion extensions. String predicates are ordinal, case-sensitive and UNKNOWN unless
both operands are strings. IN accepts a list-valued expression, including parameters. LIKE, BETWEEN, =~,
EXISTS, quantified predicates and nested property access remain CK00001; IN (1, 2)
is CG00002 naming a list value.

WHERE keeps TRUE only. Logical operators use three-valued logic. Incomparable non-null values
are unequal; UNKNOWN is a Boolean literal represented by NULL. Incomparable ordering is UNKNOWN.
IN evaluates every list item before testing membership; a later value error raises CG02013
even when an earlier item matches. IN over an empty list is FALSE even for NULL;
otherwise a match wins, then a NULL operand or item yields UNKNOWN, otherwise FALSE.
Thus `1 IN [1, NULL]` is TRUE and `1 IN [2, NULL]` is UNKNOWN.
IS truth tests accept Boolean/NULL and return Boolean. Evaluated non-Boolean logical values
raise CG02013.

A non-negated labeled predicate in a MATCH clause's top-level AND chain supplies required node
labels when its label expression is a pure conjunction: `MATCH (n) WHERE n:Keep RETURN n` and
`MATCH (n) WHERE n IS LABELED Keep RETURN n` anchor like `(n:Keep)`. Required labels also enable
property index seeks with inline or conjunctive WHERE equalities. Bound entities take precedence,
then property seeks; otherwise the smallest retained-posting estimate across required labels
and pure pattern unions chooses the anchor, breaking ties by source position. Estimates include
history and affect plan cost only.
Any node position can anchor, including `(a)->(b) WHERE b:Keep`; EXPLAIN shows the chosen label
and node ordinal. The complete predicate still filters candidates. MVCC label postings limit
decoding to matching visible nodes, including a statement's pending additions and replacements.
Conjunctions seek the cheapest required label and retain the complete residual.

OR, XOR, NOT, IS NOT LABELED, truth tests and label expressions using `|`, `!` or `%` supply no
required labels. Requirements remain local to their MATCH clause. Pure pattern unions such as
`(n:A|B {k:1})` merge label ranges, suppress duplicate identities and compete by their summed
estimate with other positions, including `m:C` in `(n:A|B)->(m:C)`. EXPLAIN describes a union as
LabelScan with entity `A|B` and detail `combine=union`. Negation, wildcard and mixed expressions
keep their existing safe scan behavior. No result ordering is guaranteed without ORDER BY.

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
is 0, including Int64.MinValue; Int64.MinValue / -1 raises CG02013. Zero divisors, overflow,
non-finite results and invalid arithmetic types raise CG02013; concatenation requires two
strings. Structured engine diagnostics carry code-free messages; ExecutionFailure text is
`CG02013: message`, with [negotiated diagnostics](#wire-diagnostics-1105) before Error.

Expressions have no count limit. Recursive depth follows the thread's stack: CG00009 during
parsing, CG05008 in the engine. See [DESIGN.md](DESIGN.md#scalar-where-expressions-1140)
for the grammar, precedence, promotion decisions, comments and reference evidence.

## Wire diagnostics (#1105)

On negotiated protocol 2.1 with the diagnostics capability, Warning and Error diagnostics retain their complete code, severity, message and source span over the model wire. Information diagnostics are omitted. A negotiated 2.0 connection retains the existing Error text and result frames. Language admission and execution semantics are unchanged.

## Structured failure tracing (#1350)

Thrown statement failures report their structured diagnostic code without exposing statement
text in tracing. Offline and unconfirmed-commit exception detail is unprefixed; Diagnostics
carries the existing model code. This adds no language syntax or diagnostic number.

## Safe index-history reclamation (#1195)

Background and unique-insert B-tree pruning preserve the statement's snapshot and all existing
language results. The shared coordinator supplies the safe horizon; ending the oldest reader
allows later maintenance to remove its dead physical index versions. This changes no syntax,
diagnostic code, wire contract or on-disk format.


## Storage decode failures (#1372)

A malformed confirmed stored record fails execution with the existing corruption diagnostic
and physical page/slot context. It is not a missing row, document or graph element. D1 confirms
the copy before decoding; this adds no syntax, SQLSTATE or format version. Valid data retains the same language results.

## Transaction control (#1540)

`START TRANSACTION`, `COMMIT` and `ROLLBACK` are standalone ISO statements, including over Execute.
START uses Snapshot; embedded callers retain typed Snapshot/ReadCommitted transaction APIs.
A failed explicit transaction refuses work until rollback. BEGIN, savepoints, transaction options,
EXPLAIN controls and multi-statement text are unsupported. ExecutePaths cannot control transactions.

An idle open wire transaction holds the database writer lock once it has acquired it, until
COMMIT, ROLLBACK, disconnect, pool return or IdleTimeout (30 minutes by default). Keep transactions
short: an idle writer blocks other writers. New clients discard sessions against pre-#1540 servers
that cannot negotiate transaction-status, so those servers are not pooled.

## Graph diagnostic conventions (#1470)

Exception and diagnostic templates use the project's build-generated `ErrorMessages` resources.
Parameterized templates format with the current culture; abort/cancellation templates used in
normal cleanup are cached. The [Database error-code registry](../../Assimalign.Cohesion.Database/docs/ERROR-CODES.md)
assigns this family its two-letter prefix and categorized five-digit identities.
Existing SQLSTATE meanings, numeric wire error kinds, frame layouts and disk formats are preserved.
Structured diagnostics keep their code outside the message. Compiler diagnostic identifiers and
standard .NET argument guards retain their own contracts.

## Model failure producer completion

GQL syntax and evaluation are unchanged. Engine state and result-access refusals carry the
[model producer diagnostics](../../Assimalign.Cohesion.Database.Graph/docs/DESIGN.md#model-failure-producer-completion);
the existing wire formatter includes the code once and keeps exception detail code-free.
Direct catalog failures preserve their concrete exception type and non-null Diagnostics.
No SQLSTATE mapping, protocol version or storage format is added.

The result-header size guard uses the same `CG02013` value-limit diagnostic as row-size
validation, retaining the existing limit detail and ExecutionFailure category. A synthetic
header regression exercises the guard directly through the existing runtime test seam;
ordinary projection admission and the 16 MiB cap are unchanged.

## Registered developer functions

Graph engine requests admit identifier calls registered in that engine builder's Functions
collection, including aggregate DISTINCT/ALL arguments. Disjoint argument-count overloads are
supported; same-count type overloads and scalar/aggregate name sharing are refused. Star is
still COUNT-only. Unknown deferred names retain CG00010 and unsupported registered arities are refused by planning with
CG05001; the default standalone parser keeps CG00010. Unexpected callback/contract failures
use CG05021, without altering native CG03003/CG02013 behavior. No window, HAVING, persisted
function catalog, new message or format version is added. OVER followed by parentheses is
recognized as unsupported WINDOW OVER (CK00001), even inside a custom call; over remains a
legal binding, label, alias or property name. Result-shape declarations participate
in pre-write admission and aggregate retained-state declarations in the statement memory budget.

Engine requests admit named calls and resolve them against the executing catalog. An unknown
name retains CG00010 but now fails during binding/execution: the wire category is ExecutionFailure,
and no StatementParseFailed event is raised. Standalone parsers retain unknown-name parse
diagnostics. This changes error categorization, with no frame-layout or protocol-version change.
