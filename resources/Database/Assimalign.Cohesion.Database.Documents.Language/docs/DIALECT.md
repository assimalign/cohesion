# OQL SQL++ dialect

Double-quoted collection, property and alias names accept doubled `""` as one embedded
double quote: `"a""b"` names `a"b`. This extends the existing double-quoted identifier syntax;
it does not add backtick quoting. A missing closing delimiter is OQL0003 (#1235).

## Storage integrity errors (#1160)

A read that detects storage corruption fails with `COHDBS003`, naming the database and
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
NULL/NIL, MISSING, Boolean, string, fractional, nonfinite and out-of-range counts give OQL0007
at the operand; literal 2.0/1e2, LIMIT ALL and arbitrary expressions are also invalid.
Integral decimal/JSON numeric parameters normalize before checking. Missing names retain COHDOC005.
Missing operands and duplicate clauses give OQL0002; LIMIT m, n gives one COHDBL001 before
validating m, including ALL, negative and string operands. FETCH FIRST gives one COHDBL001
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
OQL0008 and invalid arities report OQL0006. ARRAY_AGG and the deferred REGEXP functions retain
COHDBL001. Function tokens stay usable as fields and iteration aliases.

LENGTH counts Unicode scalars. SUBSTR and POSITION use zero-based code-point positions;
POSITION returns -1 for no match. SUBSTR permits a negative start from the end, clamps starts
to the string, and returns NULL for a negative length or fractional index. String predicates
compare ordinally; LOWER/UPPER use invariant case conversion. TRIM defaults to SQL++ whitespace:
space, tab, newline, form feed and carriage return, diverging from SQL's space-only default.
Its optional second argument is a set of code points. Empty REPLACE patterns leave text unchanged.
REPLACE accepts an optional fourth integer argument n and replaces the first n occurrences;
omitting n or using any negative integer replaces all, as
[Couchbase documents](https://docs.couchbase.com/server/current/n1ql/n1ql-language-reference/stringfun.html#replace).
Zero replaces none; a fractional count gives COHDOC008.
Constructed string results and work buffers are capped at 1,048,576 UTF-16 units; inputs are
not capped. LENGTH, searching, string predicates and SUBSTR scan without input-sized buffers,
and loops observe cancellation at least every 1,024 iterations.
ROUND uses half-away-from-zero for exact numbers, with negative scales; TRUNC rounds toward zero.
SQRT of a negative number and invalid POWER domains give COHDOC019; overflow gives COHDOC007.
Fractional ROUND/TRUNC scales give COHDOC019. SQRT and nonintegral POWER use BCL double
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
`ESCAPE ''` disables it. Invalid evaluated escapes and trailing pattern escapes give COHDOC013.

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
Duplicate names in one binding list give OQL0010. The unaliased collection name is reserved
for this check: `FROM items LEFT UNNEST tags items` is a duplicate, while paths still do not
resolve an implicit items variable. Inferred duplicate aliases span the final path-name token.
Direct AST duplicates give COHDOC023. Group-key equivalence preserves lexical binding scope;
a quantified t cannot match an outer t in a grouping expression.

`ANY | SOME | EVERY x IN array SATISFIES predicate END` executes with one scoped variable.
SOME is ANY. Each predicate result undergoes SQL++ Truth conversion; NULL/MISSING results
are not true. ANY is FALSE on empty arrays, EVERY is TRUE. A MISSING collection gives MISSING;
NULL or non-array collections give NULL. `NOT ANY x IN [NULL] SATISFIES x = 1 END` is TRUE,
following [Couchbase ANY](https://github.com/couchbase/query/blob/master/expression/coll_any.go)
and [EVERY](https://github.com/couchbase/query/blob/master/expression/coll_every.go).
UNNEST, INNER, LEFT, OUTER, EVERY and SATISFIES stay contextual names. END remains reserved.
UNNEST in UPDATE/DELETE, FLATTEN calls, multi-binding quantifiers, ARRAY/FIRST comprehensions, NEST,
JOIN, WITHIN and ANY AND EVERY remain deferred with COHDBL001.
The DML refusal applies at an UNNEST clause, so `DELETE FROM items WHERE ANY x IN tags
SATISFIES unnest END` and UPDATE predicates/values may read a field named unnest. A MERGE INTO
inside a CASE result in SATISFIES receives one COHDBL001 naming MERGE.

UPSERT INTO collection [AS alias] (KEY, VALUE) VALUES (key, value) [, ...] and
UPSERT INTO collection (KEY outputName [, VALUE outputName]) SELECT ... execute with
INSERT's validation and MISSING rules. Existing identities receive complete replacements;
absent properties disappear and each written identity receives a fresh version. Without VALUE,
the complete projected row becomes the document. Every source row is staged before writes.
A repeated key fails the whole statement with DocumentDuplicateKeyException (COHDOC012).
OPTIONS and RETURNING remain COHDBL001 at their tokens. INSERT and UPSERT both reject
(KEY k, OPTIONS o) SELECT ... with one diagnostic naming OPTIONS, including its expression form. No statement-level version precondition is available;
PutAsync(expectedVersion) and UPDATE WHERE META().version = $v provide optimistic replacement.
EXPLAIN UPSERT returns Upsert or UpsertSelect; completion reports affected count without rows.
DocumentUpsertTests, the advertised-clause corpus, wire tests and the recovery fixture cover it.

DELETE FROM collection [AS alias | alias] [WHERE expression] executes through the existing
scan and equality/range index seeks. The complete predicate selects matching ids from the
statement snapshot before any deletion; META().id remains a residual filter in T1. Omitting
WHERE deletes all content while retaining the collection and index definitions. WHERE 1
matches every document; NULL/MISSING match none. A successful command reports its affected
count through Complete with no Document frames. Conflicts or cancellation roll back the
statement; failure inside an explicit transaction aborts the whole transaction. System
collections remain read-only and Schema-owned collection contents remain mutable.
EXPLAIN DELETE reports Delete plus CollectionScan or IndexSeek without writing. USE KEYS,
RETURNING and DELETE LIMIT remain COHDBL001 at their tokens. DocumentDeleteTests and the
advertised-clause corpus cover embedded execution; wire and NativeAOT checks cover completion.

UPDATE collection [AS alias | alias] SET path = expression [, ...] [UNSET path [, ...]]
[WHERE expression], or UPDATE with UNSET alone, executes through the same snapshot scan/seek
pipeline. All SET expressions read the original document; all patched documents are validated
before any write. Name segments require objects and SET can create missing intermediate objects.
Every index segment requires an existing array and an in-range index, including UNSET and
SET-to-MISSING beneath missing members; arrays are never created or expanded implicitly.
UNSET of an absent object member is a no-op. SET-to-MISSING removes object members, while
MISSING assigned to an existing array element becomes JSON null. UNSET cannot end in an index,
and root replacement is invalid. Incompatible paths give COHDOC016; duplicate and prefix-overlapping
SET/UNSET targets after alias normalization give COHDOC017. Invalid stored JSON or a patched
document deeper than 128 levels gives COHDOC018. Temporary constructors retain query depth.
Untouched values and property-name spellings retain their bytes; rewritten-container whitespace
may be normalized. A successful UPDATE returns the matched count, including unchanged matches,
and allocates fresh versions. Failure or cancellation writes nothing under the transaction rules
above. EXPLAIN UPDATE describes the access without writing. USE KEYS, RETURNING, LIMIT and FOR
remain COHDBL001. DocumentUpdateTests covers paths, atomicity, serialization and matched counts.

CREATE [UNIQUE] INDEX [IF NOT EXISTS] name ON collection (path [, path ...]) and
DROP INDEX [IF EXISTS] name ON collection execute. Indexes have 1..32 distinct ordered paths;
root bracket-string names such as ['x.y'], nested members and integer array subscripts work.
UNIQUE is a Cohesion extension in SQL spelling and remains reserved elsewhere (SELECT UNIQUE(a)
is COHDBL001). Conditional creation skips an existing name regardless of its definition.
An absent conditional DROP is a no-op; existing schema ownership and snapshot conflicts still refuse.
Unconditional duplicate creation fails with COHDOC024; an absent unconditional DROP fails with
COHDOC025. An encoded composite key beyond 1024 bytes fails with COHDOC026. These errors
carry structured diagnostics, abort an explicit transaction, and leave the session usable after
rollback. In autocommit the next statement can run immediately.
PATHS in COHESION_SCHEMA.INDEXES is the ordered array, PATH is its first entry, and IS_UNIQUE is real.

Leading MISSING omits the entry. Components sort MISSING, NULL, FALSE, TRUE, numbers, ordinal
strings, arrays, objects; full predicates always filter seek candidates. UNIQUE keys containing
any NULL or MISSING do not conflict (PostgreSQL NULLS DISTINCT, diverging from Cohesion SQL).
Duplicate valued scalar keys fail COHDOC020 (23505), naming index and collection without key data;
array/object UNIQUE components fail COHDOC021 naming index and path. Constraints are immediate
per row, so key swaps may fail. A failed explicit transaction requires RollbackAsync before reuse.
The wire uses ExecutionFailure (6) without a protocol change and keeps the connection usable.
The path-count cap reports OQL0009 at the 33rd key (COHDOC022 for a directly constructed AST). Duplicate/empty keys, expressions, wildcards, ASC/DESC, partial
WHERE and INCLUDE MISSING receive OQL0002. ALL and DISTINCT ARRAY index keys get one COHDBL001
at ALL/DISTINCT. The catalog uses v2 index records and refuses v1 indexes before recovery writes;
there is no upgrade path or legacy reader. Composite seeks support a leading equality prefix
and one range on the next path. Choice ranks prefix length, range, UNIQUE, then ordinal name.

Conformance lives in OqlSqlppBasisTests, OqlKeywordDispositionTests, DocumentMissingTests,
OqlExecutionConformanceTests, DocumentQueryTests and DocumentMissingWireTests. The wire tests
exercise both InMemory and TCP; the recovery fixture also executes IS MISSING under NativeAOT.
