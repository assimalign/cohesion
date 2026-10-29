# OQL SQL++ dialect

## Text transactions (#1725)

In-process and wire Documents sessions accept `BEGIN [WORK | TRAN | TRANSACTION]`,
`START (WORK | TRAN | TRANSACTION)`, `COMMIT [WORK | TRAN | TRANSACTION]`, and
`ROLLBACK [WORK | TRAN | TRANSACTION]`. A start optionally takes
`ISOLATION LEVEL SNAPSHOT` or `ISOLATION LEVEL READ COMMITTED`. Snapshot is the default,
intentionally differing from Couchbase SQL++'s READ COMMITTED default. Bare BEGIN and explicit
SNAPSHOT extend the [SQL++ grammar](https://github.com/couchbaselabs/docs-devex/blob/c3bb1549af4887019b9983e371d9e8b4f569052c/modules/n1ql/partials/grammar/tcl.ebnf#L9-L28).
Transaction words remain contextual, including implicit FROM and DML aliases named `savepoint`,
and each command returns Success with affected count zero.
Controls ignore supplied parameters, including duplicate names in a wire parameter object.
For ordinary wire statements duplicate names give CD01009 and fault an explicit transaction.
Syntax diagnostics take precedence over duplicate parameter names, since parsing identifies controls.

Commands use the session's existing transaction, including one begun through its C# API.
COMMIT or ROLLBACK without an open transaction gives CD04104. Nested BEGIN gives CK04004 and
aborts the existing transaction. A failed statement preserves CD04101 until rollback;
text ROLLBACK recovers the session. COMMIT on a faulted transaction gives CD04101 and ends it
without committing. Malformed text in a faulted transaction also preserves CD04101.
Wire command validation and response serialization failures also abort explicit work; a response
size failure retains CD05016 before later statements receive CD04101.
OQL CREATE/DROP COLLECTION/INDEX in an explicit transaction gives CD04105 and aborts it,
including conditional no-ops. C# collection operations retain their existing transaction contract.
SAVEPOINT, ROLLBACK TO SAVEPOINT and SET TRANSACTION remain CK00001; the rollback form reports
the unsupported clause at `TO`. EXPLAIN accepts no
transaction control. Unsupported isolation spellings are CD00002.

On InMemory and TCP, controls require the negotiated transaction-status capability; every control
from a 2.0 or unnegotiated 2.1 peer receives CD07002 before touching a transaction. The session
remains usable for ordinary autocommit work. Controls run on the connection-owned session. Documents negotiates
transaction-status and sends Ready after Execute and Command, including complete failures.
Returning a pooled connection rolls back active or faulted work and requires Idle before reuse;
legacy peers without negotiated status are discarded. Cancellation or disconnect before a terminal
response discards the session and leaves an unacknowledged commit's outcome unknown. Once Complete
has arrived, bounded status cleanup ignores caller cancellation. No request identifier, protocol
version or persisted format changes. CD07002 remains live for this capability requirement.

## Expression subqueries and EXISTS (#1731, OQ-Q1)

A parenthesized SELECT is an expression returning an array with zero, one or many elements;
there is no scalar-cardinality error. SELECT RAW and SELECT VALUE return projected values;
ordinary named SELECT projections return objects with the usual omitted-MISSING members.
A lone star retains object roots; a nonobject root is wrapped under `document`, so ordinary
subquery elements remain objects. Star results deliberately follow this engine's top-level
SELECT-star shape instead of Couchbase's alias-keyed form. Top-level SELECT star keeps its
existing stored-value rule. RAW/VALUE MISSING
becomes a null array element. FROM remains required in every SELECT. Subqueries may nest and
appear in SELECT projections, WHERE, GROUP BY, HAVING, ORDER BY, function arguments, constructors,
CASE, comprehensions, quantified predicates and UNNEST operands. Each nested query has its own
metadata, grouping and binding scope. See the pinned
[SQL++ subquery contract](https://github.com/couchbaselabs/docs-devex/blob/c3bb1549af4887019b9983e371d9e8b4f569052c/modules/n1ql/pages/n1ql-language-reference/subqueries.adoc#L26-L68).

`EXISTS expression` returns TRUE for a nonempty array, FALSE for an empty array, MISSING for
MISSING, and NULL for NULL, scalars or objects. NOT EXISTS composes with ordinary negation.
EXISTS binds below IS presence tests, IS [NOT] DISTINCT FROM, CONCAT (`||`) and arithmetic,
and above IN, BETWEEN, LIKE, comparisons,
NOT, AND and OR. Thus `EXISTS x IS NULL` means `EXISTS (x IS NULL)`, while `EXISTS a = TRUE`
means `(EXISTS a) = TRUE` and `NOT EXISTS x AND y` means `(NOT (EXISTS x)) AND y`. These
boundaries follow the [Couchbase precedence block](https://github.com/couchbase/query/blob/6d40d1dd24e256588af2ff5bf6627ca76135397c/parser/n1ql/n1ql.y#L389-L409).
IS now binds tighter than comparisons and the operands of IN, BETWEEN, LIKE and ESCAPE.
This changes earlier OQL results: with n = 1, 2, 3, WHERE n = 1 IS NOT NULL formerly admitted
all three rows; it now means n = (1 IS NOT NULL) and admits none. The adopted SQL++ results
include FALSE for 1 = NULL IS NULL and 2 BETWEEN 1 AND 3 IS NOT NULL, NULL for
1 IN [1] IS NULL, and FALSE for 2 BETWEEN 1 IS NOT NULL AND 3. Parenthesize an entire
predicate before IS when that predicate's result is the intended operand.

The full supported expression precedence, tightest first, is below. Parentheses override it;
CASE, constructors, comprehensions and quantifiers delimit their contained expressions.
OQL retains its shared comparison/predicate tier; the adopted Couchbase boundary puts IS above
all of that tier. ESCAPE is a suffix of LIKE and parses its value with the same operand rule.

| Precedence | Operators | Grouping |
| --- | --- | --- |
| 1 | Path member and array navigation: . and [] | Left |
| 2 | Unary + and - | Right |
| 3 | * / % | Left |
| 4 | + - | Left |
| 5 | String concatenation: &#124;&#124; | Left |
| 6 | IS [NOT] NULL/MISSING/VALUED; IS [NOT] DISTINCT FROM | Left |
| 7 | EXISTS | Right |
| 8 | = != <> < <= > >=; [NOT] IN; [NOT] BETWEEN; [NOT] LIKE with optional ESCAPE | Left |
| 9 | NOT | Right |
| 10 | AND | Left |
| 11 | OR | Left |

The string operator &#124;&#124; is an addition in #1731 and uses the existing CONCAT function
and its literal-kind validation. Two strings concatenate; MISSING wins over NULL, NULL propagates,
and a wrong-kind stored value yields NULL. Incompatible literal numbers, arrays and objects give
CD05001 at planning. No implicit string conversion occurs. IS [NOT] DISTINCT FROM is also an addition in #1731. It
handles top-level NULL/MISSING explicitly and otherwise uses SQL++ equality, following
[Couchbase func_distinct.go:51-73,134-156](https://github.com/couchbase/query/blob/6d40d1dd24e256588af2ff5bf6627ca76135397c/expression/func_distinct.go#L51-L156).
For either distinctness operator, two MISSING operands or two NULL operands are not distinct;
one MISSING or one NULL against another kind is distinct. Valued inputs use structural equality:
IS DISTINCT FROM is FALSE only for definite equality, while IS NOT DISTINCT FROM returns the
underlying equality result (including NULL for arrays whose element comparison is unknown).
The conformance matrix covers MISSING, NULL, numbers, strings, arrays, objects and null elements.
WITHIN remains a named CK00001 deferred form. The truth table follows
[Couchbase coll_exists.go:40-53](https://github.com/couchbase/query/blob/6d40d1dd24e256588af2ff5bf6627ca76135397c/expression/coll_exists.go#L40-L53).
The retired ODMG EXISTS binding quantifier remains a named CK00001 refusal.

`value [NOT] IN (SELECT RAW ...)` uses existing array IN semantics. VALUE is accepted as RAW's
existing synonym. A direct SELECT without RAW/VALUE in this position gives CD00014 at SELECT.
This conservative guard is deliberately narrower than Couchbase, pending owner sign-off;
an extra pair of parentheses denotes an ordinary expression in the existing value-list grammar.
MISSING on the left propagates MISSING; NULL on the left remains NULL, even for an empty array.
A match wins over null elements; an unmatched null element yields NULL. Nested RAW MISSING rows
become null elements before membership testing, matching the existing RAW result rule.

Nested plans execute on the outer statement's operation, fixed snapshot, parameters, frozen
function catalog, transaction clock, cancellation token and retained-memory counter (CD05018).
They see the transaction's earlier writes. Deterministic uncorrelated results cache lazily by AST
identity for that statement. NOW_STR/NOW_MILLIS and every registered developer callback disable
caching, including when nested further inside the query. Their plans still bind only once per
statement. An unreached CASE, AND/OR or lazy conditional argument performs no nested execution,
consumes no nested work or retention, invokes no callback and raises no runtime error. Static
parameter, numeric, name, metadata and grouping validation still applies to every branch.

Cached deterministic arrays remain retained until the statement exits. Uncached arrays belong to
the evaluating expression or row: predicates release them immediately, while projection, grouping,
UNNEST and ordering consumers clone and charge the values they retain. Filter-only nested retention
is constant across outer rows. Arrays and output projection clones own their JSON independently.
Quantifiers and other per-element filters release discarded correlated results after each iteration,
while preserving the input collection and statement caches. IN-list operands, CASE conditions and
skipped conditional arguments have the same scratch lifetime. Object construction retains live
member values until serialization and releases discarded expression scratch per member.
Object constructors, array constructors, ARRAY and FIRST keep their returned value charged until
the enclosing consumer releases it at its iteration or row boundary.
Nested source rows, results, scratch and arrays share the existing
memory budget. A conservative reservation precedes array serialization and then shrinks to its
measured representation. Cancellation or failure releases all nested reservations. EXPLAIN binds
without executing and shows each SubqueryExpression, owning clause/span, nested access plan and
`cached_for_statement` or `per_evaluation` policy, plus captured names in `correlation`. Plans are statement-local and rebuilt on the
next execution, including a reused request after CREATE/DROP INDEX.

An unaliased FROM exports the collection name to nested SELECTs and recognizes it as the local
META receiver. Group-key matching compares captured qualified paths with unqualified keys.
The query's own paths still use only its explicit alias: items.g in FROM items reads the field
items then g. Thus SELECT items.g FROM items GROUP BY g is refused with CD05001, and
SELECT DISTINCT g FROM items ORDER BY items.g is refused with CD01014. SELECT RAW g FROM items
ORDER BY items.g sorts on that field path. Captured group-key matching strips the collection
receiver only on the captured side. META on an UNNEST variable, comprehension variable or unknown
name is refused with CD05001 before evaluation.
Local META identity ordering retains identity paging; EXPLAIN
reports identity_paging=True when this path applies. A source alias or UNNEST variable that reuses
a captured name still obeys its own query's grouping restrictions. Complete group keys are matched
before descending into their subqueries at every depth.
In ORDER BY only a standalone projection alias substitutes its projection. Direct paths inside
other expressions resolve through row and outer bindings; nested SELECTs inside ORDER BY can
capture projection aliases. Their own lexical variables retain precedence over those aliases.

Correlated subqueries capture outer FROM/UNNEST, group and comprehension bindings, and outer
projection aliases in ORDER BY. Inner declarations shadow outer names by ordinal spelling.
An unqualified field binds to the inner source.
Outer captures are constant within each invocation of an inner grouped query, including its
projection, HAVING and ORDER BY; local fields still require the normal grouping rules.
Every enclosing grouped scope also checks the capture: group keys and group bindings are visible,
while an ungrouped source path gives CD05001 just as a direct reference does. The enclosing
aggregate's row inputs may capture source fields; GROUP AS is unavailable in those inputs (CD01031).
Inner aggregates do not restore an enclosing query's hidden fields. This follows SQL++'s
[grouped-object visibility](https://docs.couchbase.com/server/7.6/n1ql/n1ql-language-reference/groupby.html#group-as-clause).
Captured paths never constrain local indexes or prove local index ordering. Only local-source
META().id / META(local_alias).id permits identity paging; ordering by captured metadata keeps
ascending inner document identity for equal keys. Metadata equivalence includes its receiver.
Repeated subquery group keys must also capture the same bindings; matching text alone does not
prove grouping when a comprehension or quantifier shadows an enclosing name.
META(outer_alias).id/version follows a captured stored source's identity. Captured expression,
derived and local-value bindings have no stored identity and return NULL for metadata.
A nested `FROM d.items AS i`, or `FROM d AS i`
where d is an explicit outer alias, iterates that outer value per row. An implicit collection name
in a bare FROM continues to name the collection. This follows the [Couchbase correlation and scope contract](https://docs.couchbase.com/server/7.6/n1ql/n1ql-language-reference/subqueries.html).
Qualified stars (`d.*`) retain their existing CK00001 refusal.

Subqueries execute in UPDATE/DELETE predicates, SET and VALUES expressions, INSERT/UPSERT SELECT
sources and RETURNING. Every nested read sees statement-start data, including writes from earlier
statements in an explicit transaction, and excludes this statement's writes. Mutation predicates,
values and SELECT inputs finish staging before writes. RETURNING with subqueries also stages
before writes, using post-images and versions reserved from the existing durable allocator;
the eventual writes use those same versions. An aborted statement may leave allocator gaps.
No correlated result is memoized across outer rows.

`StatementWorkBudget` defaults to 10,000,000 reached expression evaluations for statements using
subqueries or expression sources. All depths share the counter, including outer expressions and
cache hits; skipped expressions consume nothing. Exhaustion gives located CD05020 and rolls back
the statement. The existing StatementMemoryBudget and cancellation apply at every depth.

Complete projections on both execution paths are limited to 256 serialized JSON levels.
RAW/VALUE and lone stars have no enclosing row object and allow 256 value levels. Named columns
and stars beside other columns allow 255, reserving one level for the row object. This also
checks developer-function JSON. Excess depth gives located CD05015; the row writer applies the
same diagnostic as a last defence, preserving the connection after a terminal Failure.
Subquery expressions are known arrays for the literal-array function-kind rule: LENGTH, ABS
and CONCAT reject them with CD05001 during planning. Constructors and ARRAY/FIRST retain
in-order evaluation, serialization and failure reachability on both execution paths. This run adds no disk,
protocol, frame or capability version. InMemory, TCP and NativeAOT use the ordinary JSON cells.

## Key JOIN and NEST (#1732, OQ-J1)

The executable key-only ANSI spelling is:

~~~sql
SELECT META(a).id AS parent, b.*
FROM parents AS a
LEFT OUTER JOIN children AS b ON META(b).id = a.childId
~~~

Each binding is [INNER] JOIN, LEFT [OUTER] JOIN, [INNER] NEST or LEFT [OUTER] NEST.
Bindings execute left to right and may interleave with UNNEST. The right-hand source must be a
stored collection in this database. ON must be exactly META(rightAlias).id = keyExpression;
parentheses are allowed. The key expression sees earlier sources and lexical outer captures,
including scalar functions, parameters and subqueries. A self or forward source reference gives
CD01035. Unqualified key fields address the initial FROM value; a field whose name collides with
a self or later source alias must be qualified with its earlier source alias.
General ON predicates, reversed equality, ON KEYS, expression/derived right-hand
sources, comma joins and joins on UPDATE/DELETE remain named CK00001 refusals.
INSERT/UPSERT SELECT and expression subqueries may contain the supported SELECT joins.

Every FROM and UNNEST source in every SELECT scope of a statement containing JOIN or NEST
requires an explicit alias, including nested SELECTs without their own joins. DML targets keep their
existing alias rules. AS is optional for collection, JOIN/NEST and UNNEST aliases; existing expression
FROM rules still apply. An inferred UNNEST/path alias does not satisfy this rule (CD00016).
This temporary restriction avoids deciding the open unaliased-collection rule in #1767.
Aliases are ordinal and unique within the SELECT (CD00010; CD01023 for direct ASTs).

Keys are ordinal strings, NULL or MISSING. Other values, including arrays, give located CD02028;
key lists are outside this scalar-key subset. INNER JOIN drops unmatched inputs; LEFT JOIN binds
MISSING. NEST binds a one-element array when matched. The unmatched lookup-NEST table is:

| Key | INNER NEST | LEFT NEST binding |
| --- | --- | --- |
| Existing string | One row | One-element array |
| Absent string | No row | Empty array |
| NULL or MISSING | No row | MISSING |

This follows the pinned [ANSI key equality](https://github.com/couchbaselabs/docs-devex/blob/c3bb1549af4887019b9983e371d9e8b4f569052c/modules/n1ql/pages/n1ql-language-reference/join.adoc#L106-L123),
[LEFT JOIN missing binding](https://github.com/couchbaselabs/docs-devex/blob/c3bb1549af4887019b9983e371d9e8b4f569052c/modules/n1ql/pages/n1ql-language-reference/join.adoc#L1034) and
[lookup-NEST unknown-key table](https://github.com/couchbaselabs/docs-devex/blob/c3bb1549af4887019b9983e371d9e8b4f569052c/modules/n1ql/pages/n1ql-language-reference/nest.adoc#L307-L335).

META(alias).id/version retains each stored document's identity, independent of JSON properties.
Unmatched JOIN metadata is MISSING. Expression/derived sources and NEST arrays have no single
document identity, so their metadata is NULL; an unknown-key LEFT NEST binding is MISSING.
META() is ambiguous in a joined SELECT and gives CD01035. META on UNNEST and comprehension
variables remains invalid. Grouping, captures and ORDER BY projection aliases keep their existing
scope rules. Unqualified ordinary field paths continue to address the initial FROM value.

SELECT * yields a synthetic object keyed by the explicit FROM alias and every JOIN/NEST/UNNEST
alias; MISSING members are omitted and NULL members remain. A top-level qualified star (b.*)
is supported in ungrouped joined SELECT projections and yields that alias's value, using the existing bare
result mode when it is the sole projection. Non-join qualified stars retain their existing refusal.
Join queries support grouping, GROUP AS, HAVING, DISTINCT, ordering and paging, and execute inside
correlated subqueries. Equal sort keys retain input source order. Nonblocking joins apply paging
before projection and infer dynamic headers from the selected window, as expression sources do.

All lookups use the current operation and its snapshot. The statement caches both hits and misses
by collection identity and ordinal key, across chains and subqueries; it releases them at statement
end. Cached content and NEST arrays, dictionary entries and key strings are charged to the shared
StatementMemoryBudget (CD05018). Lookup attempts, cache hits and NEST materialization also consume
StatementWorkBudget (CD05020). No lookup opens a session operation or calls public GetAsync.
LIMIT 0 and EXPLAIN perform static validation without document lookups. EXPLAIN exposes KeyJoin,
LeftKeyJoin, KeyNest and LeftKeyNest, with the collection, alias, snapshot lookup and cache policy.
There is no disk or wire version change.

Joined ordering retains an explicit sort; EXPLAIN index candidates name key_join_requires_sort.
An array-only direct AST supplied through Bindings retains the ordinary UNNEST representation.

## Derived and expression FROM sources (#1731, OQ-Q2)

`FROM (SELECT ...) [AS] alias` requires an alias (CD00015 if absent). AS is optional,
following [from.adoc:276-282](https://github.com/couchbaselabs/docs-devex/blob/c3bb1549af4887019b9983e371d9e8b4f569052c/modules/n1ql/pages/n1ql-language-reference/from.adoc#L276-L282).
`FROM expression AS alias` accepts arrays, parameters, constructors, functions,
operators and subqueries. Generic-expression AS is required when supplying an explicit alias,
as [from.adoc:409-416](https://github.com/couchbaselabs/docs-devex/blob/c3bb1549af4887019b9983e371d9e8b4f569052c/modules/n1ql/pages/n1ql-language-reference/from.adoc#L409-L416)
specifies. An unnamed expression must have an implicit field name; literals, arrays and parameters
therefore require an explicit alias (CD01033), matching
[Couchbase from_expression.go:159-163,244-252](https://github.com/couchbase/query/blob/6d40d1dd24e256588af2ff5bf6627ca76135397c/algebra/from_expression.go#L159-L163).
An ordinary bare identifier remains a collection source. Database-qualified collection names
remain unsupported. Nested paths may capture an outer alias.
Other free source identifiers give CD01034, including under LIMIT 0. A source has no implicit
input-row scope; comprehension-local variables remain available. This follows
[formalizer.go:474-479](https://github.com/couchbase/query/blob/6d40d1dd24e256588af2ff5bf6627ca76135397c/expression/formalizer.go#L474-L479).

The documentation's [generic expression terms and mixed-value example](https://github.com/couchbaselabs/docs-devex/blob/c3bb1549af4887019b9983e371d9e8b4f569052c/modules/n1ql/pages/n1ql-language-reference/from.adoc#L391-L437)
allow JSON scalar, object and array values. The precise NULL/MISSING behavior follows
[ExpressionScan, scan_expression.go:136-153,249-250](https://github.com/couchbase/query/blob/6d40d1dd24e256588af2ff5bf6627ca76135397c/execution/scan_expression.go#L136-L153):

| Value | Input rows |
| --- | --- |
| Array | One row per element, in array order; an empty array has zero rows |
| Object | One row containing the complete object |
| Number, string or Boolean | One row containing the scalar |
| NULL | One NULL row |
| MISSING | Zero rows |

Derived sources consume the inner SELECT's rows directly. RAW/VALUE exposes each projected
value, including NULL for a RAW/VALUE MISSING; named projections expose objects with MISSING
members omitted. The alias names the complete row, and unqualified fields address its object
members. WHERE, GROUP BY and aggregates, group aliases/GROUP AS, HAVING, ORDER BY, DISTINCT,
LIMIT/OFFSET, UNNEST over source fields and further derived nesting all compose normally.

`SELECT *` on these sources produces an object keyed by the source alias (and subsequent UNNEST
bindings), following [selectclause.adoc:112-121](https://github.com/couchbaselabs/docs-devex/blob/c3bb1549af4887019b9983e371d9e8b4f569052c/modules/n1ql/pages/n1ql-language-reference/selectclause.adoc#L112-L121).
For example, `SELECT * FROM [1] AS v` produces `{"v":1}`. Stored collection stars retain their
existing shape, including when that stored SELECT supplies a derived source. META().id/version
and META(alias).id/version return NULL for expression and derived rows, which have no stored
identity; metadata explicitly projected inside the stored inner SELECT remains ordinary data.
This follows [META formalization, formalizer.go:506-521](https://github.com/couchbase/query/blob/6d40d1dd24e256588af2ff5bf6627ca76135397c/expression/formalizer.go#L506-L521) and [func_meta.go:167-192](https://github.com/couchbase/query/blob/6d40d1dd24e256588af2ff5bf6627ca76135397c/expression/func_meta.go#L167-L192).
The existing restrictions on META's argument, member and grouping placement still apply.

A source is evaluated once when its cursor is reached. After eager static validation, LIMIT 0
returns no rows with planned column names and types before opening any stored, system,
expression or derived source, including with ORDER BY, DISTINCT, grouping or aggregates.
No source, predicate, projection or callback runs; an inner derived SELECT's own zero limit
has the same rule. Simple derived SELECTs stream one row at a time, so outer LIMIT 1 pulls one
matching inner row and disposes the inner cursor immediately. Paging precedes projection on this path; dynamic headers
are inferred over the selected window. Grouping, DISTINCT and ORDER BY retain the rows they
must re-read, under the shared CD05018 memory budget. Outer ORDER BY sorts; it consumes no
inner index-order guarantee. EXPLAIN includes DerivedSource/ExpressionSource and nested plans.
All levels share the operation, snapshot, function catalog, work/memory bounds and cancellation.
Correlated sources are evaluated per outer row. INSERT/UPSERT SELECT accepts both source forms.
JOIN/NEST (including comma joins) retain named CK00001 refusals. No format or wire
version changes are required.

## RETURNING (#1727)

INSERT and UPSERT (VALUES or SELECT), UPDATE and DELETE accept a final
`RETURNING expr [AS alias], ...`, `RETURNING RAW expr` or `RETURNING VALUE expr`.
RAW and VALUE are synonyms for exactly one unwrapped expression without an alias or star.
A lone `RETURNING *` returns the complete image, preserving its stored UTF-8 bytes.
Named expressions follow SELECT's output-name, duplicate-name, type and MISSING/NULL rules;
RAW/VALUE converts a top-level MISSING to JSON null. ELEMENT is not a synonym.

DELETE projects the pre-image and its old metadata. UPDATE projects the stored post-image;
INSERT/UPSERT project the stored post-image and newly allocated durable version. The target
alias, or collection name when no alias is supplied, binds that image, including META().id
and META([binding]).version. INSERT/UPSERT SELECT source bindings do not escape into RETURNING.
Scalar functions, parameters, predicates, CASE and constructors are accepted; aggregates and
subqueries remain unavailable here. Parameters and expression contexts are checked even for
zero affected documents. A directly constructed AST with an empty RETURNING output name gives
CD01026 before mutation (empty quoted labels in text retain CD00002). EXPLAIN validates RETURNING and describes Returning/ReturningValue
without reading documents, invoking projection callbacks or performing writes.

Each affected document produces one row. INSERT/UPSERT retain VALUES or final SELECT source
order; UPDATE/DELETE retain ordinal document-ID order. When the field itself is named returning, expression continuations such as
`RETURNING returning + 1 AS x` take precedence over an implicit target alias. Use explicit
`AS returning` to disambiguate a target alias before a unary projection. There is no independent RETURNING sort,
paging or DISTINCT. A zero-match write returns an empty result with projection metadata and
an affected count of zero. Without RETURNING, write commands retain their count-only results.

Every projection executes before the statement transaction completes. A failure or cancellation
rolls back the statement; in an explicit transaction the existing whole-transaction abort rule
applies. No partial RETURNING result reaches the caller. Returned JSON values own their data.
`DocumentDatabaseEngineOptions.StatementMemoryBudget` defaults to 64 MiB and must be positive.
RETURNING charges retained row/array storage, scalar values, JSON clones (128 bytes plus UTF-8
length plus 12 bytes per token), and preserved whole-image bytes once per representation to
this per-statement budget; exhaustion gives CD05018 at the clause span
and rolls back the writes. Serialized rows are preflighted in the same transaction: the existing
CD05016 covers the 16 MiB frame cap, and CD05019 covers values deeper than 256 levels including
the named projection wrapper. This is a retained-result estimate, not a managed-heap measurement;
SELECT source and group staging share this counter; UPDATE, DELETE, INSERT and UPSERT pending buffers share it too (#1755). Pending-buffer exhaustion refuses with CD05018 before any write, including INSERT/UPSERT SELECT; all reservations release on every exit.
An over-deep array/object constructor gives CD05015 naming RETURNING at its expression span;
other serialization failures retain their own diagnostic identity.

The existing Complete payload carries both result and affected counts, without negotiation,
a capability flag, a frame-layout change or a disk-format change. With RETURNING the counts
are equal and nonnegative; read-only results keep affected count -1. Updated clients accept
this combination. Older clients that require -1 for every result header reject it, as accepted
by owner decision 8 (2026-10-25).
Their rejection is a client-side ProtocolViolation that closes the connection after an
autocommit statement has already committed. Retrying a non-idempotent write can apply it
again: `SET n = n + 10` can change 1 to 11 despite that client error, then 11 to 21 on retry.
In an explicit transaction the disconnect rolls back the uncommitted work. Use an updated
client for RETURNING; an old client's rejection does not prove an autocommit rollback.

The image/projection basis follows Couchbase's [INSERT RETURNING](https://docs.couchbase.com/server/current/n1ql/n1ql-language-reference/insert.html#returning-clause),
[UPDATE](https://docs.couchbase.com/server/current/n1ql/n1ql-language-reference/update.html#returning-clause)
and [DELETE](https://docs.couchbase.com/server/current/n1ql/n1ql-language-reference/delete.html#returning-clause).
Cohesion additionally requires transactional rollback on projection failure. RavenDB's reference
`src/Raven.Server/Documents/Patch/PatchDocumentCommand.cs:354-356` conditionally returns
ModifiedDocument; the same clone was inspected for this post-image precedent.

## Aggregate quantifiers and native aggregates (#1729, OQ-G1)

COUNT, SUM, AVG, MIN, MAX, ARRAY_AGG, BOOL_AND, BOOL_OR, aggregate EVERY, STDDEV and VARIANCE
accept one expression with an optional ALL or DISTINCT inside the parentheses. Omitted ALL
includes every admitted input. DISTINCT uses grouping equality: numeric 1 and 1.0 are equal,
strings compare ordinally, arrays compare by elements and objects by ordinal member names and
values, including nested NULL. COUNT(*) remains the unquantified row count; ALL/DISTINCT with
star and quantifiers on scalars fail CD00011. Invalid arities retain CD00006. A directly built
call with an invalid quantifier, a scalar quantifier or quantified star fails CD01029 in binding.
Single-argument registered aggregates also accept the quantifiers and retain their declared
NULL/MISSING policy. Aggregate calls are valid in grouped or ungrouped SELECT, HAVING and
ORDER BY; the existing prohibition in WHERE, GROUP BY, RETURNING and nested aggregates remains.

| Aggregate | Admitted inputs | Empty or no admitted inputs | Result |
| --- | --- | --- | --- |
| COUNT(expr) | Every non-NULL, non-MISSING value | 0 | Int64 |
| SUM, AVG | Numeric values; NULL/MISSING skipped; valued nonnumbers fail CD02008 | NULL | Decimal |
| MIN, MAX | Every non-NULL, non-MISSING value, ordered as grouping | NULL | Selected value type |
| ARRAY_AGG | Every non-MISSING value, including NULL and complex values | NULL | Owned JSON array |
| BOOL_AND, EVERY(expr) | Booleans only; NULL, MISSING and other kinds skipped | NULL | Boolean conjunction |
| BOOL_OR | Booleans only; NULL, MISSING and other kinds skipped | NULL | Boolean disjunction |
| STDDEV, VARIANCE | Numbers only; NULL, MISSING and other kinds skipped | NULL | Decimal sample statistic |

ARRAY_AGG promises no input ordering. Its DISTINCT variant includes at most one explicit NULL;
all-MISSING input gives NULL. Boolean aggregates are Cohesion extensions and do not coerce SQL++
Truth values: BOOL_AND(1) is NULL. EVERY(expr) is an aggregate call; the existing quantified
predicate EVERY v IN arr SATISFIES cond END retains its own AST and empty-array TRUE rule. Quantified collections and predicates may contain aggregate calls wherever the enclosing clause allows aggregates; those calls use the enclosing group. An aggregate inside another aggregate remains CD05001.
STDDEV and VARIANCE divide by n - 1 for n > 1 and return zero for one numeric input. They use
exact integer moments at decimal scale 28; retained state is bounded by the decimal domain and
Int64 input count. VARIANCE rounds the final rational value to decimal using nearest-even rounding.
STDDEV converts the BCL square root through its shortest round-trip representation, like SQRT,
using the unrounded rational whenever variance is not exactly representable as decimal. A final
out-of-range result retains CD02007, decided from the exact rational. A floating conversion beyond the decimal boundary falls back to nearest-even decimal rounding, including decimal.MaxValue; an intermediate moment cannot overflow or underflow.

The [Couchbase aggregate reference](https://docs.couchbase.com/server/current/n1ql/n1ql-language-reference/aggregatefun.html)
defines ALL/DISTINCT, ARRAY_AGG's unknown handling and the named sample statistics. The owner
selected these defaults on 2026-10-25. Existing SUM/AVG nonnumber refusal is preserved.

Retained SELECT source rows, catalog identities/sort staging, group keys/membership, native
accumulators, DISTINCT sets, ARRAY_AGG inputs and results, and projection/order storage charge
StatementMemoryBudget (#1753). Exhaustion gives CD05018 before any result is published.
Aggregate-call failures keep the call span; RETURNING keeps its clause span. Sources and groups
release reservations on every exit. DISTINCT sets and array input lists release after evaluation;
HAVING scratch releases per predicate, projected results are charged separately, and ORDER BY
storage releases after sorting. JSON charges UTF-8/token metadata and ownership; input and result
representations are charged separately while both live. INSERT/UPSERT SELECT sources and RETURNING
share the operation counter. DML pending buffers share this estimate (#1755); it excludes application
callback-owned state. Exhaustion aborts explicit work and preserves reusable sessions after rollback. EXPLAIN lists aggregate names and
quantifiers in Group detail without evaluating arguments or reading documents. Arrays, booleans,
NULL and decimals use existing wire values and headers; no frame or format version changes.
GROUP AS and group-term aliases use the same grouping stage, as described below.

## GROUP AS and group-term aliases (#1729, OQ-G2)

`GROUP BY expr [AS alias], ... GROUP AS g` retains an owned JSON array of binding-wrapper
objects. AS may be omitted for a group-term alias. GROUP AS requires at least one GROUP BY
term; a bare `GROUP AS g` is CD00012. GROUP BY over an empty input produces no groups and no
synthetic wrapper; a projected g still has Json column metadata. Ungrouped aggregates retain
their existing one-empty-group behavior and cannot declare GROUP AS.

Each wrapper has one member for the FROM binding (the explicit alias, otherwise the collection
name), one for each UNNEST binding, and one for each explicitly named group term. For example,
`FROM items d LEFT UNNEST d.tags t GROUP BY d.kind AS key GROUP AS g` produces
`[{"d": {original document}, "t": element, "key": group value}, ...]`.
The FROM member preserves object, array, scalar and NULL roots. MISSING bindings and MISSING
group aliases omit their members; explicit NULL members remain. LEFT UNNEST's absent element
therefore omits t. Unnamed group terms add no wrapper member. No group-array element ordering
is promised. This follows Couchbase's [GROUP BY / GROUP AS wrapper and alias contract](https://github.com/couchbaselabs/docs-devex/blob/c3bb1549af4887019b9983e371d9e8b4f569052c/modules/n1ql/pages/n1ql-language-reference/groupby.adoc#L47-L81),
with the owner's requirement to include group-term aliases. LETTING stays unavailable (CK00001),
while correlated subqueries and FROM-array sources execute. LETTING remains contextual as a field or FROM/UNNEST name; use AS or quotes for a group-term alias named letting.

Group terms evaluate against the input row, independently of other group-term aliases.
Named terms then bind each member's computed value for aggregate row inputs and bind the
representative key in SELECT, HAVING and ORDER BY. Group AS g is visible after grouping in
those clauses, including nested constructors, scalar calls, ARRAY/FIRST comprehensions and
ANY/SOME/EVERY predicates. A path such as `g[0].d.name` addresses a wrapper; a comprehension
such as `ARRAY r.d.name FOR r IN g END` maps the members. Local variables shadow group bindings
only within their lexical scope. Existing standalone projection aliases still take precedence
in ORDER BY. WHERE and GROUP BY retain input-field lookup and do not see these new bindings.
Original input fields outside aggregate calls still require grouping; GROUP AS does not relax
that rule. Names are ordinal and case-sensitive; quoted whitespace names retain their exact identifier value. Empty/duplicate declarations and collisions
with FROM or UNNEST names are CD01030; malformed direct-AST alias lists use that code too.
GROUP AS cannot appear in an aggregate's row input (CD01031), where the array does not yet
exist; a local variable introduced inside that row-input expression may reuse the name.

Wrappers, the final group array, source rows, group keys, aggregates, DML pending buffers and
projected results share StatementMemoryBudget. Alias bindings charge a shared key representation
once while retaining per-row binding overhead; independently owned values and serialized JSON
copies are charged separately. Temporary wrappers release after constructing the array; arrays
remain charged through HAVING, projection and ordering. Rejected groups
release their array charge immediately. Every exit releases staging reservations, including
cancellation and CD05018. Output JSON owns its data independently. EXPLAIN records group_as
and group_aliases on Group (or system-source detail) without reading or evaluating inputs.
Queries without grouping metadata retain their base allocation shape. Existing Json values
and headers carry the arrays over InMemory and TCP; no format/version change is required.

## ARRAY and FIRST comprehensions (#1728)

`ARRAY expr FOR v IN arr [WHEN cond] END` and
`FIRST expr FOR v IN arr [WHEN cond] END` accept one IN binding. They follow
[Couchbase range transformations](https://docs.couchbase.com/server/current/n1ql/n1ql-language-reference/collectionops.html#range-transformations).
The collection evaluates outside the new variable's scope; mapping and WHEN evaluate inside it.
Nested variables shadow outer variables and fields and restore the enclosing scope at END.
Parenthesized mappings are accepted, including mappings followed by CASE, nested ARRAY/FIRST or
ANY/SOME/EVERY expressions without extra parentheses. Mapping and WHEN may nest these forms.
The expressions compose with all current scalar contexts,
including registered arguments, grouped projections, aggregate arguments, UNNEST and RETURNING.
Closed mappings using only local paths also execute in INSERT/UPSERT VALUES. Calls in VALUES
retain their existing refusal. Aggregates inside a comprehension remain forbidden (CD05001); collection, mapping and WHEN are all validated even on empty inputs.

Closed comprehensions and quantified predicates preserve ordinary short-circuit evaluation in
WHERE, VALUES and constructors. A failed speculative index-bound evaluation is discarded; its
OQL error occurs only if execution visits that expression. Cancellation still propagates.

ARRAY keeps input order and omits mapped MISSING values; NULL mappings remain array elements.
An empty input or no retained mappings returns []. WHEN keeps only SQL++ Truth true, so NULL
and MISSING conditions exclude the element. FIRST stops at the first qualifying element and
returns its mapping, including MISSING or NULL; it returns MISSING when none qualifies.
Both operators return MISSING for MISSING input and NULL for NULL or non-array input, as ANY does.
Named projections omit MISSING; RAW/VALUE retains the existing conversion of top-level MISSING
to JSON null. These defaults implement the owner decisions of 2026-10-25; FIRST's selected
MISSING does not advance to a later element.

WITHIN, position variables, multiple bindings, OBJECT transformation and the retired ARRAY()/FIRST()
function spellings remain unsupported. Syntax and depth use CD00002/CD00005; CD01027 identifies
an empty binding in a direct AST. Grouped expression matching and metadata validation preserve
lexical binding identity. Execution checks cancellation per element. ARRAY constructs owned JSON
using the existing depth limits; retained RETURNING arrays consume the existing statement budget
(CD05018), and constructor depth retains CD05015. No wire or persisted format changes.

## Quoted identifiers

Double-quoted collection, property and alias names accept doubled `""` as one embedded
double quote: `"a""b"` names `a"b`. This extends the existing double-quoted identifier syntax;
it does not add backtick quoting. A missing closing delimiter is CD00003 (#1235).

`a<-1` and `a <- 1` mean `a < -1`; `<-` is not an OQL arrow (#1647).
`a<--1` is `<` followed by comment `--1`, leaving a missing right operand (CD00002).
`OqlLeftArrowConformanceTests` pins this shared-lexer boundary.

## Storage integrity errors (#1160)

Opening files still reports CD06104 immediately for a sharing/lock violation. Process-crash
conformance tests alone retry the actual open until success with a 30-second hang guard (#1713);
this changes no OQL syntax, execution semantics or production retry policy.

A read that detects storage corruption fails with `CK06003`, naming the database and
page and recommending a verified backup. It never succeeds with the damaged data omitted.
The error is an execution failure over the wire; the connection remains usable. A
legitimately reclaimed location retains its existing absence semantics. The engine's
storage-integrity conformance tests cover corrupted media through the executing language.

OQL retains its name and follows the executable SQL++ subset recorded in
[DESIGN.md](DESIGN.md#supported-clause-matrix). It uses the owner-selected Couchbase
MISSING truth table, distinct from NULL. The complete Boolean/predicate tables, null-path
navigation rule, ordering, aggregate behavior, reservations and freed ODMG identifiers
are the [SQL++ dialect contract](DESIGN.md#sql-reservations-and-retired-odmg-words-1126).

`MISSING`, `IS [NOT] MISSING` and `IS [NOT] VALUED` execute. `IS [NOT] NULL` propagates
MISSING; WHERE and HAVING keep only TRUE. Absent fields, incompatible shapes, navigation
through null and out-of-range array indexes produce MISSING. Projected absent members are
omitted from result objects, while explicit null members remain null. Grouping keeps both
states separate; ascending order puts MISSING before NULL and descending reverses that order.
NIL is a NULL synonym; UNDEFINED is reserved and rejected.

Unless LIMIT is zero, LIMIT n, OFFSET m, LIMIT n OFFSET m and OFFSET m LIMIT n execute after
HAVING, DISTINCT and ORDER BY, including system sources and INSERT/UPSERT SELECT. Each operand is an optionally
signed integer literal or a parameter with an integral, non-negative signed Int64 value.
NULL/NIL, MISSING, Boolean, string, fractional, nonfinite and out-of-range counts give CD00007
at the operand; literal 2.0/1e2, LIMIT ALL and arbitrary expressions are also invalid.
Integral decimal/JSON numeric parameters normalize before checking. Missing names retain CD01005.
Missing operands and duplicate clauses give CD00002; LIMIT m, n gives one CK00001 before
validating m, including ALL, negative and string operands. FETCH FIRST gives one CK00001
only when FETCH starts a construct; c.fetch FIRST and c.limit ALL still reject FIRST and ALL.
LIMIT/OFFSET are reserved: quote field names or use d.limit/d.offset; SELECT a AS limit is valid.
UPDATE/DELETE and INSERT/UPSERT VALUES paging stays unsupported, naming the statement and clause
(for example, DELETE LIMIT or INSERT VALUES OFFSET). Column types are computed before ordinary paging;
LIMIT 0 uses planned column names and types, including Int64 COUNT, without reading the source.
Scan and seek pages share ordinal document-ID order;
groups use group-key order, system sources catalog order, and ORDER BY ties retain baseline order.
Separate pages need unchanged data or a shared snapshot for repeatability. Completion counts
only returned frames; query AffectedCount remains -1. EXPLAIN includes resolved paging counts.

The [function table](DESIGN.md#supported-clause-matrix) declares META, eleven aggregates and 34
SQL++ scalar functions (#1137). Scalars execute in projections, WHERE, GROUP BY, HAVING, ORDER BY,
UPDATE expressions and INSERT/UPSERT SELECT; VALUES remains constant-only. Unknown calls report
CD00008 and invalid arities report CD00006. The deferred REGEXP functions retain
CK00001. Function tokens stay usable as fields and iteration aliases.

LENGTH counts Unicode scalars. SUBSTR and POSITION use zero-based code-point positions;
POSITION returns -1 for no match. SUBSTR permits a negative start from the end, clamps starts
to the string, and returns NULL for a negative length or fractional index. String predicates
compare ordinally; LOWER/UPPER use invariant case conversion. TRIM defaults to SQL++ whitespace:
space, tab, newline, form feed and carriage return, diverging from SQL's space-only default.
Its optional second argument is a set of code points. Empty REPLACE patterns leave text unchanged.
REPLACE accepts an optional fourth integer argument n and replaces the first n occurrences;
omitting n or using any negative integer replaces all, as
[Couchbase documents](https://docs.couchbase.com/server/current/n1ql/n1ql-language-reference/stringfun.html#replace).
Zero replaces none; a fractional count gives CD02008.
Constructed string results and work buffers are capped at 1,048,576 UTF-16 units; inputs are
not capped. LENGTH, searching, string predicates and SUBSTR scan without input-sized buffers,
and loops observe cancellation at least every 1,024 iterations.
ROUND uses half-away-from-zero for exact numbers, with negative scales; TRUNC rounds toward zero.
SQRT of a negative number and invalid POWER domains give CD02019; overflow gives CD02007.
Fractional ROUND/TRUNC scales give CD02019. SQRT and nonintegral POWER use BCL double
calculations converted through the double's shortest round-trip representation to the decimal
scalar domain, retaining full double precision; integral POWER uses decimal arithmetic.

Dates use UTC ISO strings `yyyy-MM-ddTHH:mm:ss.fffZ` and Int64 epoch milliseconds. STR_TO_MILLIS
accepts `yyyy-MM-dd` and date plus `T` or space, `HH:mm:ss`, optional 1..7 fractional digits and
optional `Z` or numeric offset. Zone-less inputs mean UTC; invalid inputs and out-of-range epoch
values give NULL. T1 has no format-string argument. NOW_STR/NOW_MILLIS share the transaction-start
instant across every row and statement; autocommit captures one instant for the statement.
COUNT, LENGTH, POSITION and ARRAY_LENGTH return long values and Int64 column metadata, including
empty grouped results. Epoch-millisecond functions also return Int64. Other numeric scalars use Decimal.
Mixed Int64/Decimal projection columns promote to Decimal; numeric cells in heterogeneous columns
also use Decimal so embedded and wire values agree. Uniform Int64 columns retain long values.

`[NOT] IN (a, b)`, `[NOT] BETWEEN a AND b`, `[NOT] LIKE pattern [ESCAPE c]` and searched/simple
CASE execute in projections, filters, grouping and aggregate arguments. IN compares list
items structurally; parentheses always mean a value list, even for one array-valued item.
The array form `x IN d.tags` also executes; a NULL or non-array right operand gives NULL,
and a MISSING operand on either side gives MISSING. `NULL IN []` deliberately retains NULL.
LIKE is ordinal and case-sensitive, `%`
matches any number of Unicode scalars and `_` matches one. Backslash is the default escape;
`ESCAPE ''` disables it. Invalid evaluated escapes and trailing pattern escapes give CD02013.

For IN, a match wins over NULL/MISSING items; without a match NULL wins over MISSING.
A MISSING left operand gives MISSING, and a NULL left operand gives NULL. BETWEEN propagates
MISSING before NULL from any operand or bound, then applies inclusive same-kind scalar
comparisons. LIKE propagates MISSING before treating non-string/NULL inputs as NULL.
Negation preserves both unknown values. CASE skips unknown conditions; an unmatched CASE,
including simple CASE on MISSING, gives ELSE or NULL when ELSE is absent. The engine
[truth tables and Couchbase sources](../../Assimalign.Cohesion.Database.Documents/docs/DESIGN.md#predicate-truth-tables-1127)
record these rules. CASE, WHEN, THEN, ELSE, END and ESCAPE are reserved; `"end"` and `d.end`
remain valid field references.

`FROM items d [INNER | LEFT [OUTER]] UNNEST d.tags [AS] t` executes and can chain bindings.
The alias may be omitted for a path, defaulting to its last named step. INNER drops empty,
MISSING, NULL and non-array operands; LEFT preserves them with t MISSING. Null array elements
stay NULL. Rows follow document ID then array order. WHERE runs after expansion; COUNT(*)
counts expanded rows. With UNNEST, `SELECT *` returns synthetic `{"d":...,"t":...}` objects;
without a FROM alias the source member is named items, while paths keep existing resolution.
A plain star without UNNEST preserves stored bytes. ORDER BY projection aliases outrank
UNNEST variables. Variables shadow fields; quantified bindings may shadow enclosing variables.
Duplicate names in one binding list give CD00010. The unaliased collection name is reserved
for this check: `FROM items LEFT UNNEST tags items` is a duplicate, while paths still do not
resolve an implicit items variable. Inferred duplicate aliases span the final path-name token.
Direct AST duplicates give CD01023. Group-key equivalence preserves lexical binding scope;
a quantified t cannot match an outer t in a grouping expression.

`ANY | SOME | EVERY x IN array SATISFIES predicate END` executes with one scoped variable.
SOME is ANY. Each predicate result undergoes SQL++ Truth conversion; NULL/MISSING results
are not true. ANY is FALSE on empty arrays, EVERY is TRUE. A MISSING collection gives MISSING;
NULL or non-array collections give NULL. `NOT ANY x IN [NULL] SATISFIES x = 1 END` is TRUE,
following [Couchbase ANY](https://github.com/couchbase/query/blob/master/expression/coll_any.go)
and [EVERY](https://github.com/couchbase/query/blob/master/expression/coll_every.go).
UNNEST, INNER, LEFT, OUTER, EVERY and SATISFIES stay contextual names. END remains reserved.
UNNEST/JOIN/NEST in UPDATE/DELETE, FLATTEN calls, multi-binding quantifiers, general JOIN/NEST
predicates, WITHIN and ANY AND EVERY remain deferred with CK00001.
The DML refusal applies at an UNNEST clause, so `DELETE FROM items WHERE ANY x IN tags
SATISFIES unnest END` and UPDATE predicates/values may read a field named unnest. A MERGE INTO
inside a CASE result in SATISFIES receives one CK00001 naming MERGE.

UPSERT INTO collection [AS alias] (KEY, VALUE) VALUES (key, value) [, ...] and
UPSERT INTO collection (KEY outputName [, VALUE outputName]) SELECT ... execute with
INSERT's validation and MISSING rules. Existing identities receive complete replacements;
absent properties disappear and each written identity receives a fresh version. Without VALUE,
the complete projected row becomes the document. Every source row is staged before writes.
A repeated key fails the whole statement with DocumentDuplicateKeyException (CD03012).
OPTIONS remains CK00001 at its token. RETURNING follows the contract above. INSERT and UPSERT both reject
(KEY k, OPTIONS o) SELECT ... with one diagnostic naming OPTIONS, including its expression form. No statement-level version precondition is available;
PutAsync(expectedVersion) and UPDATE WHERE META().version = $v provide optimistic replacement.
EXPLAIN UPSERT returns Upsert or UpsertSelect; without RETURNING, completion reports the affected count without rows.
DocumentUpsertTests, the advertised-clause corpus, wire tests and the recovery fixture cover it.

DELETE FROM collection [AS alias | alias] [WHERE expression] executes through the existing
scan and equality/range index seeks. The complete predicate selects matching ids from the
statement snapshot before any deletion; META().id remains a residual filter in T1. Omitting
WHERE deletes all content while retaining the collection and index definitions. WHERE 1
matches every document; NULL/MISSING match none. A successful command reports its affected
count through Complete, with Document frames only when RETURNING is present. Conflicts or cancellation roll back the
statement; failure inside an explicit transaction aborts the whole transaction. System
collections remain read-only and Schema-owned collection contents remain mutable.
EXPLAIN DELETE reports Delete plus CollectionScan or IndexSeek without writing. USE KEYS,
DELETE LIMIT remains CK00001 at its token. RETURNING follows the contract above. DocumentDeleteTests and the
advertised-clause corpus cover embedded execution; wire and NativeAOT checks cover completion.

UPDATE collection [AS alias | alias] SET path = expression [, ...] [UNSET path [, ...]]
[WHERE expression], or UPDATE with UNSET alone, executes through the same snapshot scan/seek
pipeline. All SET expressions read the original document; all patched documents are validated
before any write. Name segments require objects and SET can create missing intermediate objects.
Every index segment requires an existing array and an in-range index, including UNSET and
SET-to-MISSING beneath missing members; arrays are never created or expanded implicitly.
UNSET of an absent object member is a no-op. SET-to-MISSING removes object members, while
MISSING assigned to an existing array element becomes JSON null. UNSET cannot end in an index,
and root replacement is invalid. Incompatible paths give CD02016; duplicate and prefix-overlapping
SET/UNSET targets after alias normalization give CD01017. Invalid stored JSON or a patched
document deeper than 128 levels gives CD02018. Temporary constructors retain query depth.
Untouched values and property-name spellings retain their bytes; rewritten-container whitespace
may be normalized. A successful UPDATE returns the matched count, including unchanged matches,
and allocates fresh versions. Failure or cancellation writes nothing under the transaction rules
above. EXPLAIN UPDATE describes the access without writing. USE KEYS, LIMIT and FOR remain CK00001. DocumentUpdateTests covers paths, atomicity, serialization and matched counts.

## ORDER BY null placement (#1730)

Each term accepts `expr [ASC | DESC] [NULLS FIRST | LAST]`, including projection aliases,
multiple terms and LIMIT/OFFSET. ASC is the omitted direction. The omitted placement is FIRST
for ASC and LAST for DESC. FIRST/LAST moves the whole MISSING/NULL block; direction controls
its internal order: ASC keeps MISSING before NULL, DESC keeps NULL before MISSING. Thus the
older “MISSING before NULL” wording means the ascending base order, not every direction.
This follows the pinned [Couchbase table](https://github.com/couchbaselabs/docs-devex/blob/c3bb1549af4887019b9983e371d9e8b4f569052c/modules/n1ql/pages/n1ql-language-reference/orderby.adoc#L64-L99).
Values retain Boolean, number, ordinal string, array and object order (reversed for DESC).
Equal query keys retain ordinal document identity, independent of direction; later ORDER BY
terms precede that tie break. NULLS stays contextual, so existing fields named nulls remain valid.
Malformed NULLS options give located CD00013; an invalid direct-AST placement gives CD01032.
NULLS ALL, NULLS DISTINCT and numeric options report CD00013 at the option; bare NULLS reports
it at end of input. NULLS FIRST LAST reports CK00001 at the extra LAST, outside the completed suffix.
No new sorting bound or budget diagnostic is introduced; retained sorting still uses CD05018.

| Term | First to last |
|---|---|
| ASC / ASC NULLS FIRST | MISSING, NULL, values ascending |
| ASC NULLS LAST | values ascending, MISSING, NULL |
| DESC / DESC NULLS LAST | values descending, NULL, MISSING |
| DESC NULLS FIRST | NULL, MISSING, values descending |

## Index DDL (#1135)

CREATE [UNIQUE] INDEX [IF NOT EXISTS] name ON collection (path [, path ...]) and
DROP INDEX [IF EXISTS] name ON collection execute. Indexes have 1..32 distinct ordered paths;
root bracket-string names such as ['x.y'], nested members and integer array subscripts work.
UNIQUE is a Cohesion extension in SQL spelling and remains reserved elsewhere (SELECT UNIQUE(a)
is CK00001). Conditional creation skips an existing name regardless of its definition.
An absent conditional DROP is a no-op; existing schema ownership and snapshot conflicts still refuse.
Unconditional duplicate creation fails with CD03024; an absent unconditional DROP fails with
CD01025. An encoded composite key beyond 1024 bytes fails with CD02026. An encoded index
definition beyond the 8,092-byte catalog record limit fails with CD03026 before any posting
extraction or physical tree creation, including definitions with large path lists. These errors
carry structured diagnostics. OQL index DDL is autocommit-only; an explicit transaction refuses
it with CD04105 before catalog access. After a failed autocommit statement the next can run immediately.
PATHS in COHESION_SCHEMA.INDEXES is the ordered array, PATH is its first entry, and IS_UNIQUE is real.

Leading MISSING omits the entry unless the leading scalar key has INCLUDE MISSING. Components sort MISSING, NULL, FALSE, TRUE, numbers, ordinal
strings, arrays, objects; full predicates always filter seek candidates. UNIQUE keys containing
any NULL or MISSING do not conflict (PostgreSQL NULLS DISTINCT, diverging from Cohesion SQL).
Duplicate valued scalar keys fail CD03020 (23505), naming index and collection without key data;
array/object UNIQUE components fail CD02021 naming index and path. Constraints are immediate
per row, so key swaps may fail. A failed explicit transaction requires RollbackAsync before reuse.
The wire uses ExecutionFailure (6) without a protocol change and keeps the connection usable.
The path-count cap reports CD00009 at the 33rd key (CD03022 for a directly constructed AST). Duplicate/empty keys, expressions, wildcards, ASC/DESC, partial
WHERE receive CD00002. ALL array keys get one CK00001 at ALL.
The catalog uses v3 index records (#1530) and refuses v1/v2 indexes before recovery writes;
there is no upgrade path or legacy reader. Composite seeks support a leading equality prefix
and one range on the next path. Bounded snapshot-visible probes rank candidate cost first;
ties use prefix length, range, UNIQUE, then ordinal name. Comparable complete inputs may
intersect document identities before JSON reads. The full predicate always filters candidates.
EXPLAIN exposes the chosen IndexSeek or IndexIntersection and index-entry estimates, including
incomplete lower bounds and the intersection's minimum-input upper bound. It does not execute
the data statement; actual_rows remains NULL. No persisted statistics or ANALYZE command is added.

One key may instead be `DISTINCT ARRAY v.p FOR v IN arr [WHEN cond] END` (#1530).
Scalar keys may precede or follow it. The source must be a document path and the mapping a path
rooted in the variable (or that variable itself). WHEN supports element-path comparisons to
NULL, Boolean, Decimal or String literals, IS NULL/MISSING, AND, OR and NOT. Parameters,
computed mappings/sources, functions and other WHEN expressions are excluded. Text numeric
literals bind directly to Decimal, including `1.0`; CLR Single/Double constants in a direct AST
are refused. WHEN has at most 32 levels and 128 predicate nodes (a negated presence test counts
as NOT plus the test). Binding failures use CD01028, unsupported constants CD02027, restricted
expression failures CD03027 and predicate size failures CD03028, all located before catalog writes.
Multiple array keys and UNIQUE array indexes use CD03025. INCLUDE MISSING is a leading scalar-key suffix.
PATH/PATHS render normalized array keys with a canonical `v` binding and a quoted source root.
Source, projection and WHEN path segments containing punctuation, keywords, Unicode or quotes
are quoted individually, preserving typed catalog definitions as executable DDL.

Top-level conjunctive ANY/SOME predicates match source/projection paths independently of variable
names. Separate ANY scopes keep separate witnesses. Direct document-path UNNEST (including LEFT
when a mandatory element comparison rejects its synthetic MISSING row) can also use an array
index. Dependent sources, EVERY, NOT ANY, OR-only element predicates and presence tests alone
remain scans unless another usable scalar index applies. Every index WHEN condition must be
implied by matching normalized element conjuncts; reversed comparisons and negated presence
tests normalize, but broader inequality inference is not performed. A scalar prefix alone never
makes an array index complete for empty/filtered arrays.

Candidates deduplicate by document identity before fetching. The complete predicate always
executes as a residual, rejecting cross-element false positives. UNNEST then expands the stored
array with its original element multiplicity: `[1,1,2]` still produces three rows. EXPLAIN prints
DISTINCT ARRAY keys, `distinct_documents=True; array_residual=True`, and posting-count estimates.
Ungrouped, non-DISTINCT queries with fixed projection types can stop after OFFSET + LIMIT residual
survivors when unordered or ordered solely by META().id (including a projection alias), ASC or DESC.
Proven scalar index order also permits early paging (#1632); projections requiring type inference retain materialization. This limits content
fetches, not candidate metadata or index probes. The benchmark category = X AND any variant price
<= Y shape with a `(category, DISTINCT ARRAY v.price FOR v IN variants END)` index has no residual
rejections and fetches at most OFFSET + LIMIT documents in identity order. Among 256 candidates,
LIMIT 50 reads 50 documents; OFFSET 20 LIMIT 50 reads 70, in either direction. Interleaved rejections
read 99/139 ascending and 100/140 descending: at most 2 * (OFFSET + LIMIT) for this half-qualifying
shape. Arbitrary residuals have no such ratio. A matching stock WHEN fetches one document for the
selective price/stock probe; its unfiltered counterpart rejects 1,000 cross-element false positives
after 1,001 fetches. WHEN alone guarantees no ratio for additional residuals or other orderings.
These are local probes, not an SF10 run.
The syntax/matching basis is [Couchbase array indexing](https://docs.couchbase.com/server/current/n1ql/n1ql-language-reference/indexing-arrays.html);
the DISTINCT-only, scalar projection and no-UNIQUE restrictions are Cohesion's approved boundary.

### INCLUDE MISSING and ordered index execution (#1632)

`CREATE INDEX ix ON items (score INCLUDE MISSING, category)` includes leading-MISSING
records using the existing v3 flag; INCLUDE is contextual. The suffix belongs inside the key
list on the first scalar key, following [Couchbase](https://github.com/couchbaselabs/docs-devex/blob/c3bb1549af4887019b9983e371d9e8b4f569052c/modules/n1ql/pages/n1ql-language-reference/createindex.adoc#L317-L330).
It is invalid on later keys or after the closing parenthesis. Direct AST leading-array definitions
still receive CD03025. COHESION_SCHEMA.INDEXES exposes INCLUDE_MISSING and renders the suffix
in PATH and PATHS[0], making the key list reusable as DDL. No format or wire bump is needed.

EXPLAIN reports order_eligible and order_reason on the selected access and one
IndexOrderCandidate per visible index, including ORDER BY-only candidates. Eligible candidates
report forward/reverse direction, equality prefix, complete equal-key buckets and the document
identity tie break. Ordinary sparse indexes need a mandatory predicate proving that leading
MISSING cannot qualify. AND needs either proof, OR needs both; direct presence tests and direct
comparisons are recognized. Calls and computed paths establish no proof. ORDER BY must match
consecutive leading keys after optional equality-fixed keys, with one direction for varying keys.
Nondefault null placement requires proof that both unknowns are excluded. Arrays, grouping,
DISTINCT, UNNEST, nonindex expressions and intersections retain their stated fallback reasons.

Lossy array/object values never establish ordering. UNIQUE enforces scalar keys; direct scalar
comparisons or NULL/MISSING tests can also prove a domain. Otherwise a snapshot-visible,
one-entry/32-page existence probe checks complex tags on the first varying component, without
fetching documents. A visible match stops immediately; an invisible first entry leaves the domain
unproven. Incomplete probes and later varying components without a scalar proof report
scalar_domain_unproven. This deliberately conservative fallback needs no new diagnostic.
Ordering reuses the ordinary access analysis; every structural and predicate proof runs before
probing. Rejected ordinary plans allocate no EXPLAIN candidate graph.
A shorter ORDER BY than the physical key still requires a complete equal-ORDER-BY-prefix bucket,
including all trailing physical components, before identity sorting and LIMIT termination.
Execution consumes a proven scalar index in forward or reverse order and elides the result
sort. The existing predicate chooser's eligible index wins; otherwise the first eligible index
in ordinal name order wins. Intersections retain their identity merge and fallback sort.
The catalog cursor uses the statement snapshot and releases every page/latch before returning
metadata. It materializes one complete equal-ORDER-BY-prefix bucket, including trailing physical
components, then emits that bucket in ascending document identity order in either scan direction.
The full WHERE predicate filters this stream without changing its order. Bucket metadata, keys
and identities charge the existing statement memory budget (CD05018); cancellation and failure
release the cursor and reservations.

With fixed projection types, LIMIT/OFFSET stop after OFFSET + LIMIT residual survivors. LIMIT 0
opens no cursor. One next-bucket entry establishes completeness, and the B-tree may already have
read ahead in its bounded batch. SELECT * has a fixed JSON type and stops early on a consumed
ordered path. Projections needing result type inference still read all matching documents so
column headers retain the existing full-result contract;
their proven index order still elides sorting. EXPLAIN reports consumed=True; sort=elided on the
selected access and candidate, early_paging, and paging_reason=result_type_inference when needed.
VALUE/RAW ordering by its projected expression retains sorting unless MISSING is rejected,
because projection promotes MISSING to NULL and changes equal-key ties (value_missing_becomes_null).
Other candidates retain consumed=False and their eligibility/selection reason; the root describes
sort retention and its reason. EXPLAIN remains nonexecuting and actual_rows remains NULL.

Conformance compares 504 generated index/predicate/order combinations across eight paging
windows (4,032 exact comparisons), with complete duplicate buckets, residual rejections,
scalar extremes, MISSING/NULL, escaped strings and trailing coarse tags. Controlled concurrent
insert/update/delete/index replacement tests cover autocommit, Snapshot and ReadCommitted,
plus own writes, rollback and cancellation. InMemory/TCP and the NativeAOT consumer exercise
consumption and paging without new API, wire or disk formats. Star paging regressions cover
both directions and the 256 KiB statement budget; rejected-shape allocation guards cover complex
values, mixed directions, unproven MISSING, later scalar domains and array indexes.

Conformance lives in OqlSqlppBasisTests, OqlKeywordDispositionTests, DocumentMissingTests,
OqlExecutionConformanceTests, DocumentQueryTests and DocumentMissingWireTests. The wire tests
exercise both InMemory and TCP; the recovery fixture also executes IS MISSING under NativeAOT.

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

## Documents diagnostic conventions (#1470)

Exception and diagnostic templates use the project's build-generated `ErrorMessages` resources.
Parameterized templates format with the current culture; abort/cancellation templates used in
normal cleanup are cached. The [Database error-code registry](../../Assimalign.Cohesion.Database/docs/ERROR-CODES.md)
assigns this family its two-letter prefix and categorized five-digit identities.
Existing SQLSTATE meanings, numeric wire error kinds, frame layouts and disk formats are preserved.
Structured diagnostics keep their code outside the message. Compiler diagnostic identifiers and
standard .NET argument guards retain their own contracts.

## Model failure producer completion

OQL syntax and evaluation are unchanged. Engine state and result-access refusals carry the
[model producer diagnostics](../../Assimalign.Cohesion.Database.Documents/docs/DESIGN.md#model-failure-producer-completion);
the existing wire formatter includes the code once and keeps exception detail code-free.
Direct catalog failures preserve their concrete exception type and non-null Diagnostics.
No SQLSTATE mapping, protocol version or storage format is added.

## Engine-registered function calls

An engine builder may register native application scalar and aggregate implementations through
`Functions.Add`. Names follow unquoted identifier grammar and compare ordinally ignoring case.
Registrations are engine-wide, frozen before startup, and cannot replace native or reserved names.
Application overloads require disjoint arities. No CREATE FUNCTION syntax, scripting runtime,
persistent function definitions or wire message type is introduced.

Ordinary request/text/wire execution accepts named calls and resolves them against the executing
engine. Standalone parser defaults retain unknown-call CD00008 and known native arity CD00006;
the parser's explicit deferred-binding mode supports engine request parsing. Missing registrations
still fail with CD00008 before reading rows. Scalar/aggregate placement, literal kinds and grouped
expressions are validated from the function catalog, with declared result types retained for
empty/paged results. Calls remain excluded from VALUES and index seek bounds.

Native scalars and aggregates use the same validated invocation and protected-core family as
application functions. META and lazy COALESCE/IFMISSING/IFNULL/IFMISSINGORNULL remain grammar
forms. NULL/MISSING truth rules, Unicode operations, size limits, transaction clocks, native
arithmetic/domain codes, index selection and rollback semantics retain the existing contract.
External callback/result/accumulator failures use CD05017; structured diagnostic messages remain
unprefixed and wire formatting adds the code once. DatabaseException, out-of-memory, insufficient
stack and actual statement cancellation retain their identity; application I/O, storage and timeout
exceptions use CD05017 and leave the wire session reusable. No SQLSTATE or format/version bump.

Typed callback JSON parameters accept arrays and objects, not primitive JSON values. Wrong-kind
literals fail planning, while incompatible stored values yield scalar NULL or skipped aggregate
inputs. Typed integer input conversion uses CD02008 for fractional values and CD02007 for values
outside the declared CLR integer range; the callback is not invoked. An overflow thrown by the
callback itself remains the external-function diagnostic CD05017.

Engine requests admit named calls and resolve them against the executing catalog. An unknown
name retains CD00008 but now fails during binding/execution: the wire category is ExecutionFailure,
and no StatementParseFailed event is raised. Standalone parsers retain unknown-name parse
diagnostics. This changes error categorization, with no frame-layout or protocol-version change.
