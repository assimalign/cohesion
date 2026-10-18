# OQL SQL++ dialect

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
Missing operands and duplicate clauses give OQL0002; LIMIT m, n and FETCH FIRST give one COHDBL001.
LIMIT/OFFSET are reserved: quote field names or use d.limit/d.offset; SELECT a AS limit is valid.
UPDATE/DELETE paging stays unsupported. Column types are computed before paging, so empty pages
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
The array form `x IN d.tags` remains deferred. LIKE is ordinal and case-sensitive, `%`
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

Conformance lives in OqlSqlppBasisTests, OqlKeywordDispositionTests, DocumentMissingTests,
OqlExecutionConformanceTests, DocumentQueryTests and DocumentMissingWireTests. The wire tests
exercise both InMemory and TCP; the recovery fixture also executes IS MISSING under NativeAOT.
