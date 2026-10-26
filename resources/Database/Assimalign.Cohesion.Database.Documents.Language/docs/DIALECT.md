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
pre-existing source and mutation staging allocations are outside this new charge.

The existing Complete payload carries both result and affected counts, without negotiation,
a capability flag, a frame-layout change or a disk-format change. With RETURNING the counts
are equal and nonnegative; read-only results keep affected count -1. Updated clients accept
this combination. Older clients that require -1 for every result header reject it, as accepted
by owner decision 8 (2026-10-25).

The image/projection basis follows Couchbase's [INSERT RETURNING](https://docs.couchbase.com/server/current/n1ql/n1ql-language-reference/insert.html#returning-clause),
[UPDATE](https://docs.couchbase.com/server/current/n1ql/n1ql-language-reference/update.html#returning-clause)
and [DELETE](https://docs.couchbase.com/server/current/n1ql/n1ql-language-reference/delete.html#returning-clause).
Cohesion additionally requires transactional rollback on projection failure. RavenDB's reference
`src/Raven.Server/Documents/Patch/PatchDocumentCommand.cs:354-356` conditionally returns
ModifiedDocument; the same clone was inspected for this post-image precedent.

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

LIMIT n, OFFSET m, LIMIT n OFFSET m and OFFSET m LIMIT n execute after HAVING, DISTINCT
and ORDER BY, including system sources and INSERT/UPSERT SELECT. Each operand is an optionally
signed integer literal or a parameter with an integral, non-negative signed Int64 value.
NULL/NIL, MISSING, Boolean, string, fractional, nonfinite and out-of-range counts give CD00007
at the operand; literal 2.0/1e2, LIMIT ALL and arbitrary expressions are also invalid.
Integral decimal/JSON numeric parameters normalize before checking. Missing names retain CD01005.
Missing operands and duplicate clauses give CD00002; LIMIT m, n gives one CK00001 before
validating m, including ALL, negative and string operands. FETCH FIRST gives one CK00001
only when FETCH starts a construct; c.fetch FIRST and c.limit ALL still reject FIRST and ALL.
LIMIT/OFFSET are reserved: quote field names or use d.limit/d.offset; SELECT a AS limit is valid.
UPDATE/DELETE and INSERT/UPSERT VALUES paging stays unsupported, naming the statement and clause
(for example, DELETE LIMIT or INSERT VALUES OFFSET). Column types are computed before paging, so empty pages
retain metadata, including Int64 COUNT. Scan and seek pages share ordinal document-ID order;
groups use group-key order, system sources catalog order, and ORDER BY ties retain baseline order.
Separate pages need unchanged data or a shared snapshot for repeatability. Completion counts
only returned frames; query AffectedCount remains -1. EXPLAIN includes resolved paging counts.

The [function table](DESIGN.md#supported-clause-matrix) declares META, five aggregates and 34
SQL++ scalar functions (#1137). Scalars execute in projections, WHERE, GROUP BY, HAVING, ORDER BY,
UPDATE expressions and INSERT/UPSERT SELECT; VALUES remains constant-only. Unknown calls report
CD00008 and invalid arities report CD00006. ARRAY_AGG and the deferred REGEXP functions retain
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
UNNEST in UPDATE/DELETE, FLATTEN calls, multi-binding quantifiers, ARRAY/FIRST comprehensions, NEST,
JOIN, WITHIN and ANY AND EVERY remain deferred with CK00001.
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

CREATE [UNIQUE] INDEX [IF NOT EXISTS] name ON collection (path [, path ...]) and
DROP INDEX [IF EXISTS] name ON collection execute. Indexes have 1..32 distinct ordered paths;
root bracket-string names such as ['x.y'], nested members and integer array subscripts work.
UNIQUE is a Cohesion extension in SQL spelling and remains reserved elsewhere (SELECT UNIQUE(a)
is CK00001). Conditional creation skips an existing name regardless of its definition.
An absent conditional DROP is a no-op; existing schema ownership and snapshot conflicts still refuse.
Unconditional duplicate creation fails with CD03024; an absent unconditional DROP fails with
CD01025. An encoded composite key beyond 1024 bytes fails with CD02026. These errors
carry structured diagnostics. OQL index DDL is autocommit-only; an explicit transaction refuses
it with CD04105 before catalog access. After a failed autocommit statement the next can run immediately.
PATHS in COHESION_SCHEMA.INDEXES is the ordered array, PATH is its first entry, and IS_UNIQUE is real.

Leading MISSING omits the entry. Components sort MISSING, NULL, FALSE, TRUE, numbers, ordinal
strings, arrays, objects; full predicates always filter seek candidates. UNIQUE keys containing
any NULL or MISSING do not conflict (PostgreSQL NULLS DISTINCT, diverging from Cohesion SQL).
Duplicate valued scalar keys fail CD03020 (23505), naming index and collection without key data;
array/object UNIQUE components fail CD02021 naming index and path. Constraints are immediate
per row, so key swaps may fail. A failed explicit transaction requires RollbackAsync before reuse.
The wire uses ExecutionFailure (6) without a protocol change and keeps the connection usable.
The path-count cap reports CD00009 at the 33rd key (CD03022 for a directly constructed AST). Duplicate/empty keys, expressions, wildcards, ASC/DESC, partial
WHERE and INCLUDE MISSING receive CD00002. ALL and DISTINCT ARRAY index keys get one CK00001
at ALL/DISTINCT. The catalog uses v2 index records and refuses v1 indexes before recovery writes;
there is no upgrade path or legacy reader. Composite seeks support a leading equality prefix
and one range on the next path. Bounded snapshot-visible probes rank candidate cost first;
ties use prefix length, range, UNIQUE, then ordinal name. Comparable complete inputs may
intersect document identities before JSON reads. The full predicate always filters candidates.
EXPLAIN exposes the chosen IndexSeek or IndexIntersection and index-entry estimates, including
incomplete lower bounds and the intersection's minimum-input upper bound. It does not execute
the data statement; actual_rows remains NULL. No persisted statistics or ANALYZE command is added.

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
