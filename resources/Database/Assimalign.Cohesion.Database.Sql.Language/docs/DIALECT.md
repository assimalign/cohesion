# The Declared SQL Dialect

Double-quoted identifiers use doubled `""` for an embedded double quote: `"a""b"` names
`a"b` in tables, columns, aliases and function names. Rendering escapes it the same way.
An escaped final quote without a closing delimiter is CS00003 (#1235).

The contract for the Cohesion SQL surface that executes today. **Supported** means
the documented subset has been measured through a live engine, including correct
results, state changes, or intended semantic errors. Recognized clauses without
execution support report `CK00001`; unknown commands report `CS00002`; text the
parser did not consume reports `CS00003`, so nothing executes a truncated statement
(see [Statement completeness](#statement-completeness-1068)). Extending
the profile requires updating the parser, this matrix, and the engine's
`SqlLanguageConformanceTests` execution-case table in the same change. That test
enumerates the profile and fails if any advertised clause lacks a passing case.
MERGE's advertised clause is measured through action execution and RETURNING.

## Storage integrity errors (#1160)

A read that detects storage corruption fails with `CK06003`, naming the database and
page and recommending a verified backup. It never succeeds with the damaged data omitted.
The error is an execution failure over the wire; the connection remains usable.
StorageException subclasses (including slot, journal and storage-transaction failures),
IndexException and IOException become CS06041; I/O has SQLSTATE 58030. Classification
searches wrappers and aggregate branches. Attached-index corruption instead retains
CK06102 alongside the common CK06003 and model diagnostic. CK06003 maps to
XX001. Either failure aborts an explicit transaction, whose caller must roll back before
executing another statement. In autocommit the next statement runs immediately (#1364). A
legitimately reclaimed location retains its existing absence semantics. The engine's
storage-integrity conformance tests cover corrupted media through the executing language.

## Standalone sequences (#1717, SQ-S2)

CREATE SEQUENCE [IF NOT EXISTS] name accepts AS SMALLINT | INTEGER | BIGINT,
START [WITH] n, INCREMENT [BY] n, [NO] MINVALUE, [NO] MAXVALUE, CACHE n and
[NO] CYCLE in any order, each once. MINVALUE/MAXVALUE take an integer unless
prefixed with NO. ALTER SEQUENCE [IF EXISTS] accepts the same options and
RESTART [WITH n]; bare RESTART uses the configured START. START changes that
configured value without restarting. DROP SEQUENCE [IF EXISTS] accepts a name
list and optional CASCADE | RESTRICT (default RESTRICT). Names default to dbo,
share the table/view namespace, and follow the engine's case-insensitive identifier
rules, including quoted names. IF NOT EXISTS atomically skips any occupied relation name
before validating options (PostgreSQL sequence.c:135-159). IF EXISTS skips a sequence removed
during the Exclusive wait; a replacement identity still refuses CS04067. DROP validates its entire list
before one catalog commit. SQL sequence DDL is self-committing and is refused
inside BEGIN with CS04003, exactly like table DDL.

Defaults are BIGINT, INCREMENT 1, CACHE 1 and NO CYCLE. Ascending sequences use
MINVALUE 1 and the type maximum; descending sequences use the type minimum and
MAXVALUE -1. START defaults to the directional bound. AS changes a bound equal
to the former type limit to the new limit; explicit other bounds remain. CACHE is
accepted metadata: the shared allocator still reserves 32 values durably and does
not keep independent per-session caches. This makes setval immediately visible to
other sessions' next allocation. Type/option errors use CS03069 (22023); exhaustion
keeps the shared identity CS02002 (22003), deliberately differing from PostgreSQL's
2200H. Bounds, cycling and reservations reuse SQ-S1 and SQL format 28.

nextval(name), currval(name), setval(name, integer [, is_called]) and lastval()
return BIGINT. NEXT VALUE FOR also returns BIGINT; AS controls allocation bounds and metadata, without narrowing the result. This stable output type avoids stale projection metadata across a concurrent ALTER AS and differs from SQL Server's declared-sequence return type. Names may be schema-qualified text (with doubled quotes in quoted
identifiers) or numeric sequence object identities. CAST(name AS REGCLASS) and
name::regclass resolve a sequence to its BIGINT object identity for these calls;
REGCLASS is limited to sequences and is not a storable column type. References
resolve for each statement rather than pinning names across DROP/recreate.
NULL arguments propagate NULL. Calls are volatile, evaluated left to right once
per reached row; repeated nextval calls each allocate. Multi-row VALUES assigns
values in written tuple and expression order. INSERT SELECT assigns in the
source's evaluation order. Independent sequence outputs evaluate after ORDER BY and
LIMIT/OFFSET, including ordering by another output's alias or ordinal; a sequence output
used as a sort key must evaluate before sorting (PostgreSQL planner.c:6703-6763).
Without ORDER BY the source row order is unspecified;
sequence values are not ordered by transaction commit. Short-circuited expressions
do not call the allocator. Plain EXPLAIN never allocates; EXPLAIN ANALYZE SELECT
executes sequence effects. EXPLAIN also describes sequence DDL without executing it.

currval is undefined until this session calls nextval for that sequence, or
setval with is_called true; lastval is undefined until nextval chooses a sequence.
Both use session state, which survives rollback and statement failures. DISCARD ALL clears
this state and session timeout overrides outside BEGIN; inside BEGIN it reports CS04070
(25001). Pool return rolls back an open transaction, then runs DISCARD ALL through the
existing Execute exchange before reusing the connection. PostgreSQL
src/backend/commands/discard.c:59-78 likewise resets sequence caches with DISCARD ALL. setval
with omitted is_called means true: it changes currval and makes the next allocation
value + increment. False leaves currval/lastval unchanged and makes the next
allocation return value itself. setval does not select a new lastval sequence.
Allocations and setval are nontransactional; rollback leaves gaps. DROP/recreate
gets a new object identity and does not inherit session state. CS04068/CS04069
(55000) identify undefined currval/lastval; CS03073 (42P01) identifies a missing
sequence; CS03075 (22003) identifies an out-of-bounds setval. CS03077 (22023) identifies a malformed sequence reference or invalid runtime call argument.

NEXT VALUE FOR name uses the standard per-row rule: every occurrence of the same
sequence within a projected or VALUES row shares one value. For example,
SELECT NEXT VALUE FOR s, NEXT VALUE FOR s yields two equal values, whereas
SELECT nextval('s'), nextval('s') consumes two values. PostgreSQL does not implement
NEXT VALUE FOR ([CREATE SEQUENCE compatibility](https://www.postgresql.org/docs/current/sql-createsequence.html));
the per-row standard rule is corroborated by [HyperSQL's sequence documentation](https://www.hsqldb.org/doc/guide/databaseobjects-chapt.html)
and [Microsoft's NEXT VALUE FOR contract](https://learn.microsoft.com/en-us/sql/t-sql/functions/next-value-for-transact-sql).
Session/setval behavior follows local PostgreSQL src/backend/commands/sequence.c:867-1044;
option defaults and restart follow :1415-1587. Privileges remain out of scope (#177).

Sequence calls and REGCLASS are refused in persisted DEFAULT, CHECK, domain, view and SQL
function expressions with CS03074 (0A000), including typed defaults. Thus this run
creates no default dependency for CASCADE to remove; RESTRICT and CASCADE have the
same effect for standalone sequences. OWNED BY, SERIAL aliases and identity
restart/overriding/backfill remain SQ-S3 work. Compiled schemas do not declare
standalone sequences, and declared provisioning scripts refuse sequence DDL with
CS03074 until their receipt/ownership model can represent it.

Sequence statements resolve literal and parameter names to object identities and take
Shared object locks through the transaction lock manager before row evaluation.
Constant references lock only their objects. Row-varying names conservatively lock all
sequences in the directory captured at statement execution. Newly created sequences
become visible to the next statement. This blocks ALTER and DROP of unrelated sequences
until transaction end, makes one lock request per captured sequence, and a queued Exclusive request
stalls later dynamic-name statements under FIFO admission. This deliberate directory policy
keeps all waits in the deadlock graph and avoids synchronous evaluation-time lock waits; use
literal or parameter names to limit blocking to their referenced objects.
ALTER, RESTART, DROP and DROP SCHEMA CASCADE take conflicting Exclusive grants and
wait until every holder commits, rolls back or closes. Descriptions are resolved
after locking; a concurrent ALTER cannot fail a running nextval. Deadlock detection,
lock timeouts and cancellation use the ordinary engine paths. An explicit transaction
retains sequence locks after a failed statement under the existing lock contract.

INFORMATION_SCHEMA.SEQUENCES is read-only and participates in the ordinary immutable
catalog snapshot. Columns are SEQUENCE_CATALOG, SEQUENCE_SCHEMA, SEQUENCE_NAME,
DATA_TYPE, NUMERIC_PRECISION, NUMERIC_PRECISION_RADIX, NUMERIC_SCALE, START_VALUE,
MINIMUM_VALUE, MAXIMUM_VALUE, INCREMENT and CYCLE_OPTION. Precision is 16/32/64,
radix 2, scale 0; option values are invariant text and CYCLE_OPTION is YES/NO.
Identity-owned generators are not listed until SQ-S3. Existing result framing carries
all DDL, functions, EXPLAIN and metadata over InMemory and TCP. LIMIT/OFFSET sequence calls evaluate once at execution before the input rows; EXPLAIN still has no effects. Detached evaluation without a statement scope refuses CS03076 (0A000). NEXT VALUE FOR is limited to row value positions: predicates, aggregate arguments, grouping keys, direct ordering expressions and row counts refuse CS03076 before allocation; ordering by a projected alias remains valid.
Subqueries at any depth, derived tables, CTE definitions (including unused ones), and
CASE/COALESCE/NULLIF arguments also refuse NEXT VALUE FOR with CS03076 during planning,
following Microsoft's cited contract. Each remaining projected or VALUES row owns a
disposable cache scope; nested executions save and restore the outer cache. INSERT SELECT
assigns a distinct value to each row while sharing repeated standard occurrences within it.
RETURNING lists containing sequence calls evaluate before the global apply gate, keeping
durable reservation and setval commits outside that gate. Sequence effects remain nontransactional.

## Identity columns (#1123)

`id BIGINT GENERATED ALWAYS AS IDENTITY` and `GENERATED BY DEFAULT AS IDENTITY`
execute in CREATE TABLE and ALTER TABLE ADD [COLUMN]. Options are
`(START [WITH] n INCREMENT [BY] m)` in either order; both are optional, and negative
increments are supported. The SQL Server lexical spellings `IDENTITY`, `IDENTITY(seed,
increment)` map to ALWAYS. Words such as identity, generated, start and increment remain
contextual and usable as unquoted column names. Identity adds one advertised clause.

Identity requires a built-in integer type, implies NOT NULL, allows one column per table,
and excludes DEFAULT. Explicit NULL conflicts with NOT NULL, PRIMARY KEY or IDENTITY in either
order, including table primary keys and CREATE/ADD/ALTER column definitions; `CS00003` names
the column. START must fit the generator bounds; INCREMENT must be a nonzero signed Int64.
Absent START, the first value is 1 for ascending generation and -1 for descending generation.
Ascending bounds are 1 through the type maximum; descending bounds are the type minimum
through -1, following PostgreSQL `src/backend/commands/sequence.c:1462,1494,1529`.
Generation never cycles.
Definition errors use `CS03025`, naming the column, before catalog mutation. ADD identity
checks for no live rows under the existing DDL lock; deleted historical versions do not prevent it.
Typed schema-owned tables refuse identity metadata with `SqlSchemaMigrationException` /
`CS03105` and "Nothing was changed." because compiled schemas cannot represent it;
SQL script-created tables retain their SQL definitions.

Omitted INSERT columns, DEFAULT VALUES, and whole-item DEFAULT allocate one value per row,
including INSERT SELECT. BY DEFAULT accepts explicit non-null values without advancing its
counter; a generated collision then uses the existing primary-key/unique violation.
An explicit NULL fails NOT NULL. ALWAYS rejects explicit INSERT values and every UPDATE
assignment, including DEFAULT, with `CS03025`. BY DEFAULT UPDATE SET id = DEFAULT
allocates a new value. Range exhaustion fails the whole statement without partial row writes.

Allocation reserves up to 32 values durably before returning any, independently of the data
transaction. Rollback, failed constraints and restart can leave gaps; values are unique among
generated allocations, never reused after a crash, and not ordered by commit. Explicit values
can still collide. Dropping and recreating the table resets generation to START.

INFORMATION_SCHEMA.COLUMNS appends IS_IDENTITY, IDENTITY_GENERATION, IDENTITY_START,
IDENTITY_INCREMENT, IDENTITY_MAXIMUM, IDENTITY_MINIMUM and IDENTITY_CYCLE after
DATETIME_PRECISION, preserving existing ordinals. Identity option fields are nullable text,
with invariant integer spellings. IS_IDENTITY is YES/NO; generation is ALWAYS/BY DEFAULT;
cycle is NO for identity columns. Nonidentity columns have null option fields, and an identity
column's COLUMN_DEFAULT is null.

SERIAL, BIGSERIAL and SMALLSERIAL report `CK00001` suggesting GENERATED BY DEFAULT AS
IDENTITY in type positions and when creating SQL or typed-schema domains of those names.
Explicit INSERT aliases `AS overriding` and `AS "overriding"` remain valid;
only a bare OVERRIDING token in clause position is refused. OVERRIDING ... VALUE, identity
MINVALUE/MAXVALUE/CYCLE/CACHE, ALTER COLUMN RESTART/SET GENERATED, and generated stored
expressions also report named `CK00001`. Identity restart, overriding and populated-table
identity backfill remain T2. INSERT ... RETURNING exposes the allocated identity value (#1117). Validation-only full-definition ALTER COLUMN refuses identity type changes,
nullable definitions and DEFAULT; retaining the type and NOT NULL preserves generation. SET/DROP DEFAULT and DROP NOT NULL refuse identity columns with CS03025 (#1122).

## RETURNING contract (#1117)

INSERT, UPDATE and DELETE accept a final RETURNING SELECT list: `*`, target columns,
scalar expressions and aliases. A target name or alias qualifies columns; an alias hides
the original name. INSERT SELECT binds RETURNING only to the target, never to source
relations. Wildcards expand in table column order, pass-through columns keep their names,
and unaliased computed outputs use SELECT's `columnN` names and typing.

INSERT returns stored values after coercion, generated identity allocation and per-row
expression defaults. VALUES and INSERT SELECT preserve source order. UPDATE returns the
new values, while SET expressions still read the original row. DELETE returns the old
values of directly targeted rows; ON DELETE CASCADE rows and SET NULL/DEFAULT children
are excluded. UPDATE and DELETE output order is unspecified. A zero-row write returns
the same typed columns, no rows, and AffectedCount zero.

RETURNING evaluates per row inside the transactional apply step, after that row's write
and index checks, before commit; even immutable calls are not folded during planning.
Any constraint or projection error (including division
by zero) rolls back the entire statement, including inside BEGIN. Previously successful
statements in that transaction remain intact. Results are fully materialized and can be
read after ROLLBACK; an auto-commit result is delivered only after commit succeeds.

QueryAsync returns the rows and SqlResultSet.AffectedCount (n for DML, -1 for SELECT).
ExecuteAsync still returns n and discards rows; ExecuteScalarAsync returns the first
returned cell. ResultComplete already carries this count, with no wire-format change.

SQL Server OUTPUT, RETURNING ... INTO, PostgreSQL OLD/NEW pseudo-relations and
RETURNING WITH (...) are positional CK00001 refusals. A target named or aliased old/new
masks that pseudo-relation name and works normally. Ordinary output/old/new identifiers
retain their quoting rules, and UPDATE accepts a bare output target alias. DELETE's
bare output alias is reserved for OUTPUT detection: `DELETE FROM t output WHERE ...`
reports CK00001; use `AS output` to spell that alias. RETURNING subqueries report
CK00001 naming SUBQUERY in
RETURNING, even after an INSERT SELECT or a supported DML WHERE subquery. Windows also
remain CK00001; aggregates fail planning with CS05017. Modifying CTE bodies retain
CK00001 and perform no writes. Empty lists and SELECT ... RETURNING report CS00003.
ON CONFLICT ... RETURNING returns inserted and updated rows, excluding skipped proposals (#1118).
EXPLAIN reports the expanded RETURNING projection count and names for INSERT,
INSERT SELECT, UPDATE and DELETE without applying the write or evaluating the projection.

### UPDATE FROM and DELETE USING (#1714)

The parser and canonical renderer accept `UPDATE t [AS alias] SET ... FROM s1, s2 ... WHERE ... RETURNING ...`
and `DELETE FROM t [AS alias] USING s1, s2 ... WHERE ... RETURNING ...`. Sources use the SELECT
relation grammar: stored tables, views, system relations, CTEs and aliased derived queries, with
INNER, LEFT and CROSS joins. RIGHT, FULL or parenthesized joins directly in these DML lists fail CS05069 before reads or writes; RIGHT or FULL inside a derived SELECT source executes with the target pinned.
These forms execute and are advertised as UPDATE FROM and DELETE USING in the SQL profile.
Plain EXPLAIN describes the target/source association plan without applying the mutation.

Equality-driven hash or seek steps follow written source order. For example,
`UPDATE t SET v = b.v FROM a, b WHERE t.id = b.tid AND b.aid = a.id` visits |t| * |a|
pairs before b's hash; write `FROM b, a` to make these equality joins linear in the input sizes.
Results are correct in either order. General join-order search is deferred to #1629; place a
source connected by equality to the target before sources that depend on it.

SET values, WHERE and RETURNING resolve against the target plus source namespace. SET destinations
remain target columns. An alias hides the original relation name; self-joins require distinct
visible names. Unknown or ambiguous columns use located CS01015, absent tables CS01014, and
duplicate visible relation names CS01019. Each source JOIN ON sees only its own comma item's
join tree, excluding the target and other comma items. Derived source bodies cannot reference
the target or sibling inputs; explicit LATERAL remains excluded. Source columns in RETURNING
retain type, length, collation and outer-join nullability. Bare RETURNING * follows PostgreSQL
(`src/backend/parser/parse_target.c:1301-1321`): target columns first, then each visible source's
columns in FROM/USING order. JOIN USING preserves its visible output order (merged keys first,
then remaining left and right columns), omitting hidden duplicate keys rather than exposing every
physical association slot. Duplicate result names are allowed; client name lookup selects the
first match and ordinal access distinguishes them. Plain UPDATE/DELETE without sources are unchanged.
Explicit source columns execute. A real visible source named/aliased OLD or NEW
masks that pseudo-relation refusal. SET and RETURNING subqueries retain CK00001.
Delimited OLD/NEW qualifiers remain ordinary identifiers through canonical rendering.
Keyword-spelled CTE names, including `over`, `partition`, `window` and `natural`, are
accepted in definitions, qualifiers and DELETE/MERGE USING sources; WITH RECURSIVE
binds as described below.

Namespace behavior follows the local PostgreSQL `src/backend/parser/analyze.c:602-621,2560-2578`.
Target/source associations are collected before SET evaluation or writes. Multiple matching
source associations for one UPDATE target fail with structured CS05068 before any row is written,
even for equal source values. DELETE deduplicates physical targets and retains one source association
for RETURNING; which source is retained and result order are unspecified. Unkeyed equal target rows
remain distinct. UPDATE RETURNING sees new target values and collected source values; DELETE sees
old target values and collected source values. Concurrent committed target changes fail through the
existing first-updater-wins conflict, preserving the original physical identity rather than following
a replacement row. Constraint and statement-rollback behavior is the same as plain UPDATE/DELETE.
This deliberately differs from PostgreSQL's first-update behavior at
`src/backend/executor/nodeModifyTable.c:2599-2608`.

Joined associations keep the target outer. Safe target-only WHERE restrictions can seek the target;
mandatory target/source equalities use source index probes or budgeted hash joins. EXPLAIN shows
the chosen paths and algorithms. WITH definitions and source references share one materialization,
including volatile expressions. Failure/cancellation unwinds new statement locks after undo,
including locks taken by WITH/subqueries, while earlier transaction grants remain held. DELETE
retries retain collected source values and re-acquire source intents before resuming preparation.

UPDATE and DELETE without source clauses choose the same indexed equality-prefix and range access paths as SELECT,
including target aliases and qualified columns. EXPLAIN shows the chosen IndexSeek and bounds
or TableScan below the mutation. The complete WHERE remains a residual predicate, including
subqueries. Targets are collected through one statement snapshot before any write, so changing
a sought key updates each original target once; RETURNING order remains unspecified (#1274).
Execution falls back to a scan if the bound index was dropped or replaced before execution.

The row-source and target-list model follows PostgreSQL
`src/backend/executor/nodeModifyTable.c:308-330` and
`src/backend/parser/analyze.c:2796-2811`; target-only INSERT scope follows
`analyze.c:1035-1044`. OLD/NEW masking follows `analyze.c:2773-2786`.
Subqueries are a deliberate Cohesion subset exclusion.

Duplicate whole-column SET targets in DO UPDATE fail at binding before any write.
In RETURNING, a real visible target table or alias named excluded remains referenceable;
the EXCLUDED pseudo-relation itself is out of scope.

Multi-row UPDATE validates outgoing and incoming NO ACTION/RESTRICT foreign keys against
the complete final replacement state (#1667), under Snapshot and ReadCommitted. It refuses
a dangling child authorized only by a replaced parent key, permits moving a self-reference
with its key, and permits mutually referenced key swaps whose final state is consistent.
A failure leaves every row unchanged.

## INSERT ... ON CONFLICT (#1118)

`INSERT ... ON CONFLICT [(column [, ...]) | ON CONSTRAINT name] DO NOTHING`
and `... DO UPDATE SET column = expression [, ...] [WHERE predicate]` execute for
VALUES, DEFAULT VALUES and materialized query sources. RETURNING follows the conflict
clause and returns inserted and post-update rows only. Skipped rows and false or UNKNOWN
DO UPDATE filters contribute neither a returned row nor an affected count. The affected
count is inserted plus updated rows.

A column target infers **every unique index whose column set equals the target set**;
column order does not matter, and duplicate inference names collapse to that set. No matching
unique index reports CS03035 naming the target and table. ON CONSTRAINT accepts any
unique index name on this table, including a user constraint name, a generated primary-key
name read from INFORMATION_SCHEMA.TABLE_CONSTRAINTS, and a standalone CREATE UNIQUE INDEX
name: the catalog reports all of them as constraints. Unknown names, CHECK and FOREIGN KEY
names, and a legacy primary-key declaration without an index report CS03035; the legacy
case names its absent index explicitly. DO UPDATE requires a target and reports this at
planning time with CS03035. Untargeted DO NOTHING arbitrates on all unique indexes; with
no unique indexes it inserts normally. Conflicts outside an explicit target still fail
ordinary unique enforcement. If index-only DDL creates, drops or replaces unique indexes while the
statement waits for its table intent lock, the bound arbiter set is invalidated and the
transaction aborts retryably. A fresh execution binds the current indexes; an explicit target
whose index was dropped then reports CS03035.

Arbiter keys use the index's collation and temporal normalization, so case_insensitive
'ADA' conflicts with 'Ada'. **NULL conflicts with NULL** in a unique key, including a
component of a composite key. This is Cohesion's existing UNIQUE rule and differs from
PostgreSQL's default NULL-distinct behavior.

DO UPDATE expressions see the original target row (qualified by its alias, or table name
when there is no alias) and EXCLUDED. An alias hides the base table name. EXCLUDED contains
the proposed stored values after coercion, defaults, expression defaults and identity
allocation. An unqualified column common to both relations is ambiguous (CS01015).
A target named or aliased excluded makes that qualifier ambiguous too. SET expressions
all read the original target plus proposed row; SET c = DEFAULT uses the normal UPDATE
default resolver. ALWAYS identity assignments retain CS03025. Identity values allocated
for conflicting proposals are consumed, including skipped proposals. Each proposal and
its conflict projection consume defaults before the next proposal (PostgreSQL
src/backend/executor/nodeModifyTable.c:4497,2980); for example identity proposals 2 and 4
with an intervening conflict SET id = DEFAULT write identities 3 and 4.

NOT NULL, domains and CHECK validate every proposed row before arbitration. Foreign keys
validate only inserted and updated rows, so a conflicting DO NOTHING proposal with a dangling
foreign key is discarded without error. Discarded proposals never authorize a self-referencing
foreign key on another inserted row. Self-reference checks use final pending replacements,
so a rekeyed-away parent cannot authorize a dangling child, and a self-reference can move
with its key in one update. Updated rows recheck assigned-column domains, NOT NULL,
CHECK, foreign keys and incoming-reference UPDATE RESTRICT. All writes, index changes and
RETURNING evaluations share one statement bracket; a failure rolls them all back.

DO NOTHING discards duplicate proposed arbiter keys. DO UPDATE cannot affect a row a second
time (CS05036), whether the first effect inserted or updated it. An earlier DO UPDATE
rekey releases its old arbiter key in pending statement state: a later proposal may insert
that released key, matching PostgreSQL's source-order behavior. Keys introduced by an
earlier effect still count as reaching that affected row twice. A false/UNKNOWN filter
locks the existing row but does not mark it affected.

Without requested side-effecting defaults, all keys from every unique index of every proposal
are locked in one sorted batch before arbitration. A requested identity or a default containing
any non-immutable function call uses source-order per-proposal locking: evaluate its defaults,
lock all its unique keys in sorted order, then perform its latest-state lookup and conflict
projection before evaluating the next proposal. This conservative rule includes STABLE and
sequence-like function calls; constants and immutable expressions keep the up-front batch.
DO UPDATE takes live conflicting rows without waiting during arbitration, before evaluating
WHERE; a false/UNKNOWN filter retains the row lock without changing it. Old/new replacement
keys follow in a sorted batch. DO NOTHING takes no conflicting-row lock, checking visibility
under its arbiter key instead (PostgreSQL nodeModifyTable.c:1207-1211). When every proposal
conflicts and DO NOTHING writes nothing, new arbitration grants are released after visibility
checks. Keeping that transaction open therefore blocks neither UPDATE of the parent nor a
child INSERT referencing it; locks from earlier transaction work stay held.
Upserts hold only table IX, allowing different-key defaulted upserts and plain INSERTs to
progress independently. Reversed multi-row proposal orders can deadlock; the detected victim
must roll back and retry its entire transaction. Consistent input row ordering reduces
this exposure, as [PostgreSQL advises](https://www.postgresql.org/docs/18/explicit-locking.html#LOCKING-DEADLOCKS)
(doc/src/sgml/mvcc.sgml:1546-1555).
A latest-state lookup under that key lock sees committed or own entries. **Snapshot**
(the session and autocommit default) aborts retryably with DatabaseTransactionAbortedException
if a conflict was committed after the statement snapshot. In particular, two autocommit
Snapshot sessions racing ON CONFLICT DO NOTHING on a new key insert one row and abort one
contender; retry the aborted transaction. **ReadCommitted** instead skips that row for DO
NOTHING or updates its newest committed version for DO UPDATE. FK validation refreshes
ReadCommitted committed visibility after arbitration; parents committed with the new version
are visible, and other transactions' in-flight parents are excluded. A rolled-back contender's
entry is absent when its key lock releases, so the waiting proposal inserts. This follows
PostgreSQL's [transaction-isolation contract](https://www.postgresql.org/docs/18/transaction-iso.html)
and REPEATABLE READ visibility check (src/backend/executor/nodeModifyTable.c:377-405,2935).
On a contended row, arbitration releases every lock newly acquired by this statement before
waiting, keeps the granted row, takes a new retry mark, then restarts the whole multi-row
arbitration from its first proposal (#1668). Later back-offs retain earlier waited rows too.
This includes FK parent-row waits.
After a successful statement, rows kept by completed waits stay locked until the transaction
ends, even if final arbitration no longer conflicts with them; for example, the holder may
commit a rekey or delete under ReadCommitted.
A row-first UPDATE (including value-only UPDATE), DELETE,
or referential SET action can finish without a key/row deadlock with the upsert's new locks.
ReadCommitted re-arbitrates against committed state and inserts if DELETE removed the
conflict. Snapshot aborts retryably when the holder committed a change it cannot see; a
holder's rollback permits either isolation level to proceed. The wait uses the ordinary
lock manager, so deadlock detection, cancellation and database abandonment still apply.
Pre-statement locks survive back-off, including any upgrades; these retained locks and
reversed multi-row key acquisition can still cause retryable deadlocks. Keeping waited rows
also permits row-against-row deadlocks when multi-row upserts reach rows in opposite orders;
the detector picks a victim, as with PostgreSQL's residual deadlock exposure when avoiding
symmetric back-off livelock (src/backend/executor/execIndexing.c:85-95). Identities and
non-immutable defaults run again on restart, so identity gaps are intentional. No rows are
written before arbitration completes; RETURNING runs only for the final effects, each once.
Each restart holds a superset of the rows it waited for, whose previous holders have finished.
Holding those rows before keys follows UPDATE/DELETE order and immediately re-grants the same
row, preventing an endless hand-off between contenders. Cancellation is checked at every
restart. No lock wait runs inside the apply gate.
Any exception before apply begins releases this statement's new grants, including rows kept
by completed waits and arbiter keys, while an explicit transaction stays active. This includes
cancellation during or after a wait, CS05036, CHECK/NOT NULL violations and an unhandled
deadlock. Locks from earlier transaction work and their upgrades remain held. Once apply starts,
this cleanup never releases locks, even if apply fails; transaction-duration locking applies.
The gated [arbitration conformance cases](../../Assimalign.Cohesion.Database.Sql/tests/SqlUpsertArbitrationConformanceTests.cs)
measure this contract, including FK parent-row back-off and the plain INSERT audit.

The supported shape follows [PostgreSQL INSERT](https://www.postgresql.org/docs/18/sql-insert.html).
Expression, COLLATE, ordering (ASC/DESC/NULLS FIRST/LAST), operator-class and partial-WHERE
inference, DO SELECT, and subqueries in DO UPDATE SET/WHERE report named CK00001.
Subqueries there are named ON CONFLICT DO UPDATE after VALUES, SELECT and WITH sources.
EXCLUDED is absent from RETURNING scope; EXCLUDED.column there reports CK00001, unless
a real target table or alias is named excluded, which stays referenceable (PostgreSQL).
MySQL ON DUPLICATE KEY UPDATE and SQLite INSERT OR IGNORE/REPLACE report one CK00001
suggesting ON CONFLICT and execute nothing. These spellings and CONFLICT, DO, NOTHING,
EXCLUDED remain contextual, so stored identifier quoting is unchanged. MERGE executes ordered
actions, BY SOURCE and RETURNING, with target rechecks and final-state foreign keys (#1715, SQ-M2/SQ-M3).

## TRUNCATE (#1716, SQ-T1)

`TRUNCATE [TABLE] [ONLY] name [, [ONLY] name ...] [RESTART IDENTITY | CONTINUE IDENTITY] [CASCADE | RESTRICT]`
empties the complete table set in one catalog publication, returning an affected count of zero.
Omitted policies mean CONTINUE IDENTITY and RESTRICT. Repeated references to a table are
ignored. ONLY is retained in the AST and canonical rendering; there is no table inheritance.
Targets must be stored tables; persisted and system views remain read-only. Schema-owned
tables may be truncated because this changes their data, preserving ownership and definitions.
Foreign keys, defaults, checks, collations, dropped-column layout, frozen missing values and
all indexes remain defined. Neither row DELETE actions nor row scans run.

RESTRICT refuses CS03067 (0A000) unless every referencing table is included, regardless of
whether it contains rows. Self-references are allowed. CASCADE computes the recursive incoming
foreign-key closure, including cycles and tables in other SQL namespaces, independent of
ON DELETE action. The entire set is validated before publication. A concurrent identity,
definition or foreign-key closure change retains retryable CS04064/CS04006; no partial table list is published.
The closure is recomputed from the written targets at reservation and publication; added and
removed dependents both require a retry. A cascaded table dropped or renamed during the lock
wait reports CS04064 with the closure-change message; an explicitly named missing table keeps
CS01051. Concurrent FK declarations retain the initially resolved parent identities and reject
a missing or replaced parent with CS04064 before rebinding or validating existing rows.
RESTART IDENTITY resets every identity in the resulting set to its declared START, including
its declared INCREMENT. CONTINUE preserves the allocator's exact in-process next value and
unused reservation block. Reopen still skips reserved values under the existing identity
contract. Standalone sequences follow the separate sequence contract above.

TRUNCATE inside BEGIN is refused with structured CS04066 (25001); prior transaction work
and its active state remain usable. This deliberately differs from PostgreSQL's transaction-safe
TRUNCATE (local reference: src/backend/commands/tablecmds.c:2225-2258).

`CREATE TABLE [IF NOT EXISTS] t [(names)] AS query [WITH [NO] DATA]` infers its columns
from SELECT, WITH, joins, grouping or set-operation output. The default is WITH DATA.
An explicit name list must rename every output; otherwise every output needs a column name
or alias (aggregate names such as count are retained). Missing, blank, duplicate (ignoring
case) or wrong-width names fail with structured CS03068 (42601) before reservation. The complete
list rule matches Cohesion's CREATE VIEW rule; PostgreSQL also accepts a shorter list that renames
only the leading columns (createas.c:167-185).
Types, length/precision/scale, domain identities and collations follow view output inference;
untyped NULL output becomes nullable TEXT. All destination columns are nullable, as persisted
view columns are: source NOT NULL, keys, table checks, column defaults, identity and indexes are not
copied. Domain constraints and domain defaults still belong to the inferred domain type, including
the default's resolved function-call identities. WITH DATA uses normal indexed query planning;
overwide catalog metadata fails with CS05042 at reservation, before source evaluation. Values use ordinary
column assignment and domain validation. Parameters are allowed; their values are copied,
with no persisted query or source dependency.

WITH NO DATA binds and validates output without running the source, its predicates, projections,
aggregates, row limits or functions. IF NOT EXISTS against an existing table skips source binding
and execution; a view or system relation with the target name is still refused. A table name
claimed concurrently by CREATE TABLE, another CTAS or ALTER TABLE RENAME is checked atomically
at publication: the loser reports CS03049 (42P07), or completes as a zero-row no-op with IF NOT EXISTS.
CTAS reports the number of rows written as its affected count in process and over the wire,
matching PostgreSQL's SELECT n completion; WITH NO DATA, empty input and IF NOT EXISTS skips
report zero. It follows existing self-committing DDL:
inside BEGIN it fails with CS04003 (25001), leaving earlier work and the transaction active.
This differs from PostgreSQL's statement-transaction publication. A completed catalog publication
is irrevocable; cancellation after it returns the committed result. A disconnect may lose that
response. The source uses one statement MVCC snapshot. Retained query output and encoded batch
buffers share StatementMemoryBudget; exhaustion reports CS05067 and leaves no table.
The destination remains absent until its entire data build and dedicated writer transaction
are durable. Ordinary snapshot isolation follows the builder's single commit: a snapshot taken
before that commit sees no destination rows; one taken after it sees every row once the live
catalog can bind the table at publication, as with a concurrently created table. A snapshot taken
between builder commit and publication therefore sees the complete table after publication.
Statement and BEGIN admission proceed normally through that interval. No snapshot sees a partial
row set. Conversion, domain, budget, cancellation and storage failures before
publication leave no table. Crash recovery sees no table or the complete table, and reclaims
abandoned owners. SQL remains format 28; existing kind-14 reclamation records suffice.

CTAS holds the shared metadata lease for its entire source query and build, following CREATE
INDEX's precedent. Other sessions' CREATE/DROP of views, functions, domains and schemas fail
immediately during that lease. Its statement epoch also delays reclamation of owners retired
meanwhile. Source reads retain their single statement snapshot while table DML/DDL runs concurrently.
References: local PostgreSQL src/backend/commands/createas.c:90-109,167-185,242-283,342-349
(SELECT n completion), src/backend/rewrite/rewriteHandler.c:1301-1312 (domain default fallback)
and src/backend/nodes/makefuncs.c:565-582 (CTAS creates columns without source constraints).

A snapshot captured before publication can read the same old rows after TRUNCATE commits,
in process and over InMemory/TCP. Tables receive fresh physical owner identities; pinned
storage descriptions, index seeks and dependent view bindings retain the retired owners.
Direct joins, joining views and joins with untouched tables use that same snapshot. A statement snapshot
retains its owner even when auto-commit or ReadCommitted binding starts after publication.
DML targeting or joining a retired owner as a source is refused with retryable CS04006 before
writes, including INSERT SELECT and joins inside DML subqueries, views and WITH bodies; end
that transaction and retry against a fresh snapshot. Other table ALTER
binding retains its existing behavior. Current readers see empty replacements. Deferred
reclamation frees old rows and indexes only after all older transaction and statement pins end.
Prepared storage is also durably reclaimable if cancellation, an exception or a crash prevents
publication; recovery sees either the complete original set or the complete replacement set.
No disk layout, record kind or wire framing changes.

Grammar/defaults and duplicate handling follow PostgreSQL src/backend/parser/gram.y:7239-7256
and src/backend/commands/tablecmds.c:1915-1917; closure/refusal follows
src/backend/commands/tablecmds.c:2033-2090 and src/backend/catalog/heap.c:3701-3782.

## Statement matrix

With INSERT ... ON CONFLICT (#1118), DML RETURNING (#1117), persisted CREATE/DROP VIEW (#1124), CREATE/DROP SCHEMA (#1030), set operations (#1114), quantified comparisons (#1718), UPDATE ... FROM / DELETE ... USING (#1714), TRUNCATE and CREATE TABLE AS (#1716), MERGE (#1715) and aggregate FILTER (#1720), the profile has **63 of 68 declared clauses** measured against the live SQL engine, including MERGE actions and RETURNING. WITH RECURSIVE (#1719, SQ-R1) adds one declared binding-only clause; recursive execution remains CK00001 until SQ-R2.
Phase 22 measured 33 of 49.
The earlier 32/48 figure included `JOIN`, `GROUP BY`, `HAVING`, and `SUBQUERY`,
removed in Phase 12e (#1019–#1021), plus a no-op `CAST` removed in Phase 13.
Phase 14 restores `CAST` with actual conversion, type metadata, and wire execution
coverage (#1022). Phase 15 restores `JOIN` for two stored-table inner joins with
an `ON` predicate, including server/client execution (#1019); #1106 extends the same
clause to registered system relations. Phase 16 restores
`GROUP BY` and `HAVING`, including aggregation over the supported inner join
and server/client execution (#1020). Phase 17 adds executable column and expression
`COLLATE`, including persisted defaults, collation-aware index seeks, grouping,
uniqueness, and server/client execution (#1025). Phase 18 restores `SUBQUERY`
for uncorrelated scalar, `IN`/`NOT IN`, and `EXISTS`/`NOT EXISTS` queries and
adds transactional `INSERT ... SELECT`, with server/client execution (#1021).
Phase 21 restores literal `ALTER TABLE ADD COLUMN` defaults for populated tables,
including old-row and omitted-column insert values over the wire, and rejects
invalid defaults before mutation (#1023); expression defaults execute under #1121. Phase 22 resolves `ORDER BY`
projection aliases, including aliases inside scalar expressions, and select-list
ordinals through the SQL server/client (#1024). At Phase 22 the count remained 33/49 because
`ALTER TABLE` and `ORDER BY` were already advertised for smaller executable subsets.

The **5 excluded clauses** are window clauses (`WINDOW`,
`OVER`, `PARTITION`), `NATURAL`,
and `ALL`. Counts describe named clauses, not
complete ISO SQL support; the boundaries below are part of the contract.

| Statement | Status | Notes |
|---|---|---|
| `SELECT` | Supported subset, measured | Stored tables, persisted read-only views, virtual system relations and aliased derived queries, with written-order N-way INNER/LEFT/RIGHT/FULL/CROSS joins and comma items over these source kinds; `DISTINCT`, projections and aliases, scalar expressions, `WHERE`, grouping, multi-expression `ORDER BY ASC/DESC` with per-key `NULLS FIRST`/`NULLS LAST`, nonnegative integer `LIMIT`/`OFFSET`, ISO `FETCH FIRST`/`NEXT`, and the SQL Server `TOP` lexical alias. `COUNT(*)`, `COUNT(expr)`, `SUM`, `AVG`, `MIN`, `MAX`, `STRING_AGG`, and `LISTAGG` execute in grouped and ungrouped queries, with DISTINCT/ALL and ordered inputs. See the aggregate contract below. `SELECT` without `FROM` uses one implicit row, including in CTE bodies. |
| CREATE / ALTER / DROP SEQUENCE | Supported, measured | Standalone integer sequences, guarded DDL, all documented options, restart and list drops; see the sequence contract above. |
| NEXT VALUE FOR | Supported, measured | Per-row sequence value sharing; nextval/currval/setval/lastval are volatile BIGINT functions. |
| `EXPLAIN` | Supported extension, measured | SELECT, INSERT (including query sources), UPDATE, DELETE or MERGE returns the chosen plan as ordinary rows without executing it. Plain EXPLAIN remains nonexecuting. SELECT-only `EXPLAIN ANALYZE` executes the query and reports actual candidate work (#1522); data changes, including WITH data changes, normally fail `CS05057` (0A000), retaining the earlier `CS04021` read-only catalog refusal where applicable. Sequence DDL is also describable (#1717); other DDL, transaction control and nested EXPLAIN report `CK00001`. Standalone ANALYZE remains unsupported. |
| `INSERT` / `VALUES` | Supported subset, measured | Optional column list, multi-row literal/scalar `VALUES` including whole-item `DEFAULT`, `INSERT ... DEFAULT VALUES` (one row of defaults, with an optional column list), and transactional `INSERT ... SELECT` with the same destination coercion, defaults, and constraints. `VALUES` expressions have no columns in scope: a column reference reports `CS01005` before anything executes (see [VALUES and counts have no column scope](#values-and-counts-have-no-column-scope-1165)). Subqueries inside `VALUES` are excluded; use `INSERT ... SELECT`. |
| `INSERT ... ON CONFLICT` | Supported subset, measured | Column-set inference, ON CONSTRAINT unique-index names, untargeted DO NOTHING, and targeted DO UPDATE SET/WHERE with target and EXCLUDED scope; inserted/updated RETURNING rows and counts. See the upsert contract above. |
| `UPDATE` | Supported | multi-column `SET`, including `c = DEFAULT`, and `WHERE` (including correlated and uncorrelated subqueries); qualified references in both use the target name or its alias, which hides the base name (PostgreSQL `parse_relation.c:3759`); hidden/unknown qualifiers fail with located `CS01015` before any write and leave an explicit transaction active; aggregates in SET and outside child queries in WHERE are rejected; aggregates inside WHERE subqueries execute (see [Grouping and aggregate functions](#grouping-and-aggregate-functions-1020)) |
| `UPDATE ... FROM` / `DELETE ... USING` | Supported | Collected source associations feed SET, WHERE and RETURNING; UPDATE rejects multiple matches with CS05068 before writes, DELETE deduplicates physical targets. EXPLAIN shows sources. |
| `DELETE` | Supported | optional `WHERE`, including correlated and uncorrelated subqueries; aggregates outside a child query are rejected; qualified references use the target name or its alias, which hides the base name (PostgreSQL `parse_relation.c:3759`); hidden/unknown qualifiers fail with located `CS01015` before any write and leave an explicit transaction active |
| `CREATE TABLE` | Supported | Identity columns use GENERATED ALWAYS/BY DEFAULT AS IDENTITY or the SQL Server IDENTITY alias (see identity contract above). `IF NOT EXISTS`, column definitions with parameterized types, `COLLATE <name>`, `NOT NULL`/`NULL`, `DEFAULT <expression>`, column and table `PRIMARY KEY`, `REFERENCES`/`FOREIGN KEY`, `CHECK`, and `UNIQUE`; optional `CONSTRAINT <name>` |
| `CREATE TABLE AS` | Supported | Optional complete output names and WITH [NO] DATA; unpublished durable build and atomic publication. See the CTAS contract above. |
| `ALTER TABLE` | Supported subset, measured | ADD/DROP COLUMN and ADD/DROP CONSTRAINT execute. Full-definition `ALTER COLUMN c <type> [NULL | NOT NULL] [DEFAULT expression]` retains the validation-only path for nullability, length and precision when storage type, scale, collation and domain stay unchanged; DEFAULT changes on that path use SET/DROP DEFAULT. Tightening and every CHAR length change validate existing values and refuse repadding through that path. Storage-type, scale, domain and collation changes use the shared rewrite; full-definition identity-type changes retain their refusal. RENAME TO, RENAME [COLUMN], ALTER [COLUMN] SET/DROP DEFAULT and SET/DROP NOT NULL also execute (#1122). PostgreSQL TYPE/SET DATA TYPE with optional COLLATE/USING execute through the exclusive rewrite (#1447); see the ALTER TYPE contract below. ADD COLUMN nonvolatile defaults backfill old-row reads and apply to subsequent inserts that omit the column; explicit NULL follows nullability. Nullable additions without a default read NULL. NOT NULL additions to populated tables require a non-null default. Invalid defaults reject before mutation; expression defaults follow the fast-default contract below. Column COLLATE persists and governs default comparisons. See the default, atomicity and MVCC contract below (#1023). DROP COLUMN changes only the catalog: it rewrites no row, survives a crash at any point on one definition, and SELECTs running beside it read every value in its own column; see the DROP COLUMN contract below (#1241). |
| `TRUNCATE` | Supported | Table lists, per-table ONLY, CONTINUE/RESTART IDENTITY and CASCADE/RESTRICT; auto-commit only; old snapshots retain their rows and defer storage reclamation. See the contract below. |
| `DROP TABLE` | Supported | `IF EXISTS`; returns after catalog unpublication, with page reclamation asynchronous after older readers finish; the name may be recreated immediately |
| `CREATE SCHEMA` | Supported subset, measured | `CREATE SCHEMA [IF NOT EXISTS] name` persists an empty SQL namespace; AUTHORIZATION and nested schema elements report named `CK00001` errors. Namespace names compare without case and keep their stored spelling. |
| `DROP SCHEMA` | Supported subset, measured | `DROP SCHEMA [IF EXISTS] name [RESTRICT \| CASCADE]`; omission means RESTRICT. CASCADE removes contained tables/indexes and incoming foreign keys atomically, retaining external child tables. Compiled ownership blocks the operation. `dbo` cannot be dropped. |
| `CREATE INDEX` | Supported | `CREATE [UNIQUE] INDEX [IF NOT EXISTS] <name> ON <table> (<column> [, ...])` — plain column lists only (no `ASC`/`DESC`, expressions, or `INCLUDE`; each is an additive extension) |
| `DROP INDEX` | Supported | `DROP INDEX [IF EXISTS] <name> ON <table>` — the `ON <table>` qualifier is required: index names are scoped per table |
| `CASE` | Supported, measured | Simple and searched forms, multiple branches, `ELSE`, implicit null result, and row expressions; branch expressions remain limited to the executable scalar subset. |
| `ORDER BY` | Supported subset, measured | Multiple source-column/scalar-expression keys, projection aliases (bare or nested in expressions), and one-based select-list ordinals execute with ASC/DESC. Unqualified aliases take precedence over same-named source columns; qualified references bind to the source. Composes with DISTINCT, LIMIT/OFFSET, supported joins, grouped/aggregate queries, and system relations. Zero, negative, out-of-range, and non-integer numeric ordinals error; `1 + 1` remains a constant expression. Explicit NULLS FIRST/LAST and derived-table output ordering execute; GROUP BY ordinals report `CK00001`. See the ordering contract below (#1024). |
| `CAST` | Supported subset, measured | Built-in conversions across all 19 shared types according to the [conversion matrix](#explicit-conversion-cast-1022-e3), plus application-registered casts (E3's cast registry), in projections, predicates, ordering, and DML expressions, including the SQL server/client. See the exact pair and error contract below. CAST executes in DEFAULT; CHECK still rejects CAST. |
| `COLLATE` | Supported subset, measured | Column and expression overrides: `binary`, `case_insensitive`, `case_accent_insensitive`, plus compatibility `invariant` with scan execution only. Effective collation governs comparisons, `LIKE`, ordering, grouping, `DISTINCT`, and unique keys. See the collation contract below. |
| `JOIN` / `USING` | Supported subset, measured | N-way INNER (or bare JOIN), LEFT [OUTER], RIGHT [OUTER], FULL [OUTER], CROSS and comma joins over stored/system/derived sources, with parenthesized join groups. INNER/LEFT/RIGHT/FULL require ON or USING; CROSS accepts neither. FULL requires hash-compatible conditions (CS05070 otherwise). NATURAL, LATERAL and qualified wildcards remain excluded. See the precise contract below. |
| `GROUP BY` / `HAVING` | Supported subset, measured | One or more grouping expressions; `WHERE` filters input rows and `HAVING` filters groups after aggregation. Composes with supported N-way joins, `ORDER BY`, `LIMIT`, and `OFFSET`, including server/client execution. Ungrouped, unaggregated projected columns are errors. `DISTINCT`/`ALL` and aggregate input `ORDER BY` execute; STRING_AGG and LISTAGG concatenate text. Grouping extensions, windows, and other ordered-set aggregates report `CK00001`. |
| Subqueries / `INSERT ... SELECT` | Supported subset, measured | Correlated and uncorrelated scalar, `IN`/`NOT IN`, `EXISTS`/`NOT EXISTS`, and scalar `op ANY`, `op SOME` or `op ALL` queries in supported SELECT expressions and UPDATE/DELETE WHERE predicates, plus aliased derived tables and transactional insert-source queries. Each body may be a set operation (#1114). All use the outer statement snapshot; nesting is limited to 32 subquery levels, within the statement's expression nesting limit (256 levels by default, `CS00006`). Subqueries in UPDATE SET, VALUES, CHECK/DEFAULT, and LIMIT/OFFSET/FETCH/TOP expressions report `CK00001`. See the subquery contract below. |
| `TOP` / `FETCH` | Supported subset, measured | Row counts normalize to LIMIT/Offset plan fields; see [Pagination](#pagination-1107). PERCENT, WITH TIES and DML TOP retain CK00001. |
| `SELECT ALL` | Recognized, not supported | rejected with `CK00001` |
| DML `RETURNING` | Supported subset, measured | Written-row SELECT list and affected count; see the RETURNING contract below |
| `NATURAL JOIN` | Recognized, not supported | rejected with `CK00001` |
| `UNION` / `INTERSECT` / `EXCEPT` | Supported, measured | Each accepts ALL or DISTINCT (default); available in every query position, with parenthesized operands and combined ordering/paging. See [Set operations](#set-operations-1114). |
| `WITH` (CTEs) | Supported subset, measured | Ordered non-recursive SELECT bodies before SELECT, INSERT, UPDATE, DELETE or MERGE; optional output column lists; also INSERT INTO t WITH ... SELECT |
| `WITH RECURSIVE` | Parsed and bound; execution deferred | Dependency analysis, anchor output contract and PostgreSQL recursive-reference restrictions; valid recursion refuses planning/EXPLAIN/execution with CK00001 until SQ-R2. Acyclic lists execute except before MERGE. |
| `WITH RECURSIVE ... MERGE` | Recognized, not supported | CK00001 at RECURSIVE even for acyclic lists; plain WITH ... MERGE binds and executes. |
| `SELECT ... FOR UPDATE` / `FOR NO KEY UPDATE` / `FOR SHARE` / `FOR KEY SHARE` | Recognized, not supported | Complete locking strengths in the SELECT tail report CK00001, including in subqueries and CTE bodies. Deliberate change from CS00003 in #1719; qualified identifiers and implicit aliases retain their ordinary meaning. |
| Window functions / `OVER` / `WINDOW` | Recognized, not supported | function names lexed; clauses rejected with `CK00001` |
| `CREATE [OR REPLACE] VIEW` / `DROP VIEW` | Yes | Persisted read-only views; DROP defaults to RESTRICT and supports CASCADE |
| `CONSTRAINT` / `FOREIGN KEY` / `REFERENCES` / `CHECK` / `UNIQUE` constraints | Supported subset, measured | Column and table declarations normalize into constraint definitions; UNIQUE lowers to a unique catalog index. NULL is an equal index key: a second NULL violates a single-column UNIQUE constraint. Foreign-key NULL values are allowed; CHECK accepts UNKNOWN and rejects FALSE. CHECK expressions must be deterministic Boolean row expressions with the engine's scalar functions (built-in or registered, `IMMUTABLE` only), each called with arguments its signature accepts (`CS01006` otherwise); parameters and aggregates are excluded. CHECK predicates and DEFAULT expressions are stored as canonical text and parsed once per table version; see [Persisted definitions are canonical](#persisted-definitions-are-canonical). |
| `ON DELETE CASCADE` / `RESTRICT` / `NO ACTION` / `SET NULL` / `SET DEFAULT` | Supported | omitted deletion action defaults to `NO ACTION`; a cascade deletes its whole transitive closure with no depth limit, so a self-referencing chain deletes however long it is (#1164), and a cycle deletes each row once; `DROP TABLE ... CASCADE` is not supported |
| `ON UPDATE` | Recognized, not supported | absent from the profile; rejected with `CK00001` |
| `START TRANSACTION` / `BEGIN [WORK\|TRANSACTION]` / `COMMIT [WORK\|TRANSACTION]` / `END` / `ROLLBACK [WORK\|TRANSACTION]` | Supported | Spellings map to the existing BEGIN/COMMIT/ROLLBACK session commands (+0 clauses). Like every SQL statement, a valid spelling without `;` receives informational CS00009, with no error or warning; with `;` it has no parser diagnostics. START and WORK are contextual, so columns named start/work remain valid. END takes no suffix; without a transaction its CS04002 message names COMMIT. Commit and rollback observe caller cancellation only before their end starts (#1246); a pre-canceled explicit end leaves the transaction usable. Once execution completes, automatic commit ignores caller cancellation and delivers the committed result; real append and durability failures retain their normal errors. Catalog DDL that self-committed finishes mandatory cleanup with CancellationToken.None and delivers its committed result even if the caller canceled; a successful CREATE TABLE requires no retry and exists once after reopen. `SqlCommitCancellationConformanceTests` covers the end boundary and committed rows after reopen. END WORK/TRANSACTION, AND [NO] CHAIN, modes, ROLLBACK [WORK] TO [SAVEPOINT], BEGIN TRAN/named transactions and COMMIT PREPARED stay CS00003. ABORT [WORK\|TRANSACTION], SAVEPOINT, RELEASE, SET TRANSACTION and PREPARE TRANSACTION stay CS00002 (#1012); TRANSACTION alone is not a statement. |
| `MERGE` | Ordered actions, BY SOURCE and RETURNING, ReadCommitted target rechecks and mixed-action final-state foreign keys | Existing constraint/action codes; CS04064 for Snapshot target conflicts |
| `GRANT` | Not in the dialect | `CS00002` |

## Unique-build failure order (#1444)

CREATE UNIQUE INDEX and UNIQUE/PRIMARY KEY constraint backing builds construct the whole
tree before checking adjacent live keys for duplicates. This also applies to deferred index
and constraint builds. An oversized encoded key (CS05042) encountered during construction
can take precedence over a duplicate (CS03037) that occurs earlier in storage order.

Inline CREATE UNIQUE INDEX reports the smallest duplicated live key in encoded index order,
rather than the first duplicate in storage order. A safe scalar offending value, when included,
comes from that key. PostgreSQL likewise checks equality in sorted unique builds before the
row-reference tiebreaker (`src/backend/utils/sort/tuplesortvariants.c:1655-1698`); dead tuples
are excluded from its uniqueness check (`src/backend/access/nbtree/nbtsort.c:440-443`).
The existing rule that NULL components compare equal in UNIQUE keys is unchanged.

## Persisted read-only views (#1124)

`CREATE [OR REPLACE] VIEW [schema.]name [(column, ...)] AS query` stores a
canonical SELECT or set-operation body. A body can have its own non-recursive WITH,
subqueries, joins and grouping. Projection `*` is expanded at every query level,
including derived queries, CTEs and set operands, so later ADD COLUMN does not
change the view. Every input column reference is stored with its bound qualifier;
rebind rejects a changed C: identity with CS03033. An explicit column list must match the output count exactly
(the ISO rule); output names must be unique. Parameters are refused at CREATE.
Bodies receive the same SELECT-subset diagnostics as standalone queries.

Tables and views share a relation namespace. Colliding CREATE TABLE is refused
even with IF NOT EXISTS. Views retain schema identity: `dbo.v.column` and
same-name views in two schemas work independently. Views are read sources in
FROM, either JOIN input, subqueries and INSERT SELECT. Outer WHERE, GROUP BY,
ORDER BY, LIMIT and FETCH work over view columns. Each reference materializes
its body once under the caller's statement snapshot, costing O(body rows);
there is no merging or predicate pushdown. Body ordering/windowing follows the
existing derived-body rules. Overloaded functions in ORDER BY expressions use the
completed projection types when their arguments name output aliases. Those aliases
are not input dependencies. Cached bodies use scans for table and join inputs;
LIMIT, OFFSET and FETCH count expressions run once per execution in the current
statement context.

OR REPLACE retains every existing column's name, order, type, type modifiers,
domain and collation and permits appended columns only. Incompatible replacement
and dependency cycles fail atomically. Definitions are parsed once per immutable
view and bound once per catalog generation; recorded function identities keep a
later overload from changing a stored call. COLUMNS uses frozen type metadata without binding a
body, including full length, precision and scale. CASE and COALESCE keep modifiers when every
result alternative has the same type, domain and modifier; otherwise the result is unconstrained.
Identical domains survive CASE, COALESCE and set outputs (PostgreSQL
`src/backend/parser/parse_coerce.c:1360-1363,1658-1660`). USING joins coerce facet mismatches to the
unconstrained common type (`src/backend/parser/parse_clause.c:1680-1700`); persisted CHAR modifier
relabels use an equivalent CASE expression so bare CHAR's length-one default cannot truncate values.
NULLIF keeps its first argument's modifier. Set operations apply the same agreement rule per
column, including function-call operands (PostgreSQL `src/backend/nodes/nodeFuncs.c:327-480`,
`src/backend/parser/parse_coerce.c:1648-1678`). Every view column reports IS_NULLABLE = 'YES',
following PostgreSQL view attributes without NOT NULL (`src/backend/catalog/information_schema.sql:674-676`).
Query output nullability follows current bound sources.
A recoverable body binding failure affects only reads of that view, with a coded error retaining
the original exception; it never blocks open, unrelated reads, metadata enumeration or DROP VIEW.
OperationCanceledException, InsufficientExecutionStackException, OutOfMemoryException,
StorageException (every subtype, including offline, format, journal and slotted-page failures),
and IOException propagate, including through inner exceptions and aggregates. At open, stack
exhaustion retains the larger-stack advisory; reads report CS05004 and StatementTooComplex.

INSERT, UPDATE, DELETE, ALTER TABLE, CREATE INDEX and DROP TABLE require a table
and refuse a view, naming its kind. DROP VIEW refuses a table, including IF EXISTS.
DROP TABLE and DROP FUNCTION/DOMAIN refuse dependent views; DROP COLUMN and
RENAME COLUMN refuse columns a view reads. ALTER COLUMN also refuses type, length,
precision and scale changes on those columns (CS03302). RENAME TABLE also refuses dependents
because canonical bodies retain textual relation names. DROP SCHEMA RESTRICT
counts views. DROP SCHEMA CASCADE removes contained views and external views
that depend on its tables or views. DROP VIEW [IF EXISTS] defaults to RESTRICT;
CASCADE removes dependent views. Dependency failures name the view (CS03302).
View-specific declaration/replacement failures and oversized records carry structured CS03033
diagnostics in process and over the wire; ordinary query binding diagnostics still apply,
including CS05013 for a column-list count mismatch.

Views appear in TABLES (`TABLE_TYPE = 'VIEW'`), COLUMNS, VIEWS and OBJECT_OWNERSHIP.
VIEWS returns canonical VIEW_DEFINITION, CHECK_OPTION `NONE` and IS_UPDATABLE `NO`.
Session-created views are Adhoc; declared scripts use Schema ownership and atomic
`V:` publication/absence receipts. View DDL requires auto-commit (CS04003 inside
an explicit transaction). SQL record-space format **23** adds catalog kind **17**;
older formats are refused without an upgrade reader. The complete encoded record,
including text, columns and dependencies, is limited to **8,092 bytes**; CREATE
refuses a larger record before publishing anything.

MATERIALIZED, RECURSIVE, TEMP/TEMPORARY, IF NOT EXISTS, WITH (options), and
WITH [LOCAL|CASCADED] CHECK OPTION are excluded with CK00001 at CREATE.
Recursive, materialized and updatable views remain future work. DROP VIEW CASCADE
is included by the owner ruling for this item, superseding the original T2 exclusion.

## SQL namespaces (#1030)

A SQL schema is a namespace, independent of the compiled code-first schema that owns
provisioned objects. Every new database persists `dbo` as an ordinary Adhoc namespace;
SQL refuses its removal with `CS03031` because code-first provisioning remains pinned
to `dbo`. Sql.Schema builders have no target-namespace setting. Creating another namespace
does not redirect compiled tables, constraints, indexes, domains or declared migration SQL.

`CREATE TABLE s.t`, unguarded `DROP TABLE s.t`, `ALTER TABLE s.t` and table-qualified
`CREATE INDEX ... ON s.t` / `DROP INDEX ... ON s.t` require an existing namespace
(`CS01028`, PostgreSQL SQLSTATE 3F000). `DROP TABLE IF EXISTS missing.t` succeeds as a
no-op, retaining its declared-script absence receipt. Reads and ordinary DML of `missing.t`
retain the missing-relation `CS01014` (42P01). Unqualified table names
use `dbo`; an unqualified REFERENCES target uses its child table's namespace. A table created
as `Sales.t` after `CREATE SCHEMA sales` reports `sales` in TABLES and SCHEMATA.
INFORMATION_SCHEMA and COHESION_SCHEMA are reserved virtual namespaces: CREATE/DROP SCHEMA
and CREATE TABLE in either receive `CS04021`, including existence guards.

Duplicate namespaces receive `CS03029` unless IF NOT EXISTS is supplied. RESTRICT on a
nonempty namespace receives `CS03030`: it cannot be dropped because other objects depend
on it. CASCADE removes every contained table/index and each external incoming FK in one
catalog transaction; external tables and their rows survive. Tables referencing an external
parent disappear without changing that parent. Namespace, table and index compiled ownership
and external child ownership are checked before commit using the existing ownership diagnostic
with operation `DROP SCHEMA`. Both schema statements are self-committing DDL and receive
`CS04003` inside an explicit transaction.

DROP SCHEMA acquires table locks before its definition metadata lease and revalidates the
namespace contents before publication. A changed set receives `CS05013` with a retry remedy.
Deferred typed migration steps wait cancellably for definition metadata leases; overlapping
schema DDL cannot fail a migration merely by holding that lease.

After DROP SCHEMA/TABLE/INDEX or ALTER TABLE commits its catalog change, caller cancellation
cannot interrupt required binding or live index-directory cleanup. Cleanup attempts every
affected object before reporting aggregated failures; DROP's retired pages remain subject to
asynchronous reclamation.

AUTHORIZATION, including the form with no explicit namespace name, is rejected with CK00001
naming AUTHORIZATION. Nested CREATE/GRANT schema elements are rejected with CK00001 naming
schema elements, including with IF NOT EXISTS. SCHEMA and AUTHORIZATION stay contextual;
ordinary identifiers named schema or authorization retain their quoting and binding behavior.

SQL record-space format **21** adds namespace record kind **15**. Earlier databases whose
tables only named namespaces are refused without synthesizing records or writing during open.
Export with the matching old engine, drop and recreate the database, explicitly CREATE SCHEMA
for each non-dbo namespace, then import. There is no upgrade path. The new format also fences
older engines, which cannot decode kind 15.

Reference semantics: PostgreSQL `src/backend/parser/gram.y:1164-1168` defaults DROP to RESTRICT;
`:1578-1629` defines CREATE SCHEMA and rejects elements with IF NOT EXISTS;
`src/backend/catalog/namespace.c:673-683,3605-3616` distinguishes namespace creation lookup;
`src/backend/commands/tablecmds.c:1498-1506,4534-4539`, `src/backend/tcop/utility.c:1480-1483`
and `src/backend/catalog/namespace.c:526-531` distinguish missing namespaces in table/index DDL;
`src/backend/parser/parse_relation.c:1463-1487` supplies missing-relation lookup;
`src/backend/catalog/dependency.c:1163-1170` supplies dependent-objects wording; and
`src/backend/catalog/pg_constraint.c:304-324` makes the FK depend on its parent rather than
making its child table depend on that parent. These semantics are verified in process and
through SqlDatabaseServer and Sql.Client.

## Plan descriptions (#1104)

Grammar: `EXPLAIN [ANALYZE] (select | insert | update | delete) [';']`. The wrapped statement retains
normal name binding, parameter validation and planning errors. Plain EXPLAIN never calls its data
executor, including inside a transaction that later commits. Probe estimates may be present;
actual counts remain null. SELECT-only EXPLAIN ANALYZE executes and reports candidate work
(#1522); analysis of data changes is refused before execution. PostgreSQL likewise distinguishes planning from execution
with `EXEC_FLAG_EXPLAIN_ONLY` in `ExplainOnePlan` (local reference clone,
`src/backend/commands/explain.c:562-566,574-576`); Cohesion accepts only the documented data-statement subset.

The result's eight columns, types, nullability and order are the shared
[Execution contract](../../Assimalign.Cohesion.Database.Execution/docs/DESIGN.md#plan-descriptions-1104).
Operator names and seek-bound details belong to the
[SQL engine](../../Assimalign.Cohesion.Database.Sql/docs/DESIGN.md#explain-operator-vocabulary-1104).
Rows travel unchanged in existing SQL header/row/complete frames.

## Admission during deferred provisioning (49a)

While a code-first migration runs on populated tables, statements referencing its reserved
tables wait before binding. This includes FK parents in CREATE TABLE, ALTER ADD COLUMN and
ALTER ADD CONSTRAINT, so a pending unique index is available when the FK is checked.
EXPLAIN recursively admits its wrapped statement through the same table reservations before
planning, so its access path reflects the completed migration.
After admission, writes enforce the completed column nullability and bounds. Unrelated
tables remain available. Code-first changes to built-in `VARCHAR` length and `DECIMAL`
precision use the validation-only ALTER path described above; a storage, domain, scale or
DEFAULT rewrite remains unsupported.

Referential SET NULL and SET DEFAULT update only matching foreign-key columns, using the
ordinary domain, nullability, CHECK, outgoing/incoming FK, MVCC and index machinery.
NO ACTION and RESTRICT check the complete statement deletion set. A row also reached
by CASCADE is deleted once and receives no SET action. Any child failure rolls back the
whole delete. Snapshot-isolated SET actions reject child versions newer than the transaction
snapshot; their first-updater-wins diagnostic names the action. ReadCommitted SET actions
validate outgoing references against current parents after a lock wait, including parents
committed by the child writer. SET DEFAULT with no default
supplies NULL; each expression default uses the delete transaction's clock. Unique NULLs
remain equal. SET DEFAULT uses the ordinary assignment
path for identity columns: BY DEFAULT allocates, and GENERATED ALWAYS retains its CS03025
UPDATE refusal. PostgreSQL-style column lists after SET NULL/SET DEFAULT and ON UPDATE actions
report CK00001. Schema-owned typed foreign
keys retain RESTRICT/CASCADE only. SQL record-space format 20 gates the new persisted
referential-action values; format 19 and earlier are refused with the recreate remedy.

## Statement completeness (#1068)

A request carries exactly one statement, and the parser either consumes all of
it or reports what it could not. One terminating `;` is optional, and comments
may follow it. Any other text after a complete statement reports `CS00003` at the
first token the parser did not consume, and nothing executes: the text-execute
seam throws `DatabaseParseException` (`ParseFailure` on the wire), and a typed
request returns an error result carrying the diagnostics. Until #1068 the parser
checked for leftover tokens only after `CREATE TABLE`, `ALTER TABLE` and
`DROP TABLE`, so every other statement executed the prefix it understood:
`DELETE FROM t WHRE id = 1` read `WHRE` as an alias of `t` and deleted every row.

Required tokens are required. A missing closing `)`, `THEN`, `END`, the `AND` of
`BETWEEN`, the `BY` of `ORDER BY`, the `SET` of `UPDATE`, the `=` of an
assignment, a table, index or column name, or a `VALUES` row reports `CS00003`
where it was expected. A clause keyword in a name position means the name is
missing: `SET a = 1, WHERE id = 1` reports the trailing comma instead of
assigning `(id = 1)` to a column named `WHERE`. `IF NOT EXISTS` (CREATE) and
`IF EXISTS` (DROP) must be complete, and a column's `NOT` must be followed by
`NULL`. A `?` placeholder name never executes, because it is built only together
with an error.

Lexical errors are reported too. A string literal, quoted identifier or block
comment without its closing delimiter would otherwise run to the end of the text
as one token and swallow the clauses after it: `DELETE FROM t /* WHERE id = 1;`
deleted every row. Each reports `CS00003` at its opening delimiter. A character
outside the dialect, such as `?`, `#`, `^`, `§`, a backtick or a zero-width space,
lexes as `TokenType.Unrecognized` and reports one `CS00003` at the character
(#1101). It used to lex as a one-character identifier, so `SELECT * FROM t ?` bound
`?` as an alias and ran, and `SELECT ? FROM t` returned a column named `?`. The
statement now keeps only its command type: no alias, column or `NULL` placeholder
is built around the character. An operand `?` receives the shared supported-form
message, as does `:name`; a trailing stray `?` retains the generic character message.
A non-ASCII decimal digit, such as Arabic-Indic `٣` or fullwidth `１`, is a character
outside the dialect too:
only ASCII digits make a numeric literal. It used to lex as an integer, parse
cleanly and then fail at execution with an uncoded `FormatException`. A stray
character before the statement, such as a byte order mark that was not stripped,
is reported once, and the statement after it is not reported as an unknown
command. A numeric literal whose exponent has no digits, such as `1e` or `2.5E-`,
reports `CS00003` at the literal; it also used to fail only at execution.

| Written | Reported as `CS00003` | Diagnostic starts at |
|---|---|---|
| `DELETE FROM t WHRE id = 1` | Leftover text after the DELETE; the message adds that `WHRE` was read as an alias of table `t` | `id` |
| `UPDATE t SET a = 1 WHRE id = 1`, `UPDATE t SET a = 1 FROM u FROM v` | Leftover text after the UPDATE | `WHRE`, the second `FROM` |
| `SELECT ... LIMIT 1 FETCH FIRST 1 ROW ONLY` | two row-limit clauses in one SELECT | `FETCH` |
| `a NOT NULL`, `flag NOT FALSE`, `x NOT y` | `NOT` after an operand must begin a recognized predicate: BETWEEN, IN and LIKE execute; unsupported ILIKE/SIMILAR forms use CK00001. It used to parse as `x AND NOT y` | the token after `NOT` |
| `WHERE id = :id`, `SET a = :a`, `WHERE id = ;`, a statement ending in `WHERE` or `=` | Expected an expression; the NULL literal the parser used to substitute is gone | the token, or the end of the text |
| `WHERE (id = 1`, `id IN (1, 2`, `id BETWEEN 1 2`, `CASE WHEN c 'a' END`, `ORDER id`, `UPDATE t;`, `DELETE;`, `VALUES (1), ()` | The missing token, keyword, name or value | where it was expected |
| `VARCHAR(25 5)`, `DECIMAL(10, 2, 5)`, `VARCHAR(-5)`, `CHAR(MAX)` | Type arguments must be one or two unsigned integer literals, except MAX on VARCHAR/NVARCHAR/VARBINARY | the first offending token |
| `'abc WHERE id = 1`, `"id FROM t`, `/* WHERE id = 1` | Unterminated literal, quoted identifier or comment | the opening delimiter |
| `SELECT * FROM t ?`, `SELECT a # b FROM t`, `WHERE b = ?`, `WHERE id = ١` | A character outside the dialect; nothing is bound around it | the character |
| `SELECT 1e FROM t`, `WHERE a = 2.5E-` | A numeric literal whose exponent has no digits | the literal |
| `SELECT 1; SELECT 2`, `BEGIN; DELETE FROM t`, `COMMIT;;` | A request accepts exactly one statement | the first token after `;` |

`a<-1` and `a <- 1` mean `a < -1`: `<-` is not a SQL arrow (#1647).
`a<--1` is `<` followed by the line comment `--1`, so a missing right operand reports CS00003.
This follows PostgreSQL `src/backend/parser/scan.l:861-932` (comment boundaries and trailing-sign
splitting); `SqlLeftArrowConformanceTests` pins it.

A `--` comment ends before the first line terminator: line feed (LF), carriage
return (CR), next line (NEL, U+0085), line separator (LS, U+2028) or paragraph
separator (PS, U+2029); CR LF is one line break (#1150). The terminator set is the
one `Database.Language/docs/DESIGN.md` records for every language. The comment
used to end only at LF, so `DELETE FROM t -- note<CR>WHERE id = 1;` parsed as an
unfiltered `DELETE` and removed every row, and `UPDATE t SET a = 1 -- note<CR>WHERE
id = 1;` updated every row. The leftover-text rule above could not catch either,
because the swallowed clause was part of the comment token, not text after the
statement. Both now affect exactly one row, on both session seams and over the
wire, and a statement on the line after a comment is leftover text. Text on the
comment's own line is still comment text, vertical tab and form feed do not end a
comment, and a string literal or block comment may span lines. `//` is not a
comment: `/` is the division operator, so `//` is a syntax error.

A recognized clause outside the profile keeps its `CK00001`, for example
`WINDOW`. The parser stops at such a clause by design, so the text
from that clause on adds no second diagnostic. Text the parser stopped at before
it reached the clause still reports `CS00003`: `DELETE FROM t WHRE id = 1
WINDOW w AS ()` reports both, so fixing `WINDOW` does not reveal a new error. A
derived table, `(SELECT ...) alias`, is parsed as a query relation; its alias is required.
Named `:name` parameters are not part of the dialect; bind `@name` or `$1`.

A syntax error is reported once per position: recovery leaves the offending
token in place for the enclosing parser, which finds it already reported. One
mistake can still yield two diagnostics when it breaks two independent rules, for
example an unclosed type argument list followed by a second statement after `;`.

**Signs.** The operand of a sign is itself a unary expression, so `- -1` is
`1`; it used to parse as a negated NULL followed by leftover text. `+` is ISO
unary plus on any operand: `+a`, `+(1 + 2)` and `+@p` parse to a unary-plus node
(`SqlUnaryOperator.Plus`). Directly before a numeric literal the `+` stays part of
the literal, so `+1` is the literal `1` and `ORDER BY +1` remains an ordinal; `+(1)`
is unary plus applied to `1`. Unary plus returns a numeric operand with its value
and type unchanged (`+tiny` is still `TINYINT`, unlike `-tiny`, which computes in
BIGINT) and propagates NULL. Both signs require a numeric operand. When the plan
already knows the operand is not a number (a string or Boolean literal, a
string, Boolean, date/time or other non-numeric column, a predicate, `||`,
`UPPER`/`LOWER`, a CAST to a non-numeric type, or a scalar subquery of such a
type), the statement fails before it reads a row, with `CS02003` (see the
[arithmetic contract](#arithmetic-and-numeric-faults-1069)): `+'abc'`, `+TRUE`
and `-name` fail over an empty table exactly as over a populated one. A sign
directly over a parameter is checked against the value the statement binds, also
before any row is read: `+@p` with `@p = 'abc'` fails with `CS02003` whether or
not the table has rows, and a NULL value propagates. An operand whose type only a
row's value reveals, such as a `CASE` that returns a string, fails with the same
code when the row is evaluated. `+a` and `+(1 + 2)` reported
`CS00003` before unary plus existed. `~` is not a sign of the dialect; see
[Expressions](#expressions).

**ALTER TABLE actions.** `ADD [COLUMN]`, `ADD CONSTRAINT`, `DROP [COLUMN]`,
`DROP CONSTRAINT`, full-definition `ALTER COLUMN`, RENAME and ALTER column default/nullability actions parse.
TYPE/SET DATA TYPE with optional COLLATE/USING also parse. SET SCHEMA, RENAME CONSTRAINT and comma-separated action lists report named `CK00001`.
Other unknown or incomplete actions such as MODIFY, ALTER c INT, ALTER c SET/DROP,
ALTER CONSTRAINT, bare SET, SET TABLESPACE and SET LOGGED report `CS00003` at parse time.
SET SCHEMA is named only when SCHEMA follows SET; syntax diagnostics list all supported actions.
The statement then carries no action node. It used to carry a placeholder
`DROP COLUMN ?` that failed later, in the catalog. A missing action, table name,
column name or constraint name also reports `CS00003`; `ADD` or `DROP` directly
after `ALTER TABLE` is a missing table name. `TABLE` is required: `ALTER INDEX`,
`ALTER VIEW` and other `ALTER <object>` forms report `CS00003` naming the object.

**Unknown functions.** A call to a name outside the engine's function catalog (the
standard library and the functions the application registered on the engine, see
[Builtin functions](#builtin-functions)), the executable profile and the recognized-unsupported
vocabulary fails at plan time with `CS01016` and `Unknown function '<name>'.`, a `DatabaseException`
(`ExecutionFailure` on the wire). The planner checks every expression position
before it binds the statement or reads a row: projections, predicates, joins,
grouping, ordering, `LIMIT`/`OFFSET`, subqueries, DML values and `CHECK`. The
statement therefore fails the same way over an empty table as over a populated
one; the evaluator used to find the name per row, so an empty table succeeded.
Recognized window names that do not execute yet, such as `RANK` and `LEAD` (see
[Builtin functions](#builtin-functions)), are not unknown. They reject at parse time
with one named `CK00001` and are absent from the executable profile (#1103).

**Function arguments (#1189).** A call to an executable function must pass a number
of arguments its signature accepts (the table under
[Builtin functions](#builtin-functions)): `UPPER`, `LOWER`, `LENGTH` and `ABS` take
exactly one, `COALESCE` one or more, COUNT/SUM/AVG/MIN/MAX exactly one, STRING_AGG two, LISTAGG one or two, and only `COUNT`
accepts `*`. Any other call fails while planning with `CS01006`, a
`DatabaseException` (`ExecutionFailure` on the wire), in every position the
unknown-function check covers: projections, `VALUES`, `UPDATE ... SET`, predicates
(`WHERE`, `JOIN ... ON`, `HAVING`), grouping, ordering, `LIMIT`/`OFFSET`,
subqueries, `CHECK` (in `CREATE TABLE`, `ALTER TABLE ADD CONSTRAINT` and `ADD
COLUMN`) and `DEFAULT`. Nothing is read or written, and the statement fails the same
way over an empty table as over a populated one. The message names the function as
written, the counts it accepts, what the call passed and the accepted call forms:

```text
CS01006: Function 'ABS' takes exactly 1 argument but was called with 2. Accepted: ABS(numeric).
CS01006: Function 'COUNT' takes exactly 1 argument or '*' but was called with 2. Accepted: COUNT(*) or COUNT(value).
CS01006: Function 'SUM' takes exactly 1 argument but was called with '*'. Accepted: SUM(numeric).
CS01006: Function 'COUNT' takes exactly 1 argument or '*' but was called with 2 arguments including '*'. Accepted: COUNT(*) or COUNT(value).
```

A call passes `'*'` only when `*` is its sole argument; the parser also accepts `*`
after other arguments (`COUNT(id, *)`, `COALESCE(a, *)`), and such a call is reported
by its full count.

**Overloads (E2).** A function name can carry several overloads, distinguished by their
parameter types. Among the overloads whose count matches, the planner chooses the one
the arguments' types fit best: an exact type, then an implicit widening along INT8 →
INT16 → INT32 → INT64 → DECIMAL → DOUBLE (nothing converts to or from text, and REAL does
not widen), then a polymorphic overload. An integer literal has the smallest of INTEGER
and BIGINT that holds it; a NULL literal, a NULL parameter and a grouped value match any
type. A call no overload's types accept also reports `CS01006`, and one that several
accept equally well reports `CS01008` (SQLSTATE 42725); a `CAST` on an argument
chooses. Both are raised while planning:

```text
CS01006: Function 'SUM' has no overload that accepts argument types (TEXT). Accepted: SUM(numeric).
CS01008: Function call 'describe(unknown)' is ambiguous: describe(BIGINT) and describe(TEXT) accept it equally well. Cast an argument to choose one.
```

The engine resolves a call's arguments before the call itself, as PostgreSQL's parse
analysis does, so `COALESCE(name, UPPER())` reports `UPPER`, and `FOO(ABS())` reports
`ABS` rather than the unknown `FOO`. Until #1189 the evaluator evaluated an argument
only when a call had exactly one, so `SELECT ABS(1, 2), UPPER(), COALESCE() FROM t`
returned three NULLs, `CHECK (ABS(c, 1) > 0)` was stored and admitted every row, and
an aggregate with no argument outside a projection (`UPDATE t SET a = SUM()`)
succeeded over an empty table. A wrong aggregate count in a projection failed while
planning already, without a code. A recognized name that does not execute has no
signature, and fails as described above. A database whose catalog stores a CHECK
with such a call does not open; see [Persisted definitions are
canonical](#persisted-definitions-are-canonical).

**Guard.** `SqlStatementCompletenessTests` (Sql.Language) holds a complete statement
form for every profile clause and every `SqlQueryCommandType`, appends leftover
text to each form (words, a literal, `)`, a misspelled clause, text after `;`,
an unterminated string or comment, a character outside the dialect, and words on
the line after a `--` comment for every line terminator), and fails unless every
combination reports an error after the form. A clause added to the profile
without a form fails the test, as does a new statement kind.

## Persisted definitions are canonical

Every SQL expression the catalog persists is stored as **canonical SQL rendered
from its parsed tree**, never as the text the user wrote: a `CHECK` predicate, a
column literal and expression `DEFAULT` (#1121), and
persisted view queries (#1124), which use the same path. `SqlExpressionRenderer` (this
package) produces the canonical text; the engine's one persistence helper renders
it, proves before anything is stored that the text parses back to the declared
tree, and is the only code that reads it back. The canonical form is a function of
the tree alone:

- Keywords are upper case (`AND`, `IS NOT NULL`, `NOT BETWEEN ... AND ...`,
  `CASE ... END`, `TRUE`, `NULL`); collation names are lower case, as the parser
  normalizes them.
- Binary operators, `AND`/`OR` and list separators take single spaces; `!=` is
  written `<>`. Comments and line breaks are dropped.
- Parentheses appear only where precedence needs them: `(qty + 1) * 2` keeps
  them, `((qty - 1) - 2)` becomes `qty - 1 - 2`, `qty - (1 - 2)` keeps them. A sign
  applied to a sign is parenthesized (`- -1` becomes `-(-1)`, never a `--`
  comment), and `+(1)` keeps its parentheses so it stays unary plus. An `AND` or
  `OR` chain is written term by term: `(a AND b) AND c` becomes `a AND b AND c`,
  and `a AND (b AND c)` keeps its parentheses, as it did before chains became one
  node (#1151).
- Literals keep their value: strings double embedded quotes (`'O''Brien'`),
  numbers keep their digits as written (`007`, `1.5e3`, `.5`), and a `+` that was
  part of a numeric literal is gone (`+5` is `5`). Typed literals store parser-normalized
  canonical text: DATE/TIME/TIMESTAMP/TIMESTAMPTZ prefixes, INTERVAL total seconds,
  uppercase X hex, and ordinary quoted text for N strings.
- Names keep their spelling. An identifier is delimited with `"` only when the
  lexer would not read it back as one plain word: a keyword, a builtin function
  name, a word with a positional meaning such as `escape`, a name with spaces,
  punctuation or a combining mark, or a name starting with a digit. `"qty"`
  becomes `qty`, while `"order"` and `"my qty"` keep their quotes; unicode words
  such as `größe` stay bare. Function names keep their spelling (`lower(name)`); a
  user function named like a window function stays delimited (`"rank"(x)`).

For example, `CHECK (QTY>0   and /* upper */ qty<100)` is stored, and reported in
`INFORMATION_SCHEMA.CHECK_CONSTRAINTS.CHECK_CLAUSE`, as `QTY > 0 AND qty < 100`,
and `DEFAULT +5` as `5`. The renderer's corpus and a randomized round-trip test
(`SqlExpressionRendererTests`) pin both the canonical spelling of every form a
CHECK accepts and the invariant that parsing the rendered text yields the same
tree. A CHECK supplied by a compiled schema (`table.Check(name, sql)`) keeps its
author's spelling in the schema document; the engine compares it with the catalog by
canonical form, so reapplying an unchanged schema stays a no-op. Before the engine's
build touches any file it parses that text as exactly one expression and binds it to
the engine's functions and the table's declared columns, applying the same rules
`CREATE TABLE` applies (only `IMMUTABLE` functions among them), and refuses the
declaration with `CS03101` when it does not bind.

**Size.** A table's whole definition, including canonical CHECK/DEFAULT text and the
resolved function identities (consecutive repeats are compacted), is one catalog record
of at most 8,092 bytes. That bounds a stored CHECK
at a few hundred comparisons however the nesting limit is configured; a larger
definition fails its DDL with `The definition of table '<name>' encodes to <n>
bytes, more than the 8092 bytes a catalog record can hold.` and stores nothing.

**Parsed once.** The engine parses and binds a table's persisted CHECK and DEFAULT
definitions once per table version — when the database opens, and when a DDL
statement (`CREATE TABLE`, `ALTER TABLE ADD/DROP CONSTRAINT`, `ADD/DROP COLUMN`)
publishes a new version — and caches the result. Validated writes evaluate the
cached predicate; no statement parses catalog text. A dropped, re-created or
altered table is a new version, so no write is ever checked against a stale
definition.

**Fails at open.** Because canonical text always reloads, a persisted definition
that does not parse, is more than one expression, or no longer binds to its table
means the catalog is damaged or came from an incompatible engine build. Opening
the database then fails with `Database '<name>' cannot be opened.`, naming the
table and the constraint or column (`CHECK constraint 'ck_qty' on table
'dbo.orders' cannot be loaded: ...`). It never surfaces later as a failure of
every write to that table. A CHECK whose call names an application function the
opening engine no longer registers is not damage and does not fail the open: its
writes fail with `CS01009` instead (see Builtin functions, "A stored CHECK whose
function is gone"). Canonical storage is part of data-storage format 4;
text from earlier formats is not migrated, and a database on an earlier format
whose catalog holds a CHECK or DEFAULT fails the open with a format error that
says so.

**Binding is not re-validation.** Opening a database binds each stored CHECK —
its columns and collations resolve, each function call binds to its recorded signature
and remains Immutable, and it is a Boolean row predicate the engine can evaluate — but
does not apply again the rules `CREATE TABLE` and `ALTER TABLE` use to accept one,
such as the sign operand check or the ban on `CAST`. Those rules may tighten in a
later release; a predicate an earlier release accepted keeps opening and is
enforced as stored, and a row it cannot evaluate fails its own statement with the
evaluator's coded error.

A call's argument count is not such a rule: a call no signature accepts has no
value on any row (#1189). Engine builds before #1189 stored such CHECKs in format-4
catalogs, and format 4 is unreleased and has no upgrade path (#1152), so none is
carried forward. Opening a database whose catalog holds one fails with the coded
error and the constraint's name, and sends the operator to the build that stored it
rather than to a backup, which holds the same definition:

```text
Database 'shop' cannot be opened. CHECK constraint 'ck1' on table 'dbo.t' cannot be loaded: its persisted definition 'COALESCE() IS NULL' calls a function with arguments the function does not accept (CS01006: Function 'COALESCE' takes 1 or more arguments but was called with none. Accepted: COALESCE(value [, value ...]).). An engine build that did not check function arguments stored it, and this engine cannot evaluate it; open the database with that build and drop the constraint or replace it with a valid one.
```

That holds for the special form `COALESCE`. A stored call of a function name with an
argument count no built-in takes, `ABS(c, 1)`, can only come from an application's
overload of the name since E2 lets an application add one for another arity, so it is a
function the engine no longer registers: the database opens and writes fail with
`CS01009` (see [Builtin functions](#builtin-functions)).

## Ordering, output aliases and ordinals (#1024)

`ORDER BY` evaluates its keys before `OFFSET` and `LIMIT`, using the result of
grouping and `HAVING` when present. It accepts source columns, executable scalar
expressions, output aliases, and one-based select-list ordinals. These keys
compose with `DISTINCT`, pagination, the supported N-way joins, grouped
and ungrouped aggregates, and virtual system relations. Mixed ASC/DESC keys
compare from left to right. Each key accepts optional `NULLS FIRST` or `NULLS LAST`
after its ASC/DESC direction; explicit placement is independent of direction. When
omitted, NULL sorts first in ASC and last in DESC, preserving this dialect's default
(the opposite of PostgreSQL). Rows with
equal keys have no promised relative order; add a tie-breaking key for stable
paging. The same expression and column collation rules apply to aliases and
ordinals as to the values they reference.

**Name precedence:** an unqualified name in an ordering expression resolves to
an explicit SELECT alias first, case-insensitively, then to a source column.
This agrees with ISO/IEC 9075 select-list precedence for a bare ordering name
and extends the same rule consistently to names inside scalar expressions.
Thus `SELECT age AS years FROM t ORDER BY years` and `ORDER BY years + 1`
both use the projected age, even if `t` also has a `years` column.
`ORDER BY t.years` explicitly selects that source column. If several output
columns declare the same alias, using that alias is an ambiguity error; an
ordinal can identify the intended column. Alias references use the selected
expression's source scope: an alias never recursively refers to itself or to
another output alias. Grouped queries use the same rules and retain their
grouping validity checks. Aggregate call arguments still bind against their
input source, and inner SELECT bodies resolve names in their own query scope.

**Ordinal syntax:** a standalone integer literal selects an output column after
wildcard expansion: `ORDER BY 1` uses the first output, and `ORDER BY 2 DESC`
uses the second in descending order. A leading `+` directly before the numeric
literal and parentheses around a literal are accepted. Zero, negative values,
values beyond the output width, and non-integer numeric literal syntax
(including `1.0`, `.5`, and `1e0`) are precise planning errors, even on empty
input. They are never executed as constant keys. In contrast, an expression
such as `ORDER BY 1 + 1` is evaluated normally as a constant, not ordinal 2;
use another ordering key when such an expression leaves every row tied.
Parameters and numeric literals inside larger expressions are values, not
output positions. Ordinals are available only in ORDER BY. Standalone numeric
GROUP BY keys report `CK00001`; `GROUP BY 1 + 1` remains a constant grouping
expression. WHERE/HAVING predicates and LIMIT/OFFSET values retain their normal
expression meanings and never refer to select-list positions.

**NULL placement and composition (#1112):** aliases, ordinals, derived-table outputs,
combined set-operation results and aggregate input ordering share the same per-key
placement. `STRING_AGG(name, ',' ORDER BY x NULLS LAST)` and
`LISTAGG(name, ',') WITHIN GROUP (ORDER BY x NULLS LAST)` execute.
NULLS, FIRST and LAST stay unreserved identifiers; NULLS becomes a modifier only
when followed by FIRST/LAST after an ordering key. `ORDER BY a NULLS` and
`ORDER BY a NULLS MIDDLE` report CS00003. Rendering retains explicit placement,
including one equal to the default, and emits nothing for omitted placement.
NULLS remains quoted as an identifier; FIRST and LAST remain unquoted.
The grammar follows PostgreSQL `src/backend/parser/gram.y:8493-8495`; its explicit
placement binding is in `parse_clause.c:3508-3513`. Cohesion keeps its existing
default rather than PostgreSQL's direction default (`parse_clause.c:3503-3507`).
Supported correlated and uncorrelated scalar
subquery expressions, including projected scalar subquery aliases, and ordering
inside supported subqueries keep the same ordering rules. Alias names in
GROUP BY and HAVING remain outside the projection-alias binding scope; repeat
the source/grouping expression or aggregate there.

Phase 13's partial boundary is closed for the forms above: ungrouped aliases no
longer fail with an unknown-column error, and `ORDER BY 1 DESC` no longer returns
the unsorted scan. Execution and wire tests use deliberately scrambled values
whose required ordering differs from both insertion order and the actual scan.

## Renames and column defaults/nullability (#1122)

**ALTER COLUMN TYPE (#1447, SQ-A2).**
`ALTER TABLE t ALTER [COLUMN] c [SET DATA] TYPE type [COLLATE collation] [USING expression]`
executes inline under the table's exclusive lock. USING sees every column of the original row,
including qualified references to t; it cannot read another table or contain subqueries,
aggregates or window functions. Without USING the old column supplies the value. Both forms
require an assignment/implicit registry cast to the target; an explicit-only conversion such as
TEXT to INT requires `USING CAST(c AS INT)`. A bare string literal retains unknown-input
coercion. Target character length overflow refuses unless a written CAST permits truncation.
The declaration selects the target collation; omitted COLLATE uses the database default.
Nullability, keys and constraints remain. Integer identity targets retain the cursor, start and
increment. Domain/non-integer identity targets refuse. Start and allocated counter values must
fit the target integer range; unissued reservation tails are trimmed to it before publication
so the counter remains valid after reopen. Restart retains the existing reserved-value gap policy.

USING does not transform a default. Defaults must independently assignment-cast to the new type;
an incompatible default refuses even on an empty table (drop it before TYPE, then SET DEFAULT).
Numeric and Boolean defaults keep their original expression identity; unknown string literals
keep their resolved old identity. Immutable defaults validate eagerly and persist as canonical
literals, so repeated changes do not accumulate casts. A column without a default inherits the
target domain's default. Domain base modifiers use assignment overflow checks. Volatile defaults
retain per-insert evaluation and their original text and pinned calls; stable defaults also run
at INSERT time. TYPE recasts bind standard and application assignment casts, including DATE to
TIMESTAMP and TIMESTAMP to TIMESTAMPTZ. Immutable defaults freeze to literals and lose their pins.
Ordinary defaults keep their established evaluation behavior. CHAR defaults, including application
casts, keep assignment overflow/padding without a persisted explicit CAST. Immutable target-domain
defaults are checked even on an empty table. Existing CHECKs retain persisted-definition rules,
validate changed operand types against runtime comparison rules, and recheck converted rows.
LIKE keeps runtime text conversion for non-character operands. Domain and
NOT NULL failures also abort the whole operation. Every index is rebuilt; new uniqueness
collisions, including after a collation change, refuse. Incoming, outgoing and self foreign keys
must retain the engine's identical storage-type/collation compatibility and valid final keys.
Only FKs whose child or referenced columns include c are locked and revalidated; unrelated
FK neighbors remain writable and are not scanned. Affected adjacent tables are locked exclusively
during validation. Views depending on c refuse,
including a same-type USING expression; views reading only other columns retain their binding.

The shared #1265 rewrite copies live rows to a dense new owner and atomically publishes the
definition, indexes and catalog generation. A conversion/constraint failure or cancellation
before publication leaves the old table current. LockTimeout reports CS04044; stale writers
retry with CS04006. Read-only old snapshots keep the original definition/owner. Cancellation
after publication cannot undo the committed result. Full-definition storage/scale/domain/
collation changes use the same rewrite; decision 49a's validation-only facet path remains.
Full definitions refuse CS05013 if the resulting default differs or collation changes without
an explicit COLLATE, including when the type requires a rewrite. TYPE without USING retains
the heap and index trees for same-storage, same-scale, same-domain and same-collation changes
whose length/precision widen or become unbounded, except CHAR width changes that can repad values.
Narrowing rewrites with assignment semantics: excess trailing spaces trim to the target VARCHAR
length, while non-space overflow refuses. Each rewrite chunk
evaluates USING, domains and CHECKs before entering the database-wide physical apply gate.
Failed builds expose their abandoned owner through pending reclamations until its pages are freed.
Full-definition identity-type changes retain their refusal; use explicit TYPE for integer identity conversion.
Populated-table provisioning handoff and expanded crash/wire coverage follow in SQ-A3.
SQL format 27 introduced the per-column TYPE default-assignment flag in table extension 7; older files
refuse without an upgrade reader. Compiled-schema v8 is unchanged.
CS02064 names a missing assignment pathway for the value, USING result or default (42804).
Reference anchors: PostgreSQL tablecmds.c:14949-14998,15142-15199,15269-15304,15612-15624 and
utils/adt/varchar.c:620-648 in the local clone.

Each ALTER TABLE statement accepts one action. Rename/default/nullability forms are RENAME TO new_table, RENAME [COLUMN] old TO new,
and ALTER [COLUMN] c SET DEFAULT value, DROP DEFAULT, SET NOT NULL or DROP NOT NULL.
RENAME and TO remain contextual identifiers. Rename targets are unqualified and stay in the
same schema; SET SCHEMA, RENAME CONSTRAINT and action lists are excluded.
Existing names and read-only system relation names cannot be overwritten.
After the ownership check, a table name retained by a declared script object claim is refused with CS03302 naming
the script; historical targets are forward-only and cannot be silently rewritten.

Renames preserve object IDs, physical column ordinals, row versions and index trees. Index
column lists, primary keys, local and incoming/self-referencing FKs and CHECK definitions
change together in one catalog transaction. Generated constraint and index names stay unchanged.
CHECK rewriting parses stored text in the WHERE carrier at the persisted-definition ceiling,
including definitions created under a raised nesting limit. It replaces only column-reference
identifier tokens in its single-table scope;
strings, function names, type names and collations retain their meaning. Persisted DEFAULTs
have no column scope; SQL function bodies bind their own parameters and cannot contain table
subqueries. They therefore require no table-name rewrite. Renaming a table or a referenced column is refused while a persisted view reads it, naming the dependent view (#1124). INFORMATION_SCHEMA and COHESION_SCHEMA expose
the published names and facets immediately. A query using an old name reports an unknown object.

SET DEFAULT accepts the same literal and variable-free expression forms as
CREATE TABLE on this base. Function result types must satisfy INSERT assignment compatibility
(CS05013 naming the column, before any metadata change; PostgreSQL heap.c:3409). Literal
conversion and bounds are checked before publication; expression defaults also execute (#1121).
DROP DEFAULT re-inherits a domain default, and SET DEFAULT NULL persists an explicit NULL override
when the domain has a default (rewriteHandler.c:1305-1313; heap.c:2466-2475).
DROP DEFAULT affects later omitted-column writes. A NOT NULL
column without a default requires an explicit value. Neither action changes old rows: ADD
COLUMN freezes its missing value once, including NULL when it had no default. An old row still
reads that value after changing/dropping the insert default and after restart. Explicit stored
NULL stays NULL. A later column narrowing validates and binds the frozen literal under the new
type before publication, even with no live rows; an unconvertible literal refuses the change
with a coded error naming the column. This is PostgreSQL's separate missing attribute value
(src/backend/access/common/heaptuple.c:151-180) and column-default operation
(src/backend/commands/tablecmds.c:8201-8268).

SET NOT NULL scans current live rows under the Exclusive table lock, including synthesized
missing fields, and refuses a NULL naming the column without changing metadata. DROP NOT NULL
allows future NULL writes but refuses a primary-key column. SET/DROP DEFAULT and DROP NOT NULL
refuse identity columns with CS03025; PostgreSQL checks identity at tablecmds.c:7858-7863
and :8226-8233. A column already satisfying the requested facet still publishes an immutable
replacement, so queued DML retries consistently.

The target lock precedes incoming-child Exclusive locks sorted by ObjectId. Related DDL may
form a cycle; the existing lock manager aborts a victim and releases its locks so a retry succeeds.
DML bound before a same-name change fails with "changed while the statement was waiting";
a queued writer behind RENAME TO reports the old table does not exist. Lock-free SELECT planning
checks the DDL generation around table and index binding, including joins; an overlapping
publication discards the plan or binding error and retries before execution.
Name collisions report CS05013 naming the conflicting table or column; PK DROP NOT NULL
reports CS00003 naming the column. Every action retains
schema ownership checks and is refused with CS04003 inside an explicit transaction; that
failure leaves the transaction usable. SQL record-space format 17 and table extension 6 persist
the split. Earlier formats are refused; there is no compatibility reader or upgrade path.

## ADD COLUMN defaults, atomicity and MVCC (#1023)

`ALTER TABLE t ADD COLUMN extra INT DEFAULT 7` makes every preexisting row read
7 while preserving all original values. The backfill is **resolved at read time
from persisted catalog metadata**: a missing trailing field takes the added
column's frozen missing-value literal, captured at ADD COLUMN independently of its insert default. Existing row bytes and MVCC writer/deleter
stamps are unchanged. A stored NULL remains NULL; it is never mistaken for a
missing field. A later UPDATE writes the resulting full row normally.

Signed numeric defaults (`-1`, `- 1`, `-1.5e3`, `+7` and the BIGINT minimum)
and typed DATE/TIME/TIMESTAMP/TIMESTAMPTZ/INTERVAL/X/N literals use this same path.
A typed DEFAULT must match the column storage type; a mismatch fails planning before
mutation. `DEFAULT -'x'` parses and fails type validation at definition time.

A subsequent INSERT that omits the column uses its current insert default, which SET/DROP DEFAULT can change. Explicit NULL
is stored for nullable columns and rejected for NOT NULL columns. A nullable
addition with no default, or with `DEFAULT NULL`, yields NULL for old rows and
omitted inserts. On a populated table, NOT NULL requires a non-null default;
without one the statement fails. On an empty table, NOT NULL without a default
is allowed, and future inserts must supply a value. The emptiness check considers
current committed rows; historical row versions visible only to an older row
snapshot retain a missing NULL field when such an addition has no default.

Column defaults admit variable-free scalar expressions: arithmetic, unary signs, parentheses, function calls, niladic datetime forms and CAST. The top-level restricted grammar admits comparisons and IS [NOT] DISTINCT FROM (`DEFAULT 1 = 1` and `DEFAULT 1 IS DISTINCT FROM 2 NOT NULL` retain their expression and following constraint), and leaves NOT/AND/OR, truth tests, LIKE/IN and following column constraints unconsumed. Columns, parameters, aggregates and subqueries are excluded. Immutable expressions are evaluated at DDL time, including division-by-zero and conversion errors. Catalog open and table rebinds resolve DEFAULT trees without evaluating them; a changed function implementation fails only a write assigning its default. Stable and volatile defaults evaluate per assigned row, using the transaction-start clock. A call to a name outside the engine's catalog, executable profile and recognized-unsupported vocabulary fails
first, with `CS01016` and `Unknown function '<name>'.`,
and so does a call whose arguments its function does not accept, with `CS01006`
(`DEFAULT ABS(1, 2)`; see [Statement completeness](#statement-completeness-1068)),
and a parameter such as `:x` is a parse error. Defaults must
convert to the declared storage type and fit its bounds, including string length
and decimal precision/scale; invalid conversions and out-of-range defaults reject
before publication. `CREATE TABLE` applies the same check to every column
`DEFAULT` before it publishes the table (`INT DEFAULT 'abc'` fails the DDL, not
each later INSERT that omits the column). The assignment rules in
[Declared bounds on writes](#declared-bounds-on-writes-1418) apply: excess ASCII spaces
may be discarded, other excess characters report `CS02012`, and NUMERIC rounds
to its scale before checking precision (`CS02002`). The code still leads a
DEFAULT failure's message.
Approximate floating-point defaults use normal IEEE conversion rounding, but
nonfinite declaration results (literal, immutable expression, or stable ADD COLUMN missing value)
report CS02002; nonzero values that underflow to zero are rejected. Volatile DEFAULT
results follow ordinary per-row assignment, which permits NaN and infinities for DOUBLE.
Declaration validates storage type and bounds independently of NOT NULL. A NULL-producing
expression is legal on a NOT NULL column; an INSERT or UPDATE assigning it fails with
CS03024. ADD COLUMN accepts it on an empty table and rejects it against live rows.
`COLLATE` on an added string column controls its comparisons and indexes without
changing the bounded text, just as for CREATE TABLE.

Publication changes the complete table definition atomically after validation;
there is no row-rewrite phase to partially commit. A failed ADD COLUMN preserves
the previous schema, rows, and catalog state. The default survives catalog reopen,
so old-row reads recover the same values without any separate backfill journal.
Already bound plans keep their immutable table definition. Ordinary statements
planned after the change use the complete new definition, even in a transaction
whose older MVCC snapshot still selects earlier row versions; missing fields
resolve consistently from that statement's definition. Ordinary table schemas
are not pinned to BEGIN. The separate INFORMATION_SCHEMA snapshot contract below
is unchanged.

DDL remains self-committing and is rejected inside an explicit transaction with
`CS04003`; it cannot be undone by a later ROLLBACK. ALTER against a code-owned
table (`DatabaseObjectOwner.Schema`) remains refused unless performed by its
owning schema deployment.

## DROP COLUMN (#1241)

`ALTER TABLE t DROP COLUMN c` removes `c` from the table's definition: `SELECT *`,
INSERT without a column list, name resolution, the wire result header and the
system views no longer see it. It is refused, with nothing changed, for an unknown
column, a primary-key column, a column an index covers (drop the index first), a
column a foreign key, UNIQUE or CHECK constraint uses or references, and a table's
last column. A refusal for a constraint names it, a table-level CHECK that reads the
column included: "Column 'c' is referenced by constraint 'ck_t'. Drop the constraint
first."

The drop is **a catalog-only change**, as in PostgreSQL: the column's physical
position is marked dropped and no stored row is read or rewritten, so the statement
takes the same time on an empty table and a large one and holds up no other
table's writes. The dropped values stay in the rows that hold them, unreadable,
and the space returns as rows are updated and their old versions purged. Rows
written afterwards store a one-byte NULL in the dropped column's place when a
live column follows it, and nothing when none does, so each dropped column with
a live column after it lowers the largest row the table can store by one byte.
(PostgreSQL likewise keeps a null-bitmap bit for each dropped column in every
new row.) A crash at any point leaves either the
table as it was or the column dropped, with every value in its own column either
way. DDL stays self-committing (above).

Statements running beside the drop: a write or a join waits for it, and one planned
on the old definition then fails with "changed while the statement was waiting" and
can be retried. A single-table SELECT does not wait. Whichever definition it was
planned on, every value it returns is in its own column. One planned before the
drop still sees the dropped column, with the values of the rows its snapshot
sees; rows written after the drop are never visible to it, because a statement's
snapshot is taken before it is planned. Snapshots older than the drop keep
reading their rows, without the dropped column once a statement is planned on
the new definition.
Optional probe costing discards its estimates when the definition changes and retains
the original bound access path (#1740); it does not refuse that single-table SELECT.
Mutation and join execution keep their mandatory definition checks described above.

`ADD COLUMN` after a drop adds a new column, even under the dropped column's name:
existing rows read the new column's default (or NULL), never the dropped values.
`INFORMATION_SCHEMA.COLUMNS.ORDINAL_POSITION` numbers the live columns 1..n, so a
column behind a dropped one moves up by one, as the SQL standard specifies;
PostgreSQL instead reports its internal attribute number, with gaps. A table's
dropped positions are never reused; each one keeps five bytes in the table's
catalog definition, which is limited to one catalog record, so once a table has
dropped about 1,590 columns (fewer when its live columns are many or have long
names) ADD and DROP COLUMN on it are refused with a message that counts the
dropped columns. Only a new table starts without them: recreate the
table and copy its rows (`CREATE TABLE`, `INSERT ... SELECT`). PostgreSQL counts
dropped columns against its 1,600-column limit in the same way.

## Collation (#1025)

String comparisons resolve from the database default, overridden by a column's
declared `COLLATE`, overridden by an expression `COLLATE`. Nested expression
overrides resolve innermost first. A database without a configured default uses
`binary`. Column metadata and the database default survive restart. The default
is established when the database is created and is fixed for its lifetime;
opening a populated database under a different default is rejected, because
existing index keys are encoded through the collation they inherited. There is
no session override.

```sql
CREATE TABLE people (name TEXT COLLATE case_insensitive UNIQUE);
SELECT name FROM people WHERE name = 'alice';
SELECT name FROM people WHERE name = 'Alice' COLLATE binary;
```

`binary` compares Unicode code points using unchanged UTF-8 bytes.
`case_insensitive` applies invariant Unicode simple case folding, then compares
UTF-8 bytes. `case_accent_insensitive` canonically decomposes Unicode text,
removes combining marks, applies the same fold, then compares UTF-8 bytes.
These transforms are culture-independent and do not use `CompareInfo`.
Collation changes comparison rules, never the stored or returned spelling.

`WHERE` comparisons, `LIKE`, `ORDER BY`, `GROUP BY`, `DISTINCT`, and `UNIQUE`
use the effective collation. Grouping and distinct keys use matching equality
and hash rules. A case-insensitive unique column rejects a second spelling that
folds to an existing key; its enforcing index uses the same transform. A seek
is eligible only when the predicate and index use matching, index-backed
collations. An explicit override that differs from the index uses a scan.

Compatibility `invariant` is **not index-backed**: its linguistic comparison
cannot be represented by the supported byte transforms. Predicates using it
scan even when another collation has an index on the column; declaring an index
or index-backed constraint under `invariant` is rejected.

Unknown names, culture-aware collations, user-defined `CREATE COLLATION`,
session overrides, and collation-aware full-text indexes report `CK00001` or
an explicit execution error. Full linguistic collation is deferred to #1026.
SQL defaults do not apply to Documents, Graph, Key-Value, or Blob comparisons.

## Joins (#1019, #1525)

FROM is a comma-separated list of join trees. Each item starts with a stored table,
registered INFORMATION_SCHEMA/COHESION_SCHEMA relation or aliased derived query, followed by any number of INNER
(including bare JOIN), LEFT [OUTER], RIGHT [OUTER], FULL [OUTER], or CROSS joins. INNER, LEFT, RIGHT and FULL require exactly one
ON predicate or USING (column [, column ...]); CROSS accepts neither. Missing ON/USING
retains CK00001; malformed joins and CROSS with a condition fail
with an error and never execute as a product. NATURAL, LATERAL and qualified wildcards
such as t.* keep CK00001. Derived comma inputs, FROM t, (SELECT ...) d, execute.

JOIN binds more tightly than comma: in FROM a, b JOIN c ON ..., ON can see b and c,
but cannot see a. It also cannot reference a later input. Unknown/hidden qualifiers
and ambiguous columns report located CS01015 before rows are read; duplicate aliases
report CS01019. An explicit alias hides the base name in every join form and in DML.
New CHECK declarations likewise reject a qualifier that does not name the constrained table
or its schema with located CS01015, for CREATE TABLE, ADD CONSTRAINT and ADD COLUMN.
Bare column names, table.column and schema.table.column are valid. Previously persisted
CHECKs keep their original binding behavior when reopened. SqlQualifierConformanceTests
covers stored-table query clauses and the CHECK declaration/reopen contract (#1191).
PostgreSQL anchors: doc/src/sgml/queries.sgml:217-227 and
src/backend/parser/parse_clause.c:370-386 (the ON scope contains only its join inputs).

Unparenthesized joins associate left to right. Parenthesized groups preserve that explicit
association and expose their constituent namespaces. An alias on a group hides those namespaces
and lowers to an aliased SELECT * derived relation, including optional complete column-alias lists and the existing 32-level derived-query binding limit.
Groups may begin with an aliased derived query for INNER, LEFT, RIGHT, FULL or CROSS JOIN,
including nested groups. Their canonical text remains parseable when group aliases introduce
derived SELECTs, so CREATE VIEW and reopening the view preserve the same association.
RIGHT swaps physical children and executes LEFT null extension; its written left subtree
becomes nullable, while its written right side is preserved. Written SELECT * ordinals and
qualified references remain left-then-right. EXPLAIN identifies "normalized RIGHT to LEFT"
and the chosen hash, index or buffered path. Grouped or RIGHT trees keep all predicates at
their written level; safe mandatory hash keys still qualify, and a two-leaf RIGHT can seek
into its written left input. No first-pair reorder crosses an outer boundary.

FULL uses a hash join with both written inputs preserved. It requires a mandatory equality
between the two inputs and a complete ON condition proven safe by the hash rules above;
CS05070 refuses unsupported physical conditions before either input is read. Inequality-only,
OR-only, CHAR-only keys, unproven throwing/lazy/application expressions, subqueries and
unvalidated derived text are excluded. Safe merged USING COALESCE keys and widening casts
can join later inputs. The written right subtree builds even when the left is empty. Every
build row, including NULL keys and duplicates, has its own match flag; only TRUE ON marks it.
Unmatched rows from both sides receive NULL for all columns of the opposite subtree.
WHERE filters after null extension. EXPLAIN JoinStep names FullHashJoin and unmatched=both;
ANALYZE counts tested candidate pairs, excluding emitted unmatched rows. All retained hash
rows and match flags share StatementMemoryBudget (default 64 MiB), including nested builds;
CS05067 refuses exhaustion without spill. Reservations unwind on success, failure and
cancellation. Joined DML also charges retained outer/intermediate inputs and associations as
described in the statement-memory contract below. General SELECT result materialization is outside that grant.
PostgreSQL anchors: optimizer/path/joinrels.c:1157-1180, executor/nodeHashjoin.c:578-592,634-679,729-756.

Ordinary flat joins execute left-deep in written order. Only the first INNER pair
may swap sides for an index. Each later step may seek into the input it adds; a LEFT
step always drives from its preserved left input. Mandatory equalities must bind an
index-key prefix under the existing type and effective-collation rules. The full ON
remains a residual predicate. Otherwise the added input buffers once. System inputs
take no object lock and have no index path; all inputs share one statement catalog
image and MVCC snapshot. EXPLAIN shows each step's kind and access, including scans.

Proven non-throwing single-input WHERE conjuncts can select equality/range seeks and filter
before joining (#1521). The first INNER pair prefers an input restricted by a full
unique seek; EXPLAIN shows that driving IndexSeek and its bounds. Compatible mandatory
equijoin equalities propagate constants across INNER inputs. LEFT keeps nullable-side
WHERE predicates after null extension; nullable-side ON predicates may filter before it.
Only predicates whose entire evaluation is proven unable to raise an error may move:
comparisons, IS [NOT] NULL, BETWEEN, IN and LIKE over columns, constants and supplied
parameters with compatible operand types and fixed collations (index-backed for text).
LIKE also requires a validated constant/parameter pattern and escape. Text predicates
over derived outputs remain residual because those values lack stored rows' valid-Unicode
guarantee; unknown column types likewise provide no safety proof. Standard identity
and widening numeric casts qualify only without target modifiers; narrowing,
text-to-number, domain and application casts remain at their original level.
Text LENGTH/CHAR_LENGTH/CHARACTER_LENGTH and the six captured transaction-clock forms
are the only admitted calls. Immutable/Stable metadata alone does not prove safety.
Arithmetic that may overflow or divide by zero, all other unproven calls or scalar
forms, row-dependent LIKE patterns/escapes, subqueries, outer references and deferred
failures remain at their original join level. For example, an invalid CAST or zero
divisor on a row discarded by the join or an earlier WHERE/ON term still raises no error.
Complete ON/WHERE residuals retain
NULL, duplicate-key and mixed-numeric comparison semantics, including scan fallback
when a planned index is detached or replaced.

Only TRUE matches ON or the implicit USING equalities; FALSE and UNKNOWN do not.
LEFT supplies NULL for every right column when no candidate matches; RIGHT supplies NULL for every column of its written left subtree. A predicate in
ON preserves unmatched left rows; the same predicate in WHERE can remove them.
COUNT(*) counts the extended row while COUNT(right.column) ignores its NULL. All
join forms compose with grouping/HAVING, DISTINCT, ordering and LIMIT/OFFSET.
CROSS and comma multiply cardinalities; either empty input yields a typed empty
product. Row order without ORDER BY is unspecified.

USING requires each listed name exactly once on each input; missing, repeated or
ambiguous names fail binding with CS01015. Its equality uses ordinary ON collation
precedence, including column overrides. The merged output uses PostgreSQL's common-type
selection through implicit casts within the same type category. INT/DECIMAL produces DECIMAL
and INT/BIGINT produces BIGINT in either order, including arithmetic and function overloads.
LEFT preserves the coerced left value and its nullability; RIGHT preserves the coerced right value and its nullability. FULL uses COALESCE of both coerced inputs (left spelling wins on a match) and the equality collation for merged text. Its merged column is nonnullable only when both original inputs were nonnullable; both qualified sides become nullable. See PostgreSQL parse_clause.c:1739-1756. INNER prefers an unchanged input
of the common type when available. Length, precision and scale remain only when both input
types and modifiers agree; CHAR(3)/VARCHAR(10) clears the length. Qualified references retain
their own types and modifiers; right qualified references become nullable under LEFT and left qualified references become nullable under RIGHT.
Pairs with no common type fail planning with CS01015, even on empty inputs.
Identical domains retain their domain identity; differing domains or a domain/base pair
resolve through their base types (PostgreSQL parse_coerce.c:1360-1387).
Unqualified merged names are unambiguous; qualified left.id and right.id still bind.
SELECT * orders merged columns once, in USING order, then remaining left columns,
then remaining right columns. A later USING applies this rule to its accumulated
left output; comma items concatenate their outputs. ORDER BY aliases after `*` and
numeric ordinals count these visible output columns. PostgreSQL performs the same
USING name validation and output ordering at parse_clause.c:1322-1399,1460-1510,
common-type synthesis at parse_clause.c:1676-1733 and parse_coerce.c:1393-1437,
and modifier clearing at parse_coerce.c:1648-1678.

In-process pass-through metadata marks every null-supplying LEFT/RIGHT/FULL column nullable.
The existing wire header still carries name, type and length without nullability.
Binary delivery copies only selected retained outer/buffered-inner cells; the final
index inner is freshly decoded. Earlier indexed inputs retained by later steps are
copied on delivery, as are unmatched LEFT outer values used in multiple results.

Capability preflight skips advertised clauses and reports the first unadvertised
one. Thus USING cannot mask a later NATURAL, WINDOW or OVER. MERGE INTO ... USING
binds and explains; DELETE ... USING executes; CREATE INDEX ... USING remains a syntax error.

## MERGE syntax, binding and execution (#1715, SQ-M1/SQ-M2/SQ-M3)

```sql
MERGE INTO target [AS a] USING source [AS b] ON condition
WHEN MATCHED [AND condition] THEN UPDATE SET column = expression [, ...]
-- MATCHED also accepts DELETE or DO NOTHING
WHEN NOT MATCHED [BY TARGET] [AND condition] THEN INSERT [(column [, ...])]
    VALUES (expression [, ...])
-- NOT MATCHED also accepts INSERT DEFAULT VALUES or DO NOTHING
WHEN NOT MATCHED BY SOURCE [AND condition] THEN DELETE
-- BY SOURCE also accepts UPDATE SET ... or DO NOTHING
[RETURNING expression [AS name] [, ...]];
```

At least one WHEN is required. Clauses retain written order; the first qualifying clause
of a candidate's match kind selects its action. An unconditional clause, including DO
NOTHING, makes later clauses of the same kind unreachable (CS05071, at the complete later
WHEN clause). BY TARGET is the default
unmatched direction. INSERT takes one VALUES row or DEFAULT VALUES; whole-item DEFAULT and
existing identity policies apply. Target names must be unique and match the VALUES width.
Bare target/source aliases are also accepted. Sources use existing stored/system/view,
derived-query and joined-relation binding. Plain WITH-prefixed MERGE shares SELECT CTE
binding and reachable-only materialization (#1719). WITH RECURSIVE before MERGE is
refused at the RECURSIVE token with CK00001, including acyclic definitions, following
PostgreSQL `src/backend/parser/parse_merge.c:124-129`.
The UPDATE FROM source forms also bind in MERGE USING and survive canonical rendering,
including views, aliased derived queries and CROSS JOIN sources. A trailing source CROSS
JOIN leaves the following ON for MERGE. VALUES relations, table functions, LATERAL,
NATURAL JOIN and aliasless derived queries retain UPDATE FROM's refusal codes.

ON and RETURNING see both sides. MATCHED predicates/values see both, BY TARGET sees only
source, and BY SOURCE sees only target. Aliases hide base names; destinations always name
target columns. Missing/ambiguous names, duplicate destinations, missing parameters,
non-Boolean predicates, aggregates and incompatible assignment types fail during binding,
including empty inputs. Assignment compatibility follows INSERT SELECT's implicit conversion
families; row-dependent conversion, bounds, nullability and domain checks belong to execution.
ON, WHEN and action values admit existing read subqueries; RETURNING subqueries retain
CK00001. Clause conditions and values run lazily, including subquery branches, only for
rows that reach that clause; values run only after its condition holds.
Source queries keep existing correlation/capability rules.

RETURNING supports merge_action(), a contextual zero-argument text expression available only
in this projection (CS01053 elsewhere; CS01006 for arguments/modifiers), including nested
scalar expressions. It is separate from the standalone executable-function profile. Bare *
expands target columns then visible source columns, preserving source USING merges;
qualified wildcards retain CK00001. Source metadata includes absent-side nullability,
including merged USING outputs.
RETURNING emits one row per applied action and none for DO NOTHING. merge_action() yields
INSERT, UPDATE or DELETE. Target columns contain the new row for INSERT/UPDATE and the old
row for DELETE; source columns are NULL for BY SOURCE candidates.

EXPLAIN shows Merge, the association tree, ordered MergeAction children (match kind,
operation, condition presence and column counts), and RETURNING columns. Physical columns
remain target then source. Ignoring DO NOTHING clauses, MATCHED-only uses INNER; BY SOURCE
uses target LEFT source; BY TARGET uses target RIGHT source normalized physically to source
LEFT target; both unmatched directions use FULL. FULL requires #1525's safe mandatory hash
equality and complete non-throwing ON proof; otherwise planning, including EXPLAIN, refuses
with CS05070, as for source-internal FULL joins. LEFT and RIGHT still accept hashless ON
predicates. WHEN filters are not pushed into ON because they select actions rather
than classify matches.

SQ-M2 executes all supported actions within one statement rollback bracket, including result
preparation before publication. Source association cursors drain before writes and row-lock
waits. Physical identity and explicit side-presence markers classify NULL-valued candidates.
A second modifying action on one physical target fails with CS05036, including DELETE;
extra matches that select DO NOTHING do not count. Competing INSERT keys use ordinary
uniqueness enforcement. Identity allocation, defaults, coercion, domain/CHECK validation,
referential actions and lock ordering reuse existing DML rules. Hash builds share the
statement memory grant and cancellation unwinds reservations and statement locks.
Plain EXPLAIN succeeds without writes; EXPLAIN ANALYZE stays SELECT-only.
SQ-M3 locks the latest version of a concurrently changed modifying target under ReadCommitted
and reevaluates the original ON predicate and ordered WHEN clauses against it, retaining the
original source. A broken match produces both unmatched directions; a deleted MATCHED target
produces BY TARGET only. BY SOURCE never becomes MATCHED and a concurrently deleted BY SOURCE
row disappears. Snapshot retains a retryable serialization refusal with CS04064. Competing INSERTs
use ordinary uniqueness enforcement. Independent action subqueries remain cached per statement;
scalar assignments reevaluate on a target recheck.

Foreign keys see the composed final INSERT/UPDATE/DELETE rows, including overlapping CASCADE,
SET NULL and SET DEFAULT effects. A child moved away from a deleted parent survives. NO ACTION
allows a replacement parent key within the statement; RESTRICT refuses substitution. Children
moved to a deleted parent and then cascaded away retain locks on their old stored
parents through commit or rollback, so a concurrent parent delete waits before reading children.
Direct CHECK/domain and unique proposal failures remain errors even when a cascade would remove the
proposed row. RETURNING and affected counts describe direct actions before referential effects.
An UPDATE removed by a cascade still validates children of its old stored parent key against
the final state; surviving old-key children refuse the whole statement with CS03039. Immediate
direct-proposal uniqueness takes precedence over final-state foreign-key failures and
NOT NULL, CHECK, domain or identity failures on SET NULL/DEFAULT rows, following PostgreSQL
executor/nodeModifyTable.c:2371-2383 and utils/adt/ri_triggers.c:887,923-955. A retained failure
performs no further parent-row locking, so a stale matched parent cannot mask the statement error.
CS04064 identifies actual target-version conflicts. Cascade snapshot crosschecks and matched-parent
version checks retain plain DML's uncoded transaction-abort contract, as do host-ended transactions
and abandoned lock owners. The
[review regression corpus](../../Assimalign.Cohesion.Database.Sql/tests/SqlMergeReviewRegressionTests.cs)
pins these conditions and unchanged tables on refusal.
The [engine recheck corpus](../../Assimalign.Cohesion.Database.Sql/tests/SqlMergeConcurrencyTests.cs),
[final-state corpus](../../Assimalign.Cohesion.Database.Sql/tests/SqlMergeConstraintTests.cs),
[InMemory/TCP corpus](../../Assimalign.Cohesion.Database.Sql.Client/tests/SqlMergeWireTests.cs) and
[wire Snapshot corpus](../../Assimalign.Cohesion.Database.Sql.Client/tests/SqlMergeConcurrencyWireTests.cs)
exercise this contract. Wire BEGIN keeps its default Snapshot isolation. No disk/protocol/result-frame
changes. Repeated modification and target rechecks follow PostgreSQL
executor/nodeModifyTable.c:3521-3551,3557-3744; NO ACTION substitution follows
utils/adt/ri_triggers.c:944-959.
Namespace/order rules follow PostgreSQL parser/parse_merge.c:52-99,138-178,247-285;
join preservation follows optimizer/prep/prepjointree.c:222-246 and FULL eligibility follows
optimizer/path/joinrels.c:1165-1179 in the local reference clone.

| Code | MERGE condition |
|---|---|
| CS01053 | merge_action() outside MERGE RETURNING |
| CS05036 | A second modifying action selects the same physical target |
| CS05070 | FULL association lacks a safe mandatory hash equality or complete ON safety proof |
| CS05071 | WHEN follows an unconditional clause of the same match kind |

## Value comparison (#1029)

Predicates (`=`, `<>`, `<`, `<=`, `>`, `>=`, `BETWEEN`, `IN`, and join
conditions), `ORDER BY`, `DISTINCT`, `GROUP BY`, and `MIN`/`MAX` share one
non-null value comparator. NULL retains SQL three-valued predicate semantics;
grouping combines NULL keys. Ordering places NULL first ascending and last descending
by default; per-key NULLS FIRST/LAST overrides that placement independently of the
non-NULL value order.

- **Binary:** unsigned byte-by-byte lexicographic order. The first differing
  byte decides; a proper prefix sorts before its extension. Equality requires
  identical content and length, including empty values; there is no padding.
- **Numerics:** signed integers and Decimal compare exactly. Float32 promotes
  exactly to Float64; floating pairs preserve their represented IEEE values
  across the entire finite range, including subnormals and adjacent doubles.
  A mixed exact/approximate comparison compares their exact represented values
  without rounding either operand. Thus `1d = 1m`, but `0.1d > 0.1m` (host
  notation), and the next double above `1d` is greater than `1m`.
  The previous decimal-first grouping conversion is **not** acceptable for
  equality: it collapsed adjacent doubles and underflowed tiny values. Refining
  those rounded mixed equalities is necessary for a transitive total order;
  existing exact equalities and sign/range ordering are retained. Numeric hashes
  agree with this equality, including across runtime numeric types.
- **IEEE extension:** all NaNs, regardless of sign or payload, compare equal
  to each other and less than every other non-null numeric value. In particular,
  `NaN = NaN` is TRUE and `NaN <> NaN` is FALSE. The numeric order is
  `NaN < -infinity < finite values < +infinity`; like-signed infinities are
  equal. Positive and negative zero are equal in predicates and form one
  DISTINCT/grouping class. Under ASC NULLS LAST the order is NaN, -infinity,
  finite values, +infinity, NULL; DESC NULLS FIRST reverses it. NaN is a value, not NULL. These deliberate rules
  apply to Float32, Float64, and mixed numeric comparisons.
- **Temporal values:** `TIMESTAMP` compares wall-clock ticks and ignores the
  host `DateTimeKind`; `TIMESTAMPTZ` compares instants and ignores the offset.
  See "Temporal value identity" below.
- **Other values:** strings use the effective collation; Boolean orders FALSE
  before TRUE. Supported same-type comparable values retain their runtime
  ordering. Incompatible type families raise a query error.

This implements an ISO/IEC 9075 subset for binary strings and approximate
numerics, with an explicit NaN/infinity extension rather than an SQL-standard
NaN claim. Bind binary/IEEE values as parameters or read stored columns; SQL
fractional/exponent literals still follow the exact Decimal literal contract,
and there are no NaN/infinity literals. This changes comparison, not arithmetic,
SUM/AVG accumulation, CAST, storage coercion, or physical key identity.
Floating range predicates and signed-zero equality predicates do not supply
index seek bounds: physical index ordering places NaN last and distinguishes
signed zeros. They are evaluated on scanned rows or as residuals of another
safe index predicate.
Nonzero floating equality may still seek; joins retain their conservative
approximate-numeric scan policy. Mapper restrictions and key exclusions remain
deferred with #1007/#1008.

## Timestamp normalization (#1110)

TIMESTAMP is a zone-less wall clock stored as DateTime with Kind Unspecified.
TIMESTAMPTZ (TIMESTAMP WITH TIME ZONE) is an instant stored as DateTimeOffset;
a literal without an offset means +00:00, and an explicit offset is retained.
There is no session time zone. Wherever TIMESTAMP meets TIMESTAMPTZ in a cast,
comparison or assignment, TIMESTAMP is read as UTC. Converting TIMESTAMPTZ to
TIMESTAMP, DATE or TIME uses its UTC wall clock; the resulting TIMESTAMP has
Kind Unspecified. INSERT and UPDATE apply the same rule to literals and parameters.
For example, assigning `TIMESTAMPTZ '2024-01-01 12:00:00+02:00'` to a TIMESTAMP
column stores 10:00 Unspecified. The reverse assignment uses offset +00:00.
A plain string compared to a temporal value remains a type error; use
`DATE '2024-01-01'` for a date comparison. CAST, datetime functions and expression
defaults use this normalization rule; key identity is specified below.

## Temporal value identity (#1099)

`TIMESTAMP` and `TIMESTAMPTZ` each have exactly one normalization rule.
Comparison (predicates, join conditions, `ORDER BY`, `DISTINCT`, `GROUP BY`,
`MIN`/`MAX`), index keys, seek bounds, and `UNIQUE`/`PRIMARY KEY` enforcement
with its key locks all follow it. A secondary-index seek, a primary-key seek and
a scan therefore return the same rows for every predicate.

| Type | Identity (compared, ordered and keyed by) | Not part of identity | Stored and returned |
|---|---|---|---|
| `TIMESTAMP` (`DateTime`) | Wall-clock ticks | `DateTimeKind` (`Unspecified`, `Utc`, `Local`) | Kind Unspecified |
| `TIMESTAMPTZ` (`DateTimeOffset`) | Instant (UTC ticks) | Offset | The written offset |

- `TIMESTAMP` is a timestamp without time zone. Values with equal ticks are equal
  whatever their kind, and no time-zone conversion happens: a `Local` 12:00, a
  `Utc` 12:00 and an `Unspecified` 12:00 are one value, ordered by those ticks.
  A `UNIQUE` or `PRIMARY KEY` column rejects equal ticks that differ only in
  kind.
- `TIMESTAMPTZ` compares by instant, as ISO/IEC 9075 specifies for `TIMESTAMP
  WITH TIME ZONE`: `12:00+00:00`, `15:00+03:00` and `06:30-05:30` are one value,
  and values order by instant, never by local wall-clock time. A `UNIQUE` or
  `PRIMARY KEY` column rejects the same instant at another offset.
- Rows return TIMESTAMP with Kind Unspecified and preserve the written TIMESTAMPTZ offset. Index keys hold only the identity, so updating a
  row to an equal value in another kind or offset does not collide with itself.
- `=`, `<`, `<=`, `>`, `>=` and `BETWEEN` on either type seek an applicable
  index (alone, or after an equality prefix of a composite key), and two-table
  joins on equal temporal types can use index assistance.
- Parameters keep their kind and offset across the wire protocol, so these
  rules hold identically in-process and through `SqlDatabaseServer` and
  `Sql.Client`.

**Indexes written before this rule.** The rule changes the on-disk key format:
data-storage format 3 (written through 10.0.0-preview.1) stored the kind and
the offset inside index keys, and format 4 does not. Format 5 (#1194) keeps
format 4's rows and key encoding but stores index trees in B-tree page format 2,
whose entries are ordered by key, entry reference and writer, so a format-4
database is refused too. Format 6 (#1241) keeps format 5's rows and index trees
but records each table's dropped-column positions in the catalog and decodes
rows through them, so a format-5 database is refused too. The engine has no
upgrade path (#1152). It refuses to
open a database on any format but 20 — older or newer — with an error that names
the database, the format it found and the format it supports. The check reads
only the database's catalog and runs before the engine opens the data files, so
a refused open never touches them. A cleanly
closed database is left byte-identical. A crashed one keeps its journals: the
catalog files get only the storage layer's own crash recovery, which does not
depend on the format. To move an older database to the current engine, export
its data with the engine that wrote it (which can still open it), drop the
database (`DropDatabaseAsync`), create it again and reload the data. A database
whose creation was interrupted before its format was recorded reads as format 1
and needs only the drop and create. Upgrading databases across format versions
is tracked by #1152. New databases use format 28, which adds internal standalone sequence records and retains TYPE default assignment coercion; earlier files are refused. Recoverable table rewrite and dropped-ordinal compaction (#1265) reuse its existing records and TRUNCATE publication contract. SqlDatabase.RewriteTableAsync is a C# maintenance operation on the provisioning worker; it adds no SQL statement. Read-only snapshots retain the original layout until they end. Writers bound to a retired table, including old snapshot transactions reading it inside DML, abort retryably with CS04006; retry the entire transaction. FK probes against a replacement their probe snapshot cannot see also abort with CS04006, including after a parent rename. Empty DML input, MATCH SIMPLE null keys and self-keys satisfied by the same input do not probe a parent and do not trigger this refusal. Read-committed ON CONFLICT checks its refreshed probe snapshot after arbitration. Format 26 fenced changed temporal DEFAULT and frozen missing-value text (#1422). Format 25 admits distinct predicates, Boolean tests and LIKE ESCAPE in persisted expressions and views; format 24 admits explicit NULL placement in
stored view bodies; earlier formats are refused at open, with no upgrade reader.

From format 4 on, the catalog stores the format marker in a record that engines
before format 4 do not recognize, so those engines refuse to open a format-4
database instead of writing index keys that this rule's seeks and `UNIQUE`
checks would miss.

This closes the key-identity gap behind the mapper's `COHMAP003` key exclusion;
the mapper's own review stays with #1008. No external conformance suite applies;
the rule is the declared dialect contract above.

## Arithmetic and numeric faults (#1069)

`+`, `-`, `*`, `/`, and `%` propagate NULL. For non-null operands the operand
families choose the computation:

| Operands | Computation | Result type |
|---|---|---|
| Both exact signed integers (`TINYINT`, `SMALLINT`, `INT`, `BIGINT`, integer literals) | 64-bit signed, overflow-checked | `Int64` |
| Any `DECIMAL`, `REAL`, or `DOUBLE` operand | Both operands promoted to `System.Decimal` | `Decimal` |

Integer `/` truncates toward zero and `%` takes the dividend's sign
(`-7 / 2 = -3`, `-7 % 3 = -1`). Narrower integers compute in BIGINT, so
`2147483647 + 1` is the exact BIGINT `2147483648`, not a fault. `x % -1` is 0
for every integer, including the BIGINT minimum. Approximate operands enter
Decimal arithmetic through the runtime floating-point conversion, as before
(about 7 significant digits for REAL and 15 for DOUBLE). A nonzero REAL or
DOUBLE smaller in magnitude than Decimal's smallest step (1e-28) converts to 0
like any other digit the conversion drops, so `@tiny + 1` is `1`. As a divisor
it has no Decimal quotient and reports `CS02002`, not a division by zero.

An arithmetic fault fails the statement. No operator result wraps, saturates, or
becomes NULL. A sign applied to a value that is not a number is a fault of the
same kind, with its own code:

| Code | ISO SQLSTATE | Raised when |
|---|---|---|
| `CS02001` | 22012, division by zero | The right operand of `/` or `%` is zero: integer, decimal, or approximate (either signed zero), whether it is a literal, column, computed value, or bound parameter. |
| `CS02002` | 22003, numeric value out of range | A BIGINT result leaves -9223372036854775808..9223372036854775807, including `-x`, `ABS(x)`, and `x / -1` for the minimum; a Decimal result exceeds `System.Decimal`; a REAL or DOUBLE operand is NaN, infinite, or beyond Decimal's range, or is a nonzero divisor below Decimal's smallest step; a numeric literal does not fit its type (see Literals); `SUM` or `AVG` overflows or meets a non-finite value; a value does not fit the common type of `CASE`/`COALESCE` branches; a value does not fit the integer or `DECIMAL` column it is stored into; or a NUMERIC/DECIMAL value exceeds its declared precision or cannot be represented exactly as `System.Decimal` in assignment, DEFAULT or CAST, after rounding to the declared scale when there is one. |
| `CS02003` | 42804, datatype mismatch | The operand of unary `+` or `-` is not a number: `+'abc'`, `-TRUE`, `+name` on a string column. Raised while planning when the operand's type is already known or the operand is a parameter bound to a non-number, so it fails over an empty table too, and while evaluating when only a row's value reveals it. See Signs under [Statement completeness](#statement-completeness-1068). |

The code leads the message, for example
`CS02001: Division by zero: the right operand of '/' is zero.` In process the
statement throws a `DatabaseException` with that message. Over the wire the server
reports `ExecutionFailure` with the same text, and the SQL client raises
`SqlClientException` with `Kind = ExecutionFailure` and `ConnectionUsable = true`.
An evaluation fault never terminates a session. That covers the coded faults above
and uncoded query errors raised while rows are evaluated, such as `ORDER BY` keys
the value order cannot compare (`Cannot compare values of types ...`).

- **Atomicity:** every expression of a DML statement, including CHECK
  constraints, is evaluated before its first write, so a faulting `INSERT`,
  `UPDATE`, `DELETE`, or `INSERT ... SELECT` applies nothing, not even rows
  evaluated before the faulting one. `ALTER TABLE ... ADD CONSTRAINT ... CHECK`
  that faults on an existing row publishes no constraint.
- **Transactions:** an auto-commit statement rolls back. Inside `BEGIN`, the fault
  follows the rule for any failed statement: the transaction stays active with its
  earlier statements' work, and both `COMMIT` and `ROLLBACK` remain available.
- **Evaluation point:** faults are raised where an expression is evaluated. A
  predicate over an empty table evaluates nothing and raises nothing. The one
  exception is `CS02003` for a sign whose operand type the plan already knows,
  or whose operand is a parameter bound to a non-number, which is raised while
  planning. A constant
  comparand that faults during index-seek planning is not used for the seek, and
  the scan faults on the first row that evaluates it. `LIMIT` and `OFFSET`
  expressions are evaluated during planning and fault there.
- **Short-circuit:** `AND` and `OR` evaluate their left operand first and skip the
  right one when the left decides the result (`FALSE AND x`, `TRUE OR x`). A guard
  such as `d <> 0 AND x / d > 1` therefore never divides by zero; a guard written
  after the division, or a left operand that is NULL, does not protect it. A chain
  of terms (`a AND b AND c ...`, one node of any length since #1151) runs its terms
  first to last and stops at the first that decides the result, which is the order
  and the stopping point of the nested operators it replaces, so a guard anywhere
  before a division protects it. `CASE`
  evaluates only the selected branch, `COALESCE` stops at the first non-NULL
  argument, and `IN` stops at the first list value that matches. Every other
  operator and function evaluates all of its operands.
- **Store assignment:** a value an integer or `DECIMAL` column cannot hold, such as
  `UPDATE t SET i = i + 1` on an `INT` at 2147483647, reports `CS02002`
  (`value '2147483648' does not fit column 'i' of type Int32.`). A value that is not
  a number at all keeps the uncoded `Cannot convert value ...` error, and a REAL
  column stores a finite DOUBLE beyond REAL's range as an infinity.
- **CAST:** arithmetic inside a CAST operand keeps its own code
  (`CAST(1 / 0 AS INT)` reports `CS02001`). NUMERIC modifier overflow, or a built-in
  text-to-NUMERIC result that cannot fit `System.Decimal` exactly after any declared-scale
  rounding, reports `CS02002`. Other source conversion failures keep
  their `CAST ... failed` message under the conversion contract below.

## Pagination (#1107)

After ORDER BY, SELECT accepts PostgreSQL `LIMIT n OFFSET m` or `OFFSET m LIMIT n`,
or ISO `[OFFSET m [ROW|ROWS]] FETCH {FIRST|NEXT} [n] {ROW|ROWS} ONLY`.
OFFSET alone accepts an optional ROW or ROWS suffix. FETCH defaults to one row when
its count is omitted; FIRST and NEXT, and ROW and ROWS, have identical semantics.
FIRST remains contextual and unreserved.

FETCH counts follow PostgreSQL's primary-expression (`c_expr`) or signed numeric
constant grammar: bare `1+1` receives CS00003, while `(1+1)` is accepted and renders
as `(1 + 1)`. Names, parameters, function calls and CASE expressions are primary
expressions; signs on other operands and infix operators require parentheses.

The D1 SQL Server lexical aliases are `SELECT [DISTINCT] TOP (expression) ...` and
`SELECT [DISTINCT] TOP n ...`. Unparenthesized TOP consumes exactly one integer
literal or parameter token; a signed count or other expression requires parentheses.
TOP applies after ordering, grouping/HAVING and DISTINCT, as every row-limit spelling does.
Without ORDER BY, the count still applies but the choice of rows has no ordering guarantee.

Each SELECT accepts one limit spelling and one OFFSET. LIMIT and OFFSET can appear in
either order, including PostgreSQL FETCH before OFFSET; rendering uses ISO OFFSET before FETCH. LIMIT with FETCH, TOP with LIMIT/OFFSET/FETCH,
duplicate limits/offsets, or FETCH without ONLY receive CS00003. PERCENT, WITH TIES
and INSERT/UPDATE/DELETE TOP receive CK00001 naming the form. A paging clause cannot mask
a later unsupported WINDOW clause, and WITH TIES never becomes a CTE diagnostic.

These spellings execute at every accepted SELECT position: scalar, IN and EXISTS
children, derived/CTE bodies and INSERT SELECT sources, retaining the existing
ordinary derived-output ordering rules. Subqueries used as counts remain
CK00001 naming LIMIT, OFFSET, FETCH or TOP. Counts use the existing single
EvaluateCount validator and Limit/Offset plan fields; negative or noninteger values
receive CS02018 naming the written clause. SqlSelectExpression.LimitSyntaxKind
records Limit, Fetch or Top (null without a limit). Canonical rendering preserves that
family: LIMIT before OFFSET, FETCH FIRST n ROWS ONLY, and TOP (n).
PERCENT and TIES remain ordinary identifiers outside paging suffixes; canonical
rendering quotes those names. Data-storage format 19 fences earlier persisted
spellings; older databases are refused without an upgrade path.

PostgreSQL's local reference grammar accepts both LIMIT/OFFSET orders at
`src/backend/parser/gram.y:13537-13558`, FETCH's default count at `:13623-13631`,
and OFFSET's ROW/ROWS suffix at `:13652-13658`. The ISO features measured here are
F857/F858/F860 and F861/F862/F865; FETCH count grammar is at `:13674-13696`.
F866 (PERCENT) and F867 (WITH TIES) remain excluded.

`LIMIT`, `FETCH`, `TOP` and `OFFSET` accept nonnegative integer counts through
`9223372036854775807` (`Int64.MaxValue`). After ordering and `DISTINCT`, `OFFSET`
discards that many rows and `LIMIT` keeps at most that many of the remaining rows.
A limit greater than the result size keeps every remaining row; an offset at or
beyond the result size returns no rows. These rules apply to stored tables,
N-way joins, virtual system relations and grouped queries, in process and
over the SQL wire protocol (#1100). Negative and non-integer counts retain the
`<clause> requires a non-negative integer.`
diagnostics.

For non-DISTINCT queries without ORDER BY, execution stops output evaluation after
OFFSET + LIMIT qualifying rows (#1523). Stored-table
and system-view scans also stop reading input. Grouped queries still consume their
aggregate input, but stop group projection and HAVING evaluation at the window;
correlated queries stop outer-row evaluation. Join inputs/results and set operands
still materialize, while their final projection stops at the window. Unvisited
projections and HAVING predicates raise no errors and make no volatile function
calls, following PostgreSQL's Limit node (`src/backend/executor/nodeLimit.c:82-124`).
Non-DISTINCT ORDER BY + a positive LIMIT
with fixed ordering collations evaluates every input and retains only the best
OFFSET + LIMIT rows, with stable input order for ties and the same collation/NULL
placement as a full sort. Deferred collations keep full sorting, using the last
qualifying row's resolved collations even when later rows fail WHERE or HAVING.
Non-DISTINCT LIMIT 0, with or without ORDER BY, returns before the scan starts:
no input row is read and no WHERE predicate, projection or ORDER BY key is
evaluated. Grouped queries still consume their aggregate input but evaluate no
group's HAVING or projection; correlated queries evaluate no outer row. Two cases
do not short-circuit: the independent-volatile ORDER BY fallback below reads and
sorts every input and projects every row before LIMIT 0 discards them, and a
grouped query whose ORDER BY is correlated evaluates every group's HAVING and
projection (but no ORDER BY key).
DISTINCT still completes
before pagination. Results remain fully evaluated before delivery; ordered index
access and spillable sorting are separate work.

For ordinary SELECT ordering without output-alias or ordinal dependencies,
nonvolatile projections and keys evaluate together in input order, projection
first. This can change which projection/key error appears first compared with
sorting all source rows before projection. Independent volatile projections retain
the full-sort fallback: keys evaluate in input order, then projections evaluate in
sorted output order before pagination, including without LIMIT. This preserves
the previous call order and count; PostgreSQL likewise postpones independent volatile
outputs (`src/backend/optimizer/plan/planner.c:6649-6662`). When ordering references
outputs, projections still evaluate once per input before keys. Grouped and correlated
projections retain their existing evaluation before sorting.

## VALUES and counts have no column scope (#1165)

A row of `INSERT ... VALUES` is evaluated once, against no table row: ISO SQL
forbids a column reference in an INSERT's table value constructor. `LIMIT`, `OFFSET`,
`FETCH` and parenthesized `TOP` counts are evaluated once too, while planning. Their expressions are
literals, parameters, and operators, signs, `CAST`, `COLLATE`, `CASE`, predicates
and scalar functions over them. A column reference anywhere in one of them,
qualified or not and however deeply nested, reports `CS01005` (ISO SQLSTATE
class 42, syntax error or access rule violation) while planning, so nothing
executes:

```text
CS01005: Column reference 'id' is not allowed in INSERT ... VALUES, which has no columns in scope. Use literals, parameters and expressions over them, or INSERT ... SELECT to read values from a table.
```

For a count the clause is `LIMIT`, `OFFSET`, `FETCH` or `TOP` and the message ends after
"expressions over them.". Every row is checked before the first is written, so
`VALUES (1, 1), (2, id)` inserts nothing. In process the statement throws a
`DatabaseException` with that message; over the wire it is `ExecutionFailure` with
the same text, the session stays ready, and an explicit transaction stays active.
`INSERT INTO u (id, a) VALUES (id, 1)` used to resolve `id` against the target table
and fail during evaluation with a runtime error that ended a wire session with
`Internal`. A count with a column reference reported `Unknown column` without a
code, or, inside a subquery, came back as an error result carrying `CK00001`'s
correlated-subquery diagnosis instead of throwing (`ExecutionFailure` on the wire
either way). A subquery's count follows the same rule as the top level's at every
nesting level, whether the reference names the subquery's own columns or the outer
query's: ISO SQL's fetch-first count is a simple value specification, so no column
of any scope is visible to it. An explicit outer qualifier there, as in
`(SELECT id FROM w LIMIT v.a)`, parses and fails while planning with `CS01005`,
just like local and unqualified columns. To insert table values, use `INSERT ... SELECT`.

The same clauses reject an aggregate (`Aggregate functions are not allowed in
INSERT ... VALUES.`, and likewise for `LIMIT` and `OFFSET`) and a bare `*` (`'*' is
not allowed in INSERT ... VALUES.`, likewise for `LIMIT` and `OFFSET`, where it used
to report `Expression 'SqlStarExpression' is not supported by the executor yet.`)
while planning, and a sign over an operand the plan already knows is not a number
reports `CS02003` there too. A column reference is reported first, so
`VALUES (SUM(a))` and `VALUES (-a)` name the column.

`DEFAULT` as a whole VALUES item uses the target column's catalog default, exactly
like omitting that column. Without a default it supplies NULL, subject to NOT NULL.
Explicit NULL remains NULL even when the column has a default. Reordered column lists
and mixed multi-row lists map each DEFAULT to its own target column.
`INSERT INTO t DEFAULT VALUES` inserts exactly one row with every column omitted.
The optional column-list form, `INSERT INTO t (a) DEFAULT VALUES`, has the same effect;
listed columns must still exist and must not repeat. `UPDATE t SET c = DEFAULT`
uses the same column-default path for each matching row. All constraints apply before
the statement publishes any writes, and both session requests and the SQL wire use
these rules. Existing stored function-call defaults retain their per-assignment
evaluation; DEFAULT declaration expressions follow the #1121 contract.

DEFAULT is contextually typed syntax, not a scalar operand: `VALUES (DEFAULT + 1)`,
`VALUES (COALESCE(DEFAULT, 1))`, `VALUES ((DEFAULT))` and `SET c = 1 + DEFAULT`
report CS00003. Bare DEFAULT in any scalar position also reports CS00003, including
SELECT lists, WHERE, function arguments, operator operands and INSERT ... SELECT.
It never binds to a column named `"DEFAULT"`; the quoted spelling still binds.
Qualified names `t.default` and `t."default"` are column references, including in SET
and VALUES expressions (the latter still have no columns in scope at binding).
Quoted identifiers, string literals and comments containing the word are unaffected.
PostgreSQL rejects scalar DEFAULT in `src/backend/parser/parse_expr.c:310-316` and
permits keyword field labels through `gram.y:17325-17329,17716,18042-18047`.
PostgreSQL represents the keyword as `SetToDefault`
(`src/backend/parser/gram.y:15788-15804`) and allows it directly in INSERT values and
UPDATE sources (`src/backend/parser/parse_target.c:85-92,263-272`); DEFAULT VALUES
has no SELECT source (`gram.y:12559-12565`). Its optional-column-list grammar does
not include DEFAULT VALUES; accepting that combination here follows #1193's explicit
column-list requirement. These DEFAULT forms add no named clause (+0); see the [statement matrix](#statement-matrix).

`CURRENT_DATE`, `CURRENT_TIME`, `CURRENT_TIMESTAMP`, `LOCALTIME` and `LOCALTIMESTAMP`
parse as niladic function nodes and execute using the transaction-start clock (#1120).
`NOW()` executes with the same clock. Quoted datetime names and qualified references
remain column references and receive `CS01005` in these clauses.

Repeated UPDATE SET targets fail planning with CS01019, even when no row matches;
DEFAULT counts as an assignment. PRIMARY KEY, UNIQUE and FOREIGN KEY child and referenced column lists
must not repeat a column (case-insensitive); CREATE TABLE and ALTER TABLE ADD CONSTRAINT
report CS01019 naming the column and constraint. A table may declare one PRIMARY KEY:
a second inline or table declaration, or ALTER ADD CONSTRAINT / ADD COLUMN PRIMARY KEY over an existing key, reports
CS01019 naming the constraint and table. Repeated PRIMARY KEY clauses on one column
also fail, including ADD COLUMN. Composite keys remain supported. The live
SqlInvalidStatementConformanceTests corpus exercises these refusals in process and over the wire.
CREATE TABLE IF NOT EXISTS succeeds without validating key declarations when the table already
exists and leaves it unchanged, matching PostgreSQL parse_utilcmd.c:202 (without its NOTICE).
Refusing repeated FOREIGN KEY child columns is stricter than PostgreSQL.

## Grouping and aggregate functions (#1020)

### Aggregate FILTER and Boolean/statistical functions (#1720, SQ-H1)

Every catalog aggregate, including application aggregates, accepts `FILTER (WHERE condition)`
after the call (after WITHIN GROUP for LISTAGG). Only TRUE admits a row; FALSE and NULL
reject it before any argument, coercion, input ordering key, DISTINCT tuple or transition is
evaluated. This includes correlated and independent argument subqueries. Independent children
inside the filtered call are evaluated on first demand and cached for the statement.
FILTER works with default/ALL/DISTINCT calls, COUNT(*), ordered STRING_AGG/LISTAGG,
GROUP BY, HAVING and ORDER BY. Different conditions retain independent aggregate states.
Filtered calls with volatile arguments, ordering keys or conditions also retain independent
states; child queries conservatively prevent aggregate sharing.
The condition binds over input columns and may reference enclosing columns or contain
subqueries; aggregates local to those child queries belong to the child scope. Calls owned
entirely by an enclosing query retain the existing outer-only aggregate CK00001 boundary.
FILTER on a scalar call fails with CS05072 (42809), a same-scope aggregate in the condition with CS05073
(42803), and a non-Boolean condition with CS02003 (42804). Window calls in a condition and
FILTER on a window aggregate retain CK00001 until the window family implements them.
EXPLAIN reports `filtered_aggregates=N` on a grouping node only when N is nonzero.

| Aggregate | Non-NULL input and result |
|---|---|
| BOOL_AND / EVERY | Boolean input; TRUE exactly when every non-NULL value is TRUE. EVERY aliases BOOL_AND. |
| BOOL_OR | Boolean input; TRUE when any non-NULL value is TRUE. |
| STDDEV / STDDEV_SAMP | Numeric input; sample standard deviation. STDDEV aliases STDDEV_SAMP. |
| VARIANCE / VAR_SAMP | Numeric input; sample variance. VARIANCE aliases VAR_SAMP. |
| STDDEV_POP / VAR_POP | Numeric input; population standard deviation / variance. |

All nine names ignore NULL arguments and return NULL for empty/all-NULL input. Boolean
results are nullable Boolean. Sample statistics return NULL for one value; population
statistics return zero for one finite value. Signed integer and Decimal inputs return nullable
Decimal (`numeric`); Real and Double inputs return nullable Float64 (`double precision`).
An untyped NULL statistical argument resolves to Double. Other argument types fail with
CS01006. Each CASE/COALESCE node within a statistical input converts to its own numeric
common type before its enclosing expression, DISTINCT and accumulation, including REAL
rounding before an outer widening to Double. This also applies through NULLIF, casts and
arithmetic; NULLIF takes its normalized first operand's type. Ordinary scalar expressions
retain their existing alternative typing. Integer/Decimal
transitions accumulate exact scaled sums and squares, avoiding
intermediate overflow and double rounding even near BIGINT and Decimal limits. The final
result uses the engine's existing 96-bit Decimal domain, choosing up to 28 fractional digits
and rounding midpoint ties away from zero; a final value outside that domain is CS02002.
Standard deviation rounds the root of the exact rational variance directly. This preserves
representable roots even when the variance rounds to zero. Floating transitions follow
PostgreSQL's Youngs-Cramer calculation: nonfinite inputs yield NaN when a result is defined;
finite-input overflow is CS02002. No arbitrary-precision numeric result type is added.

References: PostgreSQL execExpr.c:3719-3740; parse_func.c:339-348; parse_agg.c:420-422,941-943;
pg_aggregate.dat:335-363,448-477,523-533; float.c:3090-3142; numeric.c:6060-6139,11683-11758;
pg_type.dat:225-226 and parse_func.c:1284-1360 (preferred Double for an untyped NULL);
parse_expr.c:1740-1768,2244-2257 and parse_coerce.c:1346-1456 (CASE/COALESCE common types
and conversions at each node, CASE ELSE first).

### Ordinary grouping and aggregate inputs

Grouping is a separate execution stage over a stored table, a virtual system
relation, or supported N-way INNER/LEFT/RIGHT/FULL/CROSS/comma joins. `WHERE` first filters
individual input rows. `GROUP BY` then forms one row per distinct tuple of one
or more scalar expressions. Aggregate functions consume the surviving rows in
each group; `HAVING` filters the completed groups and retains only TRUE results.
`ORDER BY`, `LIMIT`, and `OFFSET` operate on the resulting groups. This order
applies equally through `SqlDatabaseServer` and the SQL client.

`COUNT(*)`, `COUNT(expr)`, `SUM(expr)`, `AVG(expr)`, `MIN(expr)`, and `MAX(expr)`
work with and without `GROUP BY`, including multiple aggregate projections and
scalar expressions containing aggregate results. A column reference outside
an aggregate must be a grouping expression, or part of an expression derived
from grouped columns. An ungrouped source column in a projection, `HAVING`, or
`ORDER BY` is a planning error, even when the input is empty; the executor never
chooses an arbitrary row's value. Aggregate arguments cannot contain another
aggregate, and aggregates cannot occur in `WHERE`, `JOIN ... ON`, or `GROUP BY`.

Repeated scalar-subquery, EXISTS and ANY/SOME/ALL grouping expressions may appear in SELECT,
HAVING and ORDER BY. They reuse the group value when the complete query bodies are structurally
identical in the same scope, including identical correlations, and their operators, quantifiers,
negation and bound scalar operands agree. IN keys likewise compare their query bodies.
Different or unproven keys do not share a slot; an ungrouped reference still reports `CS05017`.
Matching follows PostgreSQL's whole-expression comparison (`parse_agg.c:1451`).

`UPDATE` and `DELETE` act on one row at a time, so an aggregate in `UPDATE ... SET`
or in their `WHERE` fails while planning, as PostgreSQL rejects one in those clauses
(SQLSTATE 42803), with `Aggregate functions are not allowed in UPDATE SET.` or
`Aggregate functions are not allowed in WHERE.`. The statement fails
the same way over an empty table as over a populated one and changes no row. Until
the #1189 review it succeeded over an empty table and failed per row over a
populated one, with an uncoded `... is not supported by the executor yet.` message.
A wrong argument count is checked first, so `UPDATE t SET a = SUM()` reports
`CS01006`. Aggregates inside UPDATE/DELETE WHERE subqueries execute in the child's
scope (#1113); subqueries in UPDATE SET retain `CK00001`.

| Aggregate | NULL and empty-input behavior | Result type |
|---|---|---|
| `COUNT(*)` | Counts every input row, including rows containing only NULLs. Empty input returns zero. | Nonnullable `Int64` |
| `COUNT(expr)` | Counts only non-NULL evaluated arguments. All-NULL or empty input returns zero. | Nonnullable `Int64` |
| `SUM(expr)` | Ignores NULL arguments. All-NULL or empty input returns NULL. | Nullable `Decimal` |
| `AVG(expr)` | Ignores NULL arguments in both the sum and divisor. All-NULL or empty input returns NULL. | Nullable `Decimal`, including integer input |
| `MIN(expr)` / `MAX(expr)` | Ignore NULL arguments. All-NULL or empty input returns NULL. | Nullable argument type |
| `aggregate(DISTINCT expr)` / `aggregate(ALL expr)` | DISTINCT consumes each argument tuple once; ALL consumes every row. The aggregate's NULL rule applies. | Same as the aggregate |
| `STRING_AGG(value, delimiter [ORDER BY keys])` | Skips NULL values; NULL delimiter is empty. Empty/all-NULL input returns NULL. | Nullable String |
| `LISTAGG(value [, delimiter]) WITHIN GROUP (ORDER BY keys)` | Same as STRING_AGG; omitted delimiter is empty. | Nullable String |

An ungrouped aggregate has one implicit group, so an empty input produces one
row containing the zero/NULL results above, subject to `HAVING` and pagination.
An explicit `GROUP BY` over empty input produces no groups and no result rows.
NULL grouping keys compare equal for grouping. String grouping equality and
hashing use the same effective collation. Binary groups keep `Alice` and `alice`
distinct; case-insensitive groups combine them while preserving a member's
original spelling in the result.

`SUM` and `AVG` accept signed integers, Decimal, Float32, and Float64. Numeric
arguments are converted to `System.Decimal` before accumulation; approximate
inputs follow the runtime floating-point-to-Decimal conversion. Non-numeric
arguments, non-finite approximate values, and numeric overflow are errors;
the last two report `CS02002` (see the arithmetic fault contract above).
`SUM` always returns Decimal. `AVG` divides the Decimal sum by the non-NULL
Int64 count using `System.Decimal` division: the nearest representable Decimal,
with midpoint ties rounded to even and up to 28 fractional digits. Thus integer
inputs 2 and 5 have Decimal average 3.5; integer truncation is never used.
Neither operation promises an arbitrary-precision result or fixed output scale.
The in-process and wire result metadata use the same base types as these values.
Numeric `CASE`/`COALESCE` alternatives in grouped projections use a common
numeric result type; incompatible nonnumeric alternatives are planning errors.

Aggregate expressions, output aliases (including inside scalar expressions),
and select-list ordinals can be used as `ORDER BY` keys in grouped/aggregate
queries under the ordering contract above. Aliases in `GROUP BY` or `HAVING`
remain outside this subset; use the source/grouping expression or repeat the
aggregate. GROUP BY ordinals report `CK00001`.

`DISTINCT` removes duplicate argument tuples after parameter coercion, under each
argument's collation and the exact numeric equality used by SELECT DISTINCT and GROUP BY.
Thus Int64 1 and Decimal 1.0 share a class, and NaNs share one class. Strict aggregates
skip NULL arguments before deduplication. Explicit `ALL` means the default. Distinct
state grows with the number of distinct values per group. Quantifiers cannot accompany
`*` (CS00003); COUNT with several arguments still fails with CS01006.

Every aggregate accepts an inner `ORDER BY`, including SUM whose result does not depend
on ordering. Keys bind over the source row, may contain prepared subqueries, and use
ordinary expression rules: an integer is a constant, never a select-list ordinal.
Sorting is stable, with NULL first in ASC and last in DESC. With DISTINCT, each key must
structurally match an argument after column binding (CS05032 otherwise). Quantifiers
and inner ordering on a scalar call also fail with CS05032. The parser accepts these
modifiers without knowing which names the catalog makes aggregates.
Input and outer sorting observe statement cancellation during key evaluation and comparisons,
checking at most every 32 operations and preserving cancellation through runtime sort wrappers.

STRING_AGG(value, delimiter [ORDER BY keys]) uses PostgreSQL spelling; ISO LISTAGG(value
[, delimiter]) WITHIN GROUP (ORDER BY keys) executes through the same accumulator.
LISTAGG permits DISTINCT, requires WITHIN GROUP, and cannot also have an inner ORDER BY
(CS00003), including when its name is written as the quoted lower-case `"listagg"`, which
names the same function. Any other quoted spelling, such as `"LISTAGG"`, is an ordinary call:
it may parse, but the planner refuses it without WITHIN GROUP keys. An omitted LISTAGG delimiter is empty. The delimiter must be a string literal,
NULL literal, or parameter known at plan time; a column/expression delimiter is CK00001.
Both arguments must be String or Character; CHAR is trimmed on conversion to text.
JSON and other types fail with CS01006 and require CAST(... AS TEXT). NULL values are
skipped; a NULL delimiter contributes nothing. Empty/all-NULL input returns NULL; a
single value has no leading/trailing delimiter. The nullable String result takes its
collation from the value alone, independently of the delimiter and ordering keys.

STRING_AGG/LISTAGG use StringBuilder and fail with CS02002 as soon as the output would
exceed 1,048,576 UTF-16 units. Ordered calls check while buffering, before sorting. Ordered
DISTINCT calls retain one representative per equivalence class, selected in sorted input
order, so case-insensitive aggregation preserves one input spelling.

Empty grouping sets (GROUP BY ()), GROUPING SETS, ROLLUP, CUBE,
GROUPING/GROUPING_ID, windows, non-LISTAGG WITHIN GROUP, and LISTAGG ON OVERFLOW
(including ERROR and TRUNCATE) retain CK00001. ON OVERFLOW is matched contextually
inside LISTAGG and names the whole construct once; OVERFLOW remains an ordinary identifier.
FILTER adds one named clause; the T1 modifiers add none.

Reference behavior: PostgreSQL src/backend/parser/gram.y:16017-16129,
parse_agg.c:189-203,231-233 and parse_func.c:324-341; executor/nodeAgg.c:832-834,888-898;
utils/adt/varlena.c:4331-4366,4506-4515. Cohesion restricts the delimiter to a literal or
parameter and also provides ISO LISTAGG.

## Set operations (#1114)

UNION, INTERSECT and EXCEPT accept optional ALL or DISTINCT. DISTINCT is the default.
UNION ALL concatenates inputs; UNION DISTINCT removes duplicates within and across inputs.
INTERSECT DISTINCT returns each common row once; EXCEPT DISTINCT returns each left-only row once.
For a row occurring m times on the left and n times on the right, INTERSECT ALL returns
min(m,n) copies and EXCEPT ALL returns max(m-n,0). NULL cells compare equal for set
membership, including multi-column rows. Equality and hashing use the same comparer as
GROUP BY and SELECT DISTINCT. Spelling of a collation-equivalent surviving row is unspecified.

INTERSECT binds tighter than UNION and EXCEPT; UNION and EXCEPT associate left to right.
Parenthesized operands may carry their own ORDER BY, LIMIT, OFFSET or FETCH. Without
parentheses those clauses belong to the combined result; placing them before another
operator is CS00003. TOP stays on its SELECT operand, so
`SELECT id FROM t UNION SELECT TOP 1 id FROM u OFFSET 1` skips a combined row.
Combined ORDER BY accepts first-operand output names (implicit names included) and positive
one-based ordinals only. Expressions, qualified names, absent output names and invalid ordinals
fail planning with CS02018; ambiguous names use CS01019. Combined OFFSET/FETCH shares
the ordinary Int64 count validation. Explicit NULLS FIRST/LAST follows the ordinary ordering contract.

Both inputs must have the same column count (CS05013 names both counts).
Types are checked before reading any row. The USING common-type rule selects a category-compatible
type with implicit registry casts, retaining the left candidate when conversions work in both
directions. Numeric one-way widening makes INTEGER with BIGINT produce Int64 values and metadata,
and DECIMAL with DOUBLE produce Double. Untyped NULL and string literals are unknown and take the other input's type; an all-unknown
pair becomes TEXT. Explicit CAST to TEXT remains typed and cannot match INTEGER. Type mismatches use CS02003, naming the one-based position and both types.
Length, precision and scale survive only when both input types and modifiers agree. A domain
survives only when every input has the identical domain and modifiers. Values convert before
duplicate comparison: TEXT combined with CHAR trims the latter through its registered text cast,
CHAR first with differing types or typmods becomes unconstrained CHAR and preserves full values,
including TEXT lengths; no declaration default CHAR(1) is applied. DATE combined with TIMESTAMP promotes calendar values. Output names come from the first input;
nullability is the OR of each operand's reported result metadata.

Per-column collation uses explicit COLLATE over a declared column collation over the database
default; the left operand wins a tie. This deliberately resolves conflicting declared collations
instead of rejecting them. The unified collation also governs an IN subquery's membership.
Derived and CTE outputs expose newly synthesized metadata, preserving the unified type and
collation without borrowing a physical source ordinal or expression.

Set bodies execute at the top level, in IN/NOT IN, EXISTS/NOT EXISTS, scalar subqueries,
derived tables, CTE definitions and INSERT query sources, including correlated operands.
`IN ((SELECT id FROM t))` is now membership, with redundant query parentheses removed
by canonical rendering. SELECT operands may omit FROM in both top-level and nested queries (#1373). Each subquery counts once toward the existing
32-level subquery limit; set nodes count individually toward the expression depth limit.
Parenthesized scalar queries followed by arithmetic, comparison or a list comma remain values
(PostgreSQL gram.y:15645,15899,15916). Parenthesized WITH retains the existing single subquery
refusal. Query lookahead is bounded; excess grouping parentheses retain their first-offending-token span.
All operands share one statement snapshot and catalog capture, see their transaction's own
writes, and retain existing INSERT atomicity and scalar-cardinality checks.

Compatible same-quantifier UNION chains flatten to one N-way materialization with O(total input)
time and memory before sorting; no accumulated prefix is copied per link. Other set nodes
materialize both operands and a hash membership/count table in O(L+R). Every remaining recursive
execution level checks the stack and reports CS05004. Sorting adds O(N log N); streaming
and spilling remain excluded.
CORRESPONDING (including BY forms) remains T2 and reports one contextual CK00001.
Nested WITH, recursive execution and LATERAL and outer ORDER BY keys bound to derived outputs retain their
existing exclusions.

Reference anchors in the local PostgreSQL clone: `src/backend/parser/gram.y:845-846,13103-13115`
(precedence and select_with_parens), `parse_coerce.c:1362-1457,1648-1678` (types, unknowns
and modifiers), `analyze.c:1983-2006` (combined ordering), and
`src/backend/executor/nodeSetOp.c:128-160` (multiplicities).
UNION, INTERSECT and EXCEPT were already keywords; removing their unsupported rows and
keeping CORRESPONDING contextual changes no persisted identifier quoting, so the storage
format is unchanged. Persisted-text drift tests pin these four spellings.

### Boolean search conditions (#1190)

WHERE, JOIN ON, HAVING, ON CONFLICT DO UPDATE WHERE, searched CASE WHEN, truth tests
(IS TRUE/FALSE/UNKNOWN) and operands of AND, OR and NOT require BOOLEAN. A known non-Boolean type, including arithmetic,
CASE or aggregate results and supplied parameters, fails planning before rows are read
with CS02003 / SQLSTATE 42804, naming the construct. Runtime checks enforce the same
rule for values whose types were unknown. An untyped NULL is UNKNOWN and retains SQL
three-valued logic; CAST(NULL AS INT) is a non-Boolean typed expression and is refused.
Only TRUE admits a row or selects a searched CASE arm. Boolean AND/OR keep their
short-circuit order; simple CASE compares values and does not require Boolean WHEN values.

Every CASE result and COALESCE alternative in a Boolean position must be Boolean, NULL,
or a valid Boolean input string literal, even when short-circuiting skips it. Boolean
string literals accept `t`, `true`, `f`, `false`, `yes`, `no`, `on`, `off`, `1`, `0`, `y`
and `n`, case-insensitively (PostgreSQL src/backend/utils/adt/bool.c:37-109). Other strings
fail with CS02003; text columns and text parameters do not acquire this literal coercion.
Table CHECK and domain CHECK use CS02003 / 42804 for every non-Boolean position.

Opening a database binds stored SQL function bodies without reapplying declaration-only
Boolean-position validation. An invalid legacy operand or an application function whose
result is no longer Boolean fails when the stored body is invoked with CS02003;
it does not prevent the database from opening.

## Quantified comparisons (#1718)

Scalar `expr op ANY|SOME|ALL (query)` executes for `=`, `<>` (or `!=`), `<`, `<=`, `>` and `>=`.
SOME is synonymous with ANY; canonical text retains its spelling. The query must
return exactly one column, including when empty (CS05013). Correlated references
use the same lexical scopes as IN and EXISTS. Query bodies may contain set
operations, derived sources and references to an enclosing WITH.

ANY/SOME combines comparisons with OR; ALL combines them with AND. Empty ANY/SOME
is FALSE and empty ALL is TRUE, even with a NULL left operand. A nonempty result
with a NULL left operand is UNKNOWN. A NULL comparison contributes UNKNOWN unless
a TRUE decides ANY/SOME or a FALSE decides ALL. Projection retains UNKNOWN as NULL;
WHERE admits only TRUE. These rules follow PostgreSQL
src/backend/executor/nodeSubplan.c:244-269,399-427.

Comparisons use the ordinary scalar numeric, temporal, binary, CHAR and collation
rules, including CS02055 / 42804 for incompatible non-NULL values when compared.
Explicit left COLLATE overrides the child output collation; otherwise the child
column contributes declared collation strength, as for IN. The Boolean result
contributes no text collation. Scalar folding evaluates the left operand once for
a nonempty set and skips it for an empty set, including correlated scalar children.
Independent subqueries retain eager statement preparation. Folding stops on a deciding truth
value. Set children use the existing materialization strategy: independent children
run once per statement, correlated children per outer frame. Child projection
evaluation is not guaranteed to stop at a deciding comparison. Lazy CASE, COALESCE
and Boolean branches skip unvisited correlated children.

The existing 32-level subquery and whole-expression depth limits apply. Row values,
array operands and subqueries in UPDATE SET, INSERT VALUES, RETURNING, CHECK/DEFAULT
and LIMIT/OFFSET remain excluded. Row-value syntax retains CS00003; array operands
retain named CK00001. ANY/SOME remain contextual identifier spellings and keep their
existing canonical quoting. No disk or wire format changes.

## Subqueries and query-source inserts (#1021)

Subqueries are separate planned queries. An uncorrelated child materializes once
under the statement snapshot; a child referencing an enclosing scope executes for
each outer row. Supported forms are correlated and uncorrelated
`IN (SELECT ...)`, `NOT IN (SELECT ...)`, `EXISTS (SELECT ...)`,
`NOT EXISTS (SELECT ...)`, and scalar `(SELECT ...)` expressions. They compose
with the supported SELECT projection, WHERE, INNER JOIN/ON, GROUP BY/HAVING,
ORDER BY, LIMIT, and OFFSET surface, including nested queries and wire execution.
A SELECT without FROM reads one implicit row (#1373), so SELECT 1 and SELECT f(1)
return one row. WHERE FALSE or NULL and LIMIT 0/OFFSET 1 can remove it; aggregates,
ordering, DISTINCT, set operands, derived tables and views use ordinary query semantics.
A wildcard without FROM fails with CS05013. Scalar and IN queries must return
exactly one column; EXISTS accepts any valid select list.

| Form | Result semantics |
|---|---|
| `IN` | TRUE for an equal non-NULL member. Without a match, a NULL member or a NULL operand against a nonempty result yields UNKNOWN. Empty results produce FALSE, even for a NULL operand. |
| `NOT IN` | FALSE for an equal non-NULL member. A NULL-containing result produces UNKNOWN for otherwise nonmatching operands, so WHERE retains no rows. An empty result produces TRUE, including for a NULL operand. |
| `EXISTS` / `NOT EXISTS` | Test whether the result has any rows; projected NULLs count as rows. Empty results produce FALSE / TRUE. |
| Scalar subquery | One row supplies its value, no rows supply NULL, and more than one row raises a cardinality error. An arbitrary first row is never selected. |

Every child query uses the containing statement's MVCC snapshot and transaction
context; it never creates another read view. Inner and outer stored rows
therefore observe the same instant. Children and INSERT sources can join system
relations with each other or with stored tables. Their relation bindings request the
containing statement's one catalog image, including system relations in JOIN targets.
Materialization retains the output type
and collation, including NULL and empty scalar results.

`INSERT ... SELECT` maps the result columns to the specified destination list,
or to all destination columns when the list is omitted. Planning validates the
column count and declared type compatibility even when the source returns no
rows. Destination coercion and the literal-insert path enforce defaults for
omitted columns, nullability, CHECK, primary/unique keys, and foreign keys for
every selected row. Any failure leaves none of the statement's rows inserted.
Reading from the destination table is supported: the entire source is
materialized under the statement snapshot before inserts begin, so newly
inserted rows cannot feed back into the source query.

Names resolve in the innermost FROM/JOIN scope first, then through enclosing
query scopes, including references two or more levels out. Local names and aliases
shadow outer ones; a matching local qualifier with a missing column does not fall
back outward. Ambiguity within any one scope reports located `CS01015`.
Correlated children execute in SELECT lists, WHERE, HAVING, JOIN ON, ordering,
grouping expressions and UPDATE/DELETE WHERE. Each outer row independently follows
the NULL, empty-set and scalar cardinality rules above, including repeated and NULL
correlation values. CASE, COALESCE and Boolean short-circuiting retain lazy evaluation.
Value slots belong to each evaluation frame; nested executions cannot overwrite
another outer row's results. Retained BYTEA results detach on every delivery.

In a grouped outer query, a child evaluated over completed groups may reference
grouping-key columns only. A reference to any other outer column reports
`CS05017`, naming the column. Subqueries within aggregate arguments, WHERE and
ON evaluate over source rows. Aggregate levels follow the lowest variable or
aggregate dependency, descending into nested query bodies and adjusting levels.
An aggregate belonging to an outer level reports `CK00001` before grouped-column
validation, with explicit, implicit or absent outer grouping. Relocating it to an
enclosing query is outside this subset. A mixed aggregate with a local dependency
executes at its own level. A count expression still has no column scope: local and
outer columns in LIMIT/OFFSET report `CS01005` (#1165).
PostgreSQL's reference implementation searches parent namespaces in
`src/backend/parser/parse_relation.c:151-179`, and rejects ungrouped outer columns in
`parse_agg.c:1545-1564`; its aggregate level analysis is at `parse_agg.c:646-690,762,850`.
Grouped outer references compare bound grouping values and types, including USING
merges and coercions (`parse_clause.c:1722`); aggregate arguments at the grouped level
may read its ungrouped source columns (`parse_agg.c:1385-1405`).
Query nesting permits at
most **32 subquery levels**, including derived bodies, below the top-level SELECT (or INSERT's
source SELECT); level 33 reports `CK00001` before recursive parsing continues.
Each subquery is also one level of the statement's expression tree, and its
clauses nest below it, so the [expression nesting limit](#expression-nesting-limit-1151)
(256 levels by default) bounds the statement as a whole, subqueries included. The
32-level subquery limit is fixed; it does not follow the configured expression limit.

Nested WITH, recursive CTE execution, lateral joins,
and subqueries in UPDATE SET, INSERT VALUES, CHECK/DEFAULT,
or LIMIT/OFFSET/FETCH/TOP expressions remain excluded with `CK00001`. Ordinary numeric
LIMIT/OFFSET/FETCH/TOP clauses on queries containing subqueries do execute. CHECK remains
a deterministic row-local expression and still rejects every subquery form.
Quantified comparisons add one measured named clause beside SUBQUERY.

## Common table expressions (#1116)

`WITH a [(column, ...)] AS (SELECT ...), b AS (SELECT ... FROM a) ...`
executes before SELECT, INSERT, UPDATE, DELETE or MERGE. The ISO query-source spelling
`INSERT INTO target WITH a AS (SELECT ...) SELECT ... FROM a` also executes.
The public AST exposes each statement's `CommonTables` and each definition's name,
output column list, body and source location. This adds exactly one advertised clause.
Contextual names such as `top` and `ties` work in WITH definitions and relation
references, including `WITH recent AS (...), top AS (SELECT ... FROM recent) SELECT ... FROM top`.

Each body sees earlier definitions. Unqualified reads resolve a CTE before a stored
table; a schema-qualified name bypasses that scope. A non-recursive self-reference
resolves the stored table, as does a name matching a later sibling when a stored
table exists. A CTE has no schema-qualified column identity: `t.id` or an alias
can name its output, while `dbo.t.id` requires a stored FROM entry or an enclosing
stored relation through correlation (otherwise CS01015, missing FROM-clause
entry). A CTE and a stored relation with the same unaliased name conflict
(CS01019); alias one to distinguish them. DML targets always resolve through the catalog, so
`WITH t AS (SELECT 1 AS id) DELETE FROM t` deletes from stored t. A target naming
only a CTE fails as an unknown table. Scalar and aggregate SELECT bodies may omit
FROM and operate on one implicit row; a wildcard still requires a relation.
This implicit row applies to every FROM-less SELECT, including set-operation operands,
subqueries and derived tables nested in that body (#1373). Constant bodies run the same planning checks, including
CS05017 for aggregates in WHERE even when the definition is unused.
FROM-less top-level and nested queries use the same implicit row (#1373).

Every definition is bound before execution, including unused definitions. Duplicate
names and unresolved references to later siblings use `CS01026` before any row
is read; a same-named stored table is resolved before the forward-reference hint.
An empty output column list is `CS00003`; a supplied list must have exactly the
body's output width (`CS05013`). Names use the catalog's case-insensitive rules.
Column renaming preserves types, bounds, nullability, domains and collations.

Every reachable definition materializes once per statement through the derived
relation contract, including references in joins, scalar, IN and EXISTS subqueries.
Unused definitions and their otherwise-unused dependencies never execute; binding
validates their count expressions and predicates without evaluating counts or seek bounds.
All CTEs share the statement's MVCC and catalog snapshots; catalog bindings inside
bodies request the same catalog image under auto-commit. Retained binary cells and
delivered copies follow the ordinary relation ownership rules.
Before DML writes, all reachable definitions materialize in declaration order.
Source rows cannot see rows the statement later inserts or deletes. A failure
rolls back the whole statement and preserves an explicit transaction. Identity
columns allocate one value for each inserted row through the existing insert path.
EXPLAIN includes WITH, materialization and constant-source operators without running them.

WITH inside subqueries, derived tables or CTE bodies, modifying CTE bodies,
`AS [NOT] MATERIALIZED` and SEARCH/CYCLE report `CK00001`.
A WITH list followed by another command kind reports `CS00003`.
Capability scanning continues through advertised WITH lists, so an excluded OVER,
NATURAL JOIN or WINDOW still reports its own clause.

## WITH RECURSIVE binding (#1719, SQ-R1)

WITH RECURSIVE declares visibility of every name throughout its WITH list. Acyclic
entries bind in dependency order and keep ordinary reachable-only materialization.
Unqualified self-references identify recursive entries; schema-qualified names still
resolve in the catalog. Mutual recursion reports CK00001. A recursive entry requires
an anchor UNION [ALL] recursive term and exactly one self-reference in the latter.
The anchor fixes output names, width, types, modifiers, domains and collations before
the recursive FROM binds. A complete column list renames the output. Unknown anchor
NULL/string output becomes text. Overall UNION type/modifier and collation changes
report CS02066 and CS02067 with cast/COLLATE hints; widths report CS02065.

The recursive reference cannot appear in the anchor, in scalar/IN/EXISTS/quantified
subqueries, on a nullable outer-join side, under INTERSECT ALL, or on either side of
EXCEPT ALL or the right of EXCEPT DISTINCT. INTERSECT DISTINCT, the left of EXCEPT
DISTINCT, preserved outer-join sides and derived FROM queries follow PostgreSQL's
allowed forms. Aggregates at the level containing the reference are invalid;
independent subquery aggregates remain allowed. Outer-only subquery aggregates
owned by the recursive level also report CS01061, including projection, HAVING and
ORDER BY subqueries over grouped recursive inputs (PostgreSQL
`src/backend/parser/parse_agg.c:646-690,1327-1334`). Top-level recursive UNION ORDER BY,
LIMIT/FETCH, OFFSET and FOR UPDATE/SHARE use CK00001. SEARCH/CYCLE, CREATE RECURSIVE
VIEW, nested WITH and modifying CTE bodies remain CK00001.

Valid recursive definitions bind all entries and the consuming statement, then
planning, EXPLAIN and execution return CK00001 until SQ-R2, even for an unused
recursive definition. No working-table operator or depth limit is introduced here.
Binding defers count/seek and constant-fold evaluation once recursion is identified.
CREATE VIEW with a recursive query body freezes ordinary anchor metadata without
executing it; cached and reopened view use propagates the same planning refusal.
No catalog, storage or wire format changes are needed.

IsRecursiveScope records the list modifier on each AST definition, independently
of whether it self-references. Binding retains the existing case-insensitive name
rules, requires complete output-name lists and conservatively makes recursive
columns nullable. Independent ready definitions retain declaration-order priority.
The complete row-locking strengths FOR UPDATE, FOR NO KEY UPDATE, FOR SHARE and
FOR KEY SHARE are contextual CK00001 refusals after the SELECT's clauses, including
in subqueries and CTE bodies. This deliberately replaces their previous CS00003
syntax refusal. Qualified identifiers such as `t.for` with implicit aliases `share`,
`no` or `key`, and explicit `AS share`, retain their ordinary meaning. Incomplete
strengths such as FOR NO or FOR KEY UPDATE remain CS00003. The grammar position
and complete strengths follow PostgreSQL `src/backend/parser/gram.y:13119-13176,13803-13840`.

WITH RECURSIVE before MERGE is refused with CK00001 at RECURSIVE, even for acyclic
definitions (PostgreSQL `src/backend/parser/parse_merge.c:124-129`). Plain WITH
before MERGE remains supported and shares SELECT's CTE binding and materialization.
These contextual exclusions do not change the 63/68 declared-clause count.

References re-derived from the local PostgreSQL clone: src/backend/parser/parse_cte.c:
345-400 (output checks and hints), 580-632 (unknown anchor output), 648-725 and 863-906
(dependencies), 915-1019 (form/tails), 1027-1199 (references and joins), 1209-1277
(set operands); parse_collate.c:807-860 (implicit default and conflicts);
parse_agg.c:1327-1334 (aggregates). No reference code is copied.

| Code | Condition | PostgreSQL SQLSTATE |
| --- | --- | --- |
| CS01054 | Recursive definition lacks a top-level anchor UNION [ALL] recursive term | 42P19 |
| CS01055 | Recursive reference in the anchor | 42P19 |
| CS01056 | Recursive reference inside a scalar, IN, quantified or EXISTS subquery | 42P19 |
| CS01057 | Recursive reference on the nullable side of an outer join | 42P19 |
| CS01058 | Recursive reference under INTERSECT ALL | 42P19 |
| CS01059 | Recursive reference in a prohibited EXCEPT operand | 42P19 |
| CS01060 | Recursive term has more than one self-reference | 42P19 |
| CS01061 | Aggregate at the query level containing a recursive reference | 42P19 |
| CS02065 | Recursive anchor, recursive output or declared column-list width mismatch | 42601 |
| CS02066 | Recursive UNION changes the anchor type, domain or type modifiers; cast hint | 42804 |
| CS02067 | Recursive UNION changes or conflicts with the anchor collation; COLLATE hint | 42P21 |

## Derived tables and DML predicates (#1113, part b)

`FROM (SELECT ...) AS d`, `(SELECT ...) d (a, b)`, JOIN targets and comma items
are query relations. An alias is required; an optional column list must name
every output exactly once by position (its length must equal the output width).
Both shorter and longer lists fail during planning with `CS05013`; unlike
PostgreSQL, a partial list does not fill remaining names from the target list.
Empty or whitespace-only output names also fail with `CS05013`, preserving
the session and any open transaction.
The alias hides all underlying names; an unknown or hidden qualifier reports
located `CS01015`. Duplicate output names remain ambiguous when referenced.
Output storage types, length/precision/scale, nullability, shared domains and collation provenance survive
materialization, including empty results. WHERE, GROUP BY/HAVING, ORDER BY and
LIMIT/OFFSET inside the body apply before the enclosing query reads its rows.
Stored, system and derived sources can join each other, including LEFT null
extension. An uncorrelated body executes once under the enclosing statement context;
a correlated body re-evaluates for each enclosing frame.

Correlated and uncorrelated scalar, membership and existence subqueries also execute in
UPDATE/DELETE WHERE. Their children materialize before any target mutation, so a
self-reading DELETE compares against the pre-statement values. A child sees
the session's uncommitted changes; rollback restores target rows and cascades.
System relations in these predicates and in derived bodies request the same one
statement catalog snapshot as ordinary SELECT children.

A derived reference that binds to a sibling FROM item receives `CK00001` naming LATERAL at that reference.
Typos and nonexistent or hidden qualifiers retain located `CS01015`.
An enclosing-query reference from a derived table nested in a subquery resolves
through the ordinary outer scope chain and executes for that outer row. Independent
derived bodies retain one materialization per statement. Set operations inside derived
bodies execute. Outer ORDER BY keys bound to derived outputs, including aliases and
ordinals, execute with the same NULL placement rules as stored inputs (#1112). Ordering
within derived bodies containing further derived inputs also executes. These extensions
add no named clause, catalog format or wire payload. PostgreSQL's local reference
uses `RangeSubselect` in `src/backend/parser/gram.y:13938-13956` and isolates its
namespace in `parse_clause.c:407-457`. The required alias and full-width column
list are this dialect's explicit subset rules.
Projected metadata follows `parse_relation.c:1715-1721`; qualified USING columns
keep physical metadata while the unqualified output keeps merged metadata.
Projected IN/NOT IN outputs are BOOLEAN regardless of the membership element type.
Default-only collation on a computed output remains inherited rather than becoming
a declared override. A declared source column or explicit inner COLLATE supplies
declared strength (priority 2), including explicit binary; two declared collations
retain the dialect's existing left-operand tie rule. Missing-reference classification
follows `parse_relation.c:3759-3789`.

## Expression nesting limit (#1151)

ISO SQL leaves nesting limits implementation-defined. The dialect takes a middle
course between SQL Server, which fixes small limits on particular constructs, and
PostgreSQL, which bounds only the stack (owner decision of 2026-10-01): it counts
**genuine nesting only**, against **one documented, configurable limit**, and keeps
a **stack check as the backstop** for whatever the limit admits.

- **The limit.** An expression nests at most **256 levels** by default. An engine
  sets its own within **32..4096** (`SqlDatabaseEngineOptions.ExpressionNestingLimit`,
  or `ExpressionNestingLimit` on the engine builder) and refuses a value outside that
  range with `ArgumentOutOfRangeException` when it is created. A parser takes the
  limit from `SqlQueryParserOptions.ExpressionNestingLimit` and checks the range when
  it is constructed; any other parser options, or none, give the default.
- **Tree depth counts.** Every node of the expression tree is a level: an
  arithmetic, concatenation or comparison operator, a predicate (`IS NULL`,
  `BETWEEN`, `IN`, `LIKE`), `NOT`, a sign, `CASE`, `CAST`, `COLLATE`, a function call
  (its arguments sit one level below it), an `IN` list (its values sit one level
  below it), a subquery, and the leaf at the bottom (a literal, column, parameter or
  `*`, the `*` of `COUNT(*)` included). Depth is measured on the tree, so an
  arithmetic chain counts every link: `1 + 1 + ... + 1` with N terms is N levels
  deep, and so are N - 1 signs over a column or N - 1 nested `ABS(...)` calls. A
  subquery's clauses count below the subquery, so the limit spans the whole
  statement: `SELECT (SELECT <255-level expression> FROM u) FROM t` is at the
  default limit.
- **Parenthesis nesting counts.** Grouping parentheses are not nodes, so they add no
  level to the tree, but at most as many pairs as the limit may enclose any point of
  an expression. A subquery's, call's, `IN` list's or `CAST`'s own parentheses are
  part of that construct, not grouping.
- **A flat `AND` or `OR` chain does not.** A run of terms joined by `AND` (or by
  `OR`) at one level is one node however many terms it has, as in PostgreSQL:
  `a = 1 OR a = 2 OR ... OR a = 10000` is three levels deep (the `OR`, the
  comparisons, their operands), and it parses, plans and executes, in process and
  over the wire. A parenthesized chain that opens a chain of the same operator is
  part of it (`(a AND b) AND c` is the chain `a AND b AND c`); one in a later
  position is a nested chain and a level (`a AND (b AND c)`). Evaluation is
  unchanged: the terms run first to last, the first that decides the result ends
  the chain, and three-valued logic gives the result the nested binary operators
  gave (see Short-circuit under [Arithmetic and numeric faults](#arithmetic-and-numeric-faults-1069)).

Deeper text reports `CS00006` at the token where the limit was crossed: the link of
a chain that made it too deep, the operand that would be one level too deep, the
operator of an `AND`/`OR` chain that would be one level too deep, or the first `(`
past the limit. The message names the limit (`Expression nesting exceeds the
supported limit of 256 levels.`). The rest of the statement is not parsed, so the
parser reports nothing further, and the statement keeps only its command type, so it
never executes: the text-execute seam throws `DatabaseParseException`
(`ParseFailure` on the wire, with the connection still usable), and a typed request
returns an error result carrying the diagnostic. Diagnostics the parser reported
before that point, such as a `CK00001` earlier in the statement, are kept, and so
are those of the checks that scan the whole text before parsing: an unterminated
literal or comment, a character outside the dialect, and an unsupported clause.
Before #1151 nothing bounded nesting: a 200,000-term expression overflowed the
stack, which .NET cannot catch, so one statement ended the process, and over the
wire every session of the server with it.

**The engine's limit holds on every seam.** The engine parses statement text with
its own limit, which covers every statement a wire client sends. A typed request was
parsed by its caller, perhaps with a higher limit, so the parser records how deep
each statement nests (`SqlQueryStatement.ExpressionNestingDepth`, the greater of its
deepest tree and its deepest parentheses), and the engine refuses a request that
nests deeper than its limit with an error result carrying `CS00006`
(`Expression nesting of 300 levels exceeds this engine's limit of 256 levels.`),
exactly what parsing the text itself would have decided. A request over a query the
parser did not return as a statement, such as a subquery taken out of a parsed
statement, has no parser's measure and is held to the depth of its own expression
tree, which is what the engine's walks recurse through. `SqlQueryRequest.FromSql`
parses at the default limit; a caller targeting an engine configured with another
limit passes it through the overload that takes `SqlQueryParserOptions`, so the
typed path accepts exactly what the engine accepts as text.

| Written | Levels | Result under the default limit |
|---|---|---|
| `1 + 1 + ... + 1`, 256 terms | 256 | Parses and executes |
| `1 + 1 + ... + 1`, 257 terms | 257 | `CS00006` at the 256th `+` |
| `- - ... - a`, 255 signs | 256 | Parses and executes |
| `ABS(ABS(... 1 ...))`, 256 calls | 257 | `CS00006` at the innermost `1` |
| `((( ... 1 ... )))`, 256 pairs | 1 | Parses and executes |
| `((( ... 1 ... )))`, 257 pairs | 1 | `CS00006` at the 257th `(` |
| `a = 1 AND a = 2 AND ...`, 10,000 comparisons | 3 | Parses and executes |
| `a = 1 OR (a = 2 OR (... OR (a = 256)))`, 256 comparisons | 257 | `CS00006` at the 255th `OR` |
| `a IN (1, 2, ..., 10000)` | 2 | Parses and executes |

**Compared with SQL Server and PostgreSQL.**

| | SQL Server | PostgreSQL | This dialect |
|---|---|---|---|
| Long `AND`/`OR` chains | Thousands of terms; an expression holds at most 65,535 identifiers and constants (error 8632) | One n-ary `BoolExpr` per chain, bounded by the stack alone | One n-ary `SqlLogicalExpression` per chain, one level of the limit |
| Fixed construct limits | `CASE` nests at most 10 levels (error 125); subqueries at most 32 | None | Subqueries at most 32 (`CK00001`); every other construct only by the expression limit |
| Expression nesting | No documented limit; a statement too deep for the server's stack fails (error 8631) | None beyond `max_stack_depth` (2 MB by default) | A documented limit, 256 by default, configurable within 32..4096 (`CS00006`) |
| Stack backstop | Error 8631 | `check_stack_depth()`: SQLSTATE 54001, stack depth limit exceeded | Stack checks in the parser and every walker: `CS05004`, SQLSTATE 54001 |

From SQL Server the dialect takes a fixed, documented limit that a client can rely
on and an operator can tune; from PostgreSQL it takes n-ary chains, which make long
generated predicates cheap, and the stack check as the last line of defense rather
than the only one.

**Stack.** The limit bounds how deep a statement nests, not how much stack the
thread that runs it has. Each level of parentheses, call arguments, `CASE` or `CAST`
costs the parser about 1 to 2 KB of stack in a release build, depending on how far
the JIT has optimized it, and about 6 KB in a debug build. In a release build the
deepest text the default limit accepts, 256 pairs of parentheses around 255 nested
calls, parses on a 1 MB thread, and a default .NET thread has more (1.5 MB on
Windows); a debug build needs about 3 MB for it. A higher configured limit admits
text no default thread can parse: 4,000 nested parentheses need several MB. Wherever
the stack runs out first, the statement fails instead of the process. The parser
checks the stack each time it recurses and reports `CS00007` (the text is within
the dialect and parses on a thread with more stack). An engine reports a statement
whose parse, or any later walk, runs out of stack as `CS05004` (ISO SQLSTATE
54001, statement too complex): a `DatabaseException` in process, `ExecutionFailure`
on the wire, with the session and any open transaction intact as for any failed
statement. A statement within the limit therefore executes or fails with
`CS05004`; it never ends the process.

**Persisted definitions stay openable.** `CREATE TABLE` and `ALTER TABLE` parse
with the engine's limit, so a stored `CHECK` or `DEFAULT` is never deeper than the
limit of the engine that stored it. Its canonical text (see [Persisted definitions
are canonical](#persisted-definitions-are-canonical)) has the same tree, and the
renderer adds at most one pair of parentheses around a node, so the stored text also
nests its parentheses no deeper than that tree. The engine reads persisted
definitions back at the highest limit, 4096, because the limit decides which
statements an engine accepts, not which databases it opens: a definition stored
under one engine's limit opens, and is enforced, under every other, including one
that would refuse the same text as a statement. `SqlExpressionRenderer` refuses a
tree deeper than 4096 levels with `NotSupportedException`, because no parser would
read its text back. An `AND`/`OR` chain renders term by term to the text the nested
binary form rendered (`a AND b AND c`, `a AND (b AND c)`), so canonical text stored
before chains were n-ary reads back to the same chain and renders unchanged. A
database opened on a thread too small to read its deepest definition back fails to
open with an error that says so, that the catalog is not damaged, and to open it on
a thread with a larger stack. A statement that reads a definition back on such a
thread, such as a DDL proving its canonical text, fails with `CS05004` instead.

**The engine checks its stack too.** Every recursive walk over a statement (the
session's system-relation scan, planning, evaluation, CHECK validation, and
binding a persisted definition) checks the stack before it descends, and recurses
once per level of the tree, never once per term of an `AND`/`OR` chain, whose terms
it iterates. A statement that runs any walk out of stack fails with `CS05004` as
described above. `LIKE` matching is iterative (#1111): its pattern length no
longer consumes stack per wildcard. Long matches observe statement cancellation.

## System-view matrix (C1)

All 11 registered system relations participate in the supported N-way joins,
with each other or with stored tables, on either side. Aliases, three-part column
references, ambiguity checks and all supported SELECT operators use the same relation
binding. New registered system relations inherit joining without join-specific code.

The binder requests one immutable catalog image when it first resolves a system relation.
Every child query and INSERT source shares that image; no outer-AST prediction is needed.
Snapshot transactions supply their BEGIN-time image; ReadCommitted and automatic statements
capture on first system-relation binding. A stored-only statement needs no catalog capture.
Derived tables, CTE bodies and UPDATE/DELETE subqueries use this capture; future set operands and view bodies inherit
this binding rule when their syntax becomes executable. Their current admission boundaries
remain as stated above.

The every-relation conformance theory seeds a domain with `CREATE DOMAIN`. Its self-join
key is the first (catalog-name) column for SCHEMATA, TABLES, COLUMNS, TABLE_CONSTRAINTS,
KEY_COLUMN_USAGE, REFERENTIAL_CONSTRAINTS, CHECK_CONSTRAINTS, INDEXES and OBJECT_OWNERSHIP;
FUNCTIONS, TYPES and DOMAINS use IS_SQL_DEFINED. Equality preserves each key's multiplicity:
for key groups of size k the self join yields the sum of k squared rows. Catalog-name keys
are constant within the database and therefore yield n squared rows. Function/type/domain
names are not constant and are deliberately not used as that cross-product key.

The SQL engine exposes the following virtual relations through ordinary `SELECT`,
including the wire protocol. View names require their schema and are
case-insensitive. This is the MVP subset of ISO 9075-11 column names and type
conventions, not the complete ISO view layouts. Columns below are listed in
`SELECT *` order. No Cohesion-only columns are appended to the ISO relations.

| Relation | Columns, in order | Rows |
|---|---|---|
| `INFORMATION_SCHEMA.TABLES` | `TABLE_CATALOG`, `TABLE_SCHEMA`, `TABLE_NAME`, `TABLE_TYPE` | Stored tables (`BASE TABLE`) and persisted user views (`VIEW`) in the current database |
| `INFORMATION_SCHEMA.VIEWS` | `TABLE_CATALOG`, `TABLE_SCHEMA`, `TABLE_NAME`, `VIEW_DEFINITION`, `CHECK_OPTION`, `IS_UPDATABLE` | Persisted canonical body; `NONE`, `NO` for every read-only view |
| `INFORMATION_SCHEMA.SCHEMATA` | `CATALOG_NAME`, `SCHEMA_NAME`, `SCHEMA_OWNER`, `DEFAULT_CHARACTER_SET_CATALOG`, `DEFAULT_CHARACTER_SET_SCHEMA`, `DEFAULT_CHARACTER_SET_NAME`, `SQL_PATH` | Persisted namespaces, including empty namespaces and dbo, from the statement's captured catalog. Owner principal, default character set and SQL path are NULL because those SQL facilities are not implemented. |
| `INFORMATION_SCHEMA.COLUMNS` | `TABLE_CATALOG`, `TABLE_SCHEMA`, `TABLE_NAME`, `COLUMN_NAME`, `ORDINAL_POSITION`, `COLUMN_DEFAULT`, `IS_NULLABLE`, `DATA_TYPE`, `CHARACTER_MAXIMUM_LENGTH`, `CHARACTER_OCTET_LENGTH`, `NUMERIC_PRECISION`, `NUMERIC_PRECISION_RADIX`, `NUMERIC_SCALE`, `DATETIME_PRECISION`, `IS_IDENTITY`, `IDENTITY_GENERATION`, `IDENTITY_START`, `IDENTITY_INCREMENT`, `IDENTITY_MAXIMUM`, `IDENTITY_MINIMUM`, `IDENTITY_CYCLE` | One row per live table or view column; view names, types, domains and collations are frozen, and every view column reports IS_NULLABLE = YES without body binding. Ordinals start at one and have no gap where a table column was dropped. All 19 DATA_TYPE names parse in DDL and CAST; aliases report canonical identity and modifiers. COLUMN_DEFAULT renders resolved temporal and binary literal values with typed prefixes, including defaults declared as plain strings; other defaults retain canonical SQL |
| `INFORMATION_SCHEMA.TABLE_CONSTRAINTS` | `CONSTRAINT_CATALOG`, `CONSTRAINT_SCHEMA`, `CONSTRAINT_NAME`, `TABLE_CATALOG`, `TABLE_SCHEMA`, `TABLE_NAME`, `CONSTRAINT_TYPE`, `IS_DEFERRABLE`, `INITIALLY_DEFERRED` | Primary keys, unique indexes/constraints, foreign keys, and explicit checks; both deferral fields are `NO` |
| `INFORMATION_SCHEMA.KEY_COLUMN_USAGE` | `CONSTRAINT_CATALOG`, `CONSTRAINT_SCHEMA`, `CONSTRAINT_NAME`, `TABLE_CATALOG`, `TABLE_SCHEMA`, `TABLE_NAME`, `COLUMN_NAME`, `ORDINAL_POSITION` | One row per primary, unique, or foreign-key column, in constraint order |
| `INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS` | `CONSTRAINT_CATALOG`, `CONSTRAINT_SCHEMA`, `CONSTRAINT_NAME`, `UNIQUE_CONSTRAINT_CATALOG`, `UNIQUE_CONSTRAINT_SCHEMA`, `UNIQUE_CONSTRAINT_NAME`, `MATCH_OPTION`, `UPDATE_RULE`, `DELETE_RULE` | Foreign keys with the referenced key identity; `MATCH_OPTION = 'NONE'`, `UPDATE_RULE = 'RESTRICT'`, delete rule `RESTRICT`, `CASCADE`, `NO ACTION`, `SET NULL` or `SET DEFAULT` |
| `INFORMATION_SCHEMA.CHECK_CONSTRAINTS` | `CONSTRAINT_CATALOG`, `CONSTRAINT_SCHEMA`, `CONSTRAINT_NAME`, `CHECK_CLAUSE` | Explicit checks; `CHECK_CLAUSE` is the persisted canonical predicate (see [Persisted definitions are canonical](#persisted-definitions-are-canonical)) |
| `COHESION_SCHEMA.INDEXES` | `TABLE_CATALOG`, `TABLE_SCHEMA`, `TABLE_NAME`, `INDEX_NAME`, `COLUMN_NAME`, `ORDINAL_POSITION`, `IS_UNIQUE`, `IS_PRIMARY_KEY` | Cohesion extension: one row per index key column |
| `COHESION_SCHEMA.OBJECT_OWNERSHIP` | `TABLE_CATALOG`, `TABLE_SCHEMA`, `TABLE_NAME`, `OBJECT_TYPE`, `OBJECT_NAME`, `OWNER`, `OWNING_SCHEMA` | Cohesion extension: one row per table, view or index; `OWNER` is `Adhoc` or `Schema` |
| `COHESION_SCHEMA.FUNCTIONS` | `FUNCTION_NAME`, `FUNCTION_KIND`, `PARAMETER_TYPES`, `PARAMETER_COUNT`, `RETURN_TYPE`, `VOLATILITY`, `NULL_BEHAVIOR`, `IS_BUILT_IN`, `IS_SQL_DEFINED` | Cohesion extension (E2): one row per function overload of the engine's catalog, built-ins first in registration order, then one row per special form. `FUNCTION_KIND` is `SCALAR`, `AGGREGATE` or `SPECIAL FORM`; `PARAMETER_TYPES` lists the parameter types (`TEXT ...` for a variadic tail, `*` for an aggregate called as `name(*)`, empty for none); `VOLATILITY` is `IMMUTABLE`, `STABLE` or `VOLATILE` for a scalar and NULL for an aggregate, which is never folded or admitted in a CHECK; `NULL_BEHAVIOR` is `RETURNS NULL ON NULL INPUT` or `CALLED ON NULL INPUT`. A special form's type, count, volatility and NULL-behavior columns are NULL. Native rows are frozen at engine build; SQL-defined rows come from the statement catalog image |
| `COHESION_SCHEMA.TYPES` | `TYPE_NAME`, `BASE_TYPE`, `IS_DOMAIN`, `IS_SQL_DEFINED` | Native types and domains plus SQL domains in the catalog image |
| `COHESION_SCHEMA.DOMAINS` | `DOMAIN_NAME`, `BASE_TYPE`, `IS_SQL_DEFINED` | Native domains and SQL domains in the catalog image |

Identifier, descriptive, expression, and `YES`/`NO` columns have shared type
`String`. Ordinals, lengths, precision, radix, and scale have shared type `Int64`;
their values are nonnegative, with one-based ordinals. `IS_NULLABLE`,
`IS_DEFERRABLE`, `INITIALLY_DEFERRED`, `IS_UNIQUE`, and `IS_PRIMARY_KEY` use
strings `YES`/`NO`. `OBJECT_TYPE` is `TABLE` or `INDEX`; `OWNING_SCHEMA` is the
compiled schema name, distinct from the SQL namespace in `TABLE_SCHEMA`, and is
null for ad-hoc objects. Every non-null catalog-name column names the current
database.

`DATA_TYPE` uses canonical names for catalog type identities; it does not retain
the original alias spelling. For example, `INT`/`INTEGER` report `INTEGER`,
`DECIMAL`/`NUMERIC` report `NUMERIC`, and `CHAR` reports `CHARACTER` with its declared length; `VARCHAR`/`TEXT` report
`CHARACTER VARYING`. The remaining canonical names are `BOOLEAN`, `TINYINT`,
`SMALLINT`, `BIGINT`, `REAL`, `DOUBLE PRECISION`, `BINARY VARYING`, `DATE`,
`TIME`, `TIMESTAMP`, `TIMESTAMP WITH TIME ZONE`, `INTERVAL`, `UUID`, `JSON`, and
`JSONB`. Type parameters appear separately where catalog metadata retains them.
Unknown or inapplicable values are null; `CHARACTER_OCTET_LENGTH` is null because
the catalog stores no character-set byte bound. `COLUMN_DEFAULT` is the persisted
canonical expression as declared, including escaped quotes for string defaults (`DEFAULT
'it''s'` reports `'it''s'`, `DEFAULT +5` reports `5`), or null when absent.

Projection, aliases, parameters, `WHERE`, `ORDER BY`, `DISTINCT`, aggregation,
`LIMIT`, and `OFFSET` follow the engine's existing SELECT surface.
The supported subsets above apply equally to these relations. A client
can issue, for example:

```sql
SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME = 'orders'
ORDER BY ORDINAL_POSITION;
```

All relations are read-only. DML and supported table/index DDL targeting them,
including colliding `CREATE TABLE` statements and `IF [NOT] EXISTS` forms, fail
with `System view '<SCHEMA>.<VIEW>' is read-only.` The wire error code is
`ExecutionFailure`. Persisted user views execute through the same read-only relation path (#1124).
The snapshot rules, ISO-subset limitations, and the rationale for the Cohesion
extension relations are recorded in the
[SQL engine design](../../Assimalign.Cohesion.Database.Sql/docs/DESIGN.md#virtual-system-relations-c1).

## Temporal operators (#1120)

TIMESTAMP and TIMESTAMPTZ support +/- day-time INTERVAL and subtraction from
either timestamp type to produce INTERVAL. DATE +/- INTERVAL produces TIMESTAMP;
DATE +/- integer produces DATE; DATE - DATE produces INTEGER days. INTERVAL supports
+/- INTERVAL and unary minus. Multiplication, division and remainder over temporal
operands are unsupported, including INTERVAL times a number. Literal, column and
parameter operands behave alike. TIMESTAMPTZ arithmetic checks range on the UTC
instant; it preserves the written offset when representable, otherwise uses +00:00.
Every other temporal arithmetic pair fails planning (CS02003), including empty
inputs; values outside years 1..9999 fail evaluation with CS02002.

DATE compares with TIMESTAMP/TIMESTAMPTZ at midnight; a zone-less TIMESTAMP reads
as +00:00 when meeting TIMESTAMPTZ, using #1110's normalization rule. Equal temporal
values have equal hashes in DISTINCT and GROUP BY. CASE/COALESCE promote calendar
alternatives to TIMESTAMP, or to TIMESTAMPTZ when an instant is an alternative.
Index bounds use the existing coercion check; a stable clock-derived bound uses
the same captured instant as residual predicates and projections.

## Expressions

Parameters follow the [shared parameter contract](../../Assimalign.Cohesion.Database.Language/docs/DESIGN.md#parameter-contract-1102).
`$name`, `@name`, `$_x`, `$1` and `@1` are accepted. The name is an identifier or
only ASCII digits; a bare sigil, `$1a`, `$١`, `@١` or `$1١` is CS00003 at the whole
reference. Sigils are interchangeable and caller
keys may have one leading sigil. Matching is ordinal and case-sensitive, regardless
of the caller dictionary's comparer. `$01` binds key `01`, not key `1`. Unused keys
are ignored; normalized duplicates fail with CS01023, including on BEGIN, COMMIT
and ROLLBACK, before transaction state changes. Unbound references fail
with CS01020 before any row is read or written, including empty inputs. SQL
GROUP BY compares parameter references by bare name. Operand `?` and `:name`
report one CS00003 at their complete span and name the supported forms; a trailing
stray `?` keeps its generic message. SQL `::` reports CK00001 naming CAST(x AS type).
ParameterConformanceTests, ParameterBindingTests and SqlParameterWireTests cover
these rules without changing SQL request payloads.

Precedence, low to high: `OR` < `AND` < `NOT` < `IS` tests and distinct
predicates < comparison (`=`, `<>`, `<`, `>`, `<=`, `>=`) < `[NOT] BETWEEN`,
`[NOT] IN`, `[NOT] LIKE` < `ESCAPE` < concatenation (`||`) < additive (`+`, `-`)
< multiplicative (`*`, `/`, `%`) < `COLLATE` < unary signs (`-`, `+`) < primary.
This follows PostgreSQL `src/backend/parser/gram.y:847-853,904-910`.
Compared with the earlier ladder, IS moves below comparison, BETWEEN/IN/LIKE
move above comparison, LIKE pattern and ESCAPE operands include concatenation,
concatenation moves below addition, and the lower BETWEEN bound admits restricted
comparison/distinct expressions as PostgreSQL b_expr does. Thus `a = b IS TRUE` tests `(a = b)`,
`a = b LIKE c` compares `a` with `(b LIKE c)`, `a IN (1) = TRUE` compares
the IN result, and `a IS DISTINCT FROM b = c` compares `a` with `(b = c)`.
Completed postfix tests can be followed by another operator (`a IS NULL = b`).
Binary predicates and comparisons do not chain at their own rung without
parentheses. Completed IN/NOT IN forms, with a list or subquery, can be followed by
another predicate: `1 IN (1) IN (TRUE)`, `1 IN (SELECT 1) IN (TRUE)` and
`1 NOT IN (SELECT 2) BETWEEN FALSE AND TRUE` operate on the first Boolean result
(gram.y:15632-15668). Unary signs bind above COLLATE:
`-a COLLATE binary` means `(-a) COLLATE binary`. NOT stays below predicates even
when its operand starts with EXISTS: `NOT EXISTS (q) IS TRUE` means
`NOT ((EXISTS (q)) IS TRUE)`. Renderer parentheses remain conservative: comparison operands and
IS operands retain their earlier grouping, and LIKE pattern/ESCAPE concatenations
remain parenthesized. Mixed arithmetic/concatenation trees may need additional
parentheses under the new ladder: `(a || b) + c` keeps its left grouping rather
than rendering the formerly ambiguous `a || b + c`. Format 26 gates this change
along with the newly storable predicates. Executable primary forms include literals, parameters,
column references, supported function calls, simple/searched `CASE`, and
parenthesized expressions and `CAST` within the conversion contract below.
Correlated and uncorrelated scalar subqueries and subquery predicates execute within the contract above.
SQL aggregates follow the grouping and aggregate contract below. Arithmetic result
types and the `CS02001`/`CS02002`/`CS02003` faults follow the arithmetic
contract above.
`IS [NOT] DISTINCT FROM`, `IS [NOT] TRUE/FALSE/UNKNOWN`, and `[NOT] LIKE ... ESCAPE`
execute as specified below. An expression nests at most 256
levels by default (configurable per engine within 32..4096), and its parentheses at
most as many pairs; a chain of `AND` (or `OR`) terms is one level however long, and
deeper text reports `CS00006` (see [Expression nesting limit](#expression-nesting-limit-1151)).

`~` is recognized but not part of the dialect, and is rejected at parse time with
one `CK00001` at the operator (#1101). In prefix position (`~a`, bitwise NOT)
the message names the prefix `~` operator. That covers every operand position,
including a rule that takes a primary directly, such as a `LIKE` pattern
(`LIKE ~'x'`) or a column `DEFAULT ~1`, and a run of prefix operators (`~ ~a`) is
one diagnostic. In infix position (`a ~ 'x'`, PostgreSQL's regular-expression
match) it names the infix operator as written. The infix form is recognized
wherever an additive operator may follow an operand, and after a predicate that
completes on its own (`a IS NULL ~ 'x'`, `a IN (1, 2) ~ 'x'`, `a LIKE 'x' ~ 'y'`,
`BETWEEN 1 AND 2 ~ 3`). PostgreSQL's operators built on `~` are one operator each:
`~*`, `!~` and `!~*` (regular-expression match) and `~~`, `~~*`, `!~~` and `!~~*`
(LIKE match). `~` used to parse as bitwise NOT, and the evaluator returned NULL
for a NULL operand and threw per row otherwise. So `SELECT ~NULL;` and `~` over an
empty table succeeded, and an infix `~` fell to the leftover-token check with a
generic message. None of them executes now, on either session seam or over the
wire.

## Distinct predicates, Boolean tests and LIKE ESCAPE (#1111)

`a IS DISTINCT FROM b` returns TRUE when exactly one operand is NULL, FALSE when
both are NULL, and otherwise the negation of equality. `IS NOT DISTINCT FROM`
is its complement. Both always return a non-NULL BOOLEAN. Non-NULL operands use
the same SqlValueComparer and effective collation as `=`: incompatible types
raise the same error, NaN equals NaN, and `'a' COLLATE case_insensitive IS NOT
DISTINCT FROM 'A'` is TRUE. These predicates use scans in T1, including when an
index exists; they are not extracted as seek comparisons.

| Input | IS TRUE | IS NOT TRUE | IS FALSE | IS NOT FALSE | IS UNKNOWN | IS NOT UNKNOWN |
|---|---|---|---|---|---|---|
| TRUE | TRUE | FALSE | FALSE | TRUE | FALSE | TRUE |
| FALSE | FALSE | TRUE | TRUE | FALSE | FALSE | TRUE |
| NULL | FALSE | TRUE | FALSE | TRUE | TRUE | FALSE |

Truth tests require BOOLEAN or NULL. A statically non-Boolean operand, including
a supplied parameter, fails before rows are read with CS02003, even on empty
tables. All predicates participate in projections, WHERE, JOIN ON, HAVING, CASE,
ORDER BY, GROUP BY and correlated subqueries. CHECK rejects only FALSE, so
`CHECK (flag IS TRUE)` rejects NULL while `CHECK (flag)` accepts NULL.

`value [NOT] LIKE pattern [ESCAPE escape]` supports literal, parameter, column
and concatenated patterns and escapes. `%` matches any run, `_` one Unicode
scalar, and an escape quotes the next scalar literally. The escape is read
before wildcard recognition, even when the escape itself is `%` or `_`.
`'50%' LIKE '50!%' ESCAPE '!'` is TRUE, whereas `'500'` does not match it.
For valid patterns, NULL in any operand gives UNKNOWN; NOT LIKE complements the Boolean result.

The default escape is backslash and `ESCAPE ''` disables escaping, following
PostgreSQL and OQL. These intentionally depart from ISO's lack of an implicit
escape and refusal of an empty escape. An escape may precede any scalar (including
an ordinary letter), also following PostgreSQL. A nonempty escape must contain
exactly one Unicode scalar; a longer string or trailing pattern escape raises
CS02034 (SQLSTATE 22025), naming ESCAPE. Constant and supplied-parameter
patterns/escapes, including their concatenations, are validated during binding,
before any row, and compiled once. Dynamic operands are validated when evaluated.
A NULL escape is checked before escape-length validation. Eager constant escape
validation is a deliberate divergence from PostgreSQL: PostgreSQL reports a
trailing escape only when matching reaches it, and a NULL operand returns NULL
without matching (like_match.c:162-168,247-252). Here invalid fixed patterns fail
during binding even for a NULL matched value; dynamic NULL operands still give NULL.

The matched value, pattern and ESCAPE operand all participate in effective collation
resolution, including an explicit COLLATE on ESCAPE. Tokenization reads raw escapes
and wildcards before collation normalization;
only literal comparisons are normalized. Wildcards advance over the original input
scalars, including combining marks and scalars whose normalized spelling expands
(like_match.c:193-221). Case folding or accent removal therefore cannot
change an escape into a wildcard or erase it. All four collations support scalar
escapes and scalar `_`; CHAR left operands keep their stored padding while CHAR
patterns use their text conversion. Invariant literals use collation-aware suffix,
prefix and middle searches with match lengths where scalar ends are independent.
An independent terminal literal after `%` checks the suffix directly, including
when the preceding text contains combining or ignored scalars.
Single-run ASCII letter/digit predicates also check isolated prefixes when a search
misses at a combining boundary. Other combining/contextual literals retain exhaustive scalar
boundaries, including reordered Thai/Lao prefixes (like_match.c:289-311).
Invariant matching has a work budget of 64 × (input UTF-16 length + 1) × (raw pattern
UTF-16 length + 1), computed with checked 64-bit arithmetic. Each search transition and boundary probe costs one unit; each
linguistic comparison/search additionally costs the sum of its span lengths (at least
one unit); successful anchors charge only through the matched prefix, and an independent
prefix failure after boundary analysis charges the literal extent. Each zero-initialized 32-bit visited-state word costs one unit, charged
before allocation. Exhaustion raises located CS05040 as an ExecutionFailure and leaves
the connection usable; LIKE and NOT LIKE both fail rather than returning an estimate.
This is a deliberate resource limit on linguistic matching (#1166). Other collations
keep their O(input × pattern) search. The iterative matcher observes the executing
statement's cancellation token inside row-time matching and has no data-dependent recursion.
Binary, case-insensitive and independent invariant literals without ignorables
keep only the last `%`. Other paths keep the first visited boundary inline per
token, expanding to at most
tokens × (input length + 1) bits when additional starts arrive.
Accent-insensitive literals map normalized ends to original scalar boundaries and
coalesce ignored-mark continuations. A final linguistic literal compares the complete
remaining text once, so a long exact match does not spend its budget probing prefixes.
Constant patterns are cached in the bound node; dynamic patterns compile per
evaluation. No Regex, reflection or runtime
expression compilation is used. PostgreSQL anchors: `like_match.c:162-168,428-453,473-476`
and `parse_expr.c:2541-2560,3085-3105`.

These forms are storable in CHECK, parenthesized DEFAULT, domain definitions,
SQL function bodies and views. Canonical CHECK text is exposed by
INFORMATION_SCHEMA.CHECK_CONSTRAINTS. Storage format 28 refuses older files;
there is no upgrade reader. No named clause is added; the profile count does not change.
ESCAPE and UNKNOWN stay contextual. ISNULL and NOTNULL are recognized in
postfix position and refused with one CK00001; explicit AS aliases and ordinary
identifier positions keep their meaning. ILIKE, NOT ILIKE, SIMILAR TO and NOT SIMILAR
TO are recognized positionally and report one CK00001; ordinary identifiers
`ilike` and `similar` retain their canonical spelling. Binary `&`, `|`, `<<`, `>>`,
`::` casts (use CAST(x AS type)), IS JSON, IS OF and IS NORMALIZED likewise report
one CK00001 at the operator. Missing FROM/comparands or ESCAPE operands remain
CS00003 at the offending or missing token. Adjacent `<-` retains CS00003.

## Literals

Names may be delimited with double quotes, including reserved words, spaces and
literal dots. The parser removes delimiters from identifier values for catalog
binding while retaining their identifier token classification. Schema/table
qualification uses a dot **outside** the delimiters: `"dbo"."order details"` is
qualified, while `"order.details"` names one table. This applies to DML target
columns, expression references, aliases and DDL table/index/constraint names.
Embedded double quotes are doubled: `"a""x"` names one identifier, `a"x`.
Before #1235, the base lexer read that adjacent spelling as column `a` followed by alias `x`.
Persisted base view text containing it can rebind differently or fail on reopen; no stored-text
migration is provided. Recreate an affected view with `"a" "x"` or `"a" AS "x"` to retain the
column/alias interpretation. Whitespace between delimited identifiers remains significant.

| Form | Examples | AST literal type |
|---|---|---|
| National string | `N'héllo'` (adjacent prefix; canonical output `'héllo'`) | `String` |
| String | `'it''s'` (doubled-quote escape) | `String` |
| Integer | `42` | `Integer` |
| Float | `3.14`, `.5`, `1e10` | `Float` |
| Date | `DATE '2024-01-01'`; exactly yyyy-MM-dd | `Date` (DateOnly) |
| Time | `TIME [WITHOUT TIME ZONE] '10:30[:ss[.fffffff]]'`; canonical `TIME '10:30:00'` | `Time` (TimeOnly) |
| Timestamp | `TIMESTAMP [WITHOUT TIME ZONE] '2024-01-01[ T10:30[:ss[.fffffff]]]'`; one space or T separates date and time; canonical `TIMESTAMP '2024-01-01 10:30:00'` | `Timestamp` (DateTime) |
| Timestamp with zone | `TIMESTAMPTZ '2024-01-01 10:30:00[Z or +/-HH:mm]'`, or `TIMESTAMP WITH TIME ZONE`; canonical seven fractional digits and explicit offset | `TimestampWithTimeZone` (DateTimeOffset) |
| Day-time interval | `INTERVAL '3' DAY`, `INTERVAL '1:30' HOUR TO MINUTE`, `INTERVAL '2 days 3 hours'`, `INTERVAL 'P1DT2H'`; canonical `INTERVAL 'PT93600S'` for the last example | `Interval` (TimeSpan) |
| Binary string | `X''`, `X'00ff'`, `x'ABCD'`; adjacent prefix, even-count hexadecimal digits; canonical `X'00FF'` | `Binary` (byte[]) |
| Boolean | `TRUE`, `FALSE` | `Boolean` |
| Null | `NULL` | `Null` |

Typed literals execute in every scalar position, including predicates, IN, CASE, VALUES,
UPDATE and literal DEFAULTs. The parser validates their text and stores canonical values;
invalid contents report CS00008 over the complete literal. A bare date, time or interval
remains a column reference. X and N prefixes must touch the quote: `X '00'` is not a literal.
X is the ISO binary string form; PostgreSQL instead treats X as a bit-string form.

Times use 00..23 hours, 00..59 minutes and seconds, and at most seven fractional digits.
TIME accepts single-digit hours (1:02), AM/PM (1 PM, 12:02 am), and a trailing
fractional dot (13:45:00.). A written zone is validated and discarded, as PostgreSQL time_in
does (date.c:1475-1510). Numeric zones accept +/-HH, +/-HHMM, +/-HHMMSS and colon-separated
hours/minutes/seconds, within +/-15:59:59. The fixed abbreviation vocabulary is Z, UT, UTC, GMT,
EST/EDT, CST/CDT, MST/MDT, PST/PDT, CET/CEST, EET/EEST and JST (case-insensitive). Other
abbreviations and named regional zones remain unsupported: no server timezone database or
date-dependent zone resolution is provided. TIMESTAMP literals reject zones. TIMESTAMPTZ accepts Z or an offset within
+/-14:00; missing offsets mean UTC. Regional named zones, BC years, infinity, epoch, today and now
are outside the grammar. `SqlLiteralText` exposes the invariant text readers publicly in
Sql.Language, for reuse by CAST. Plain-string TIME and INTERVAL assignment use the same readers
as CAST (#1422), including surrounding ASCII whitespace trimming; non-ASCII whitespace is rejected.
24:00 remains rejected because TimeOnly cannot represent the following midnight. More than
seven fractional digits remain rejected because storage preserves exact 100 ns ticks; PostgreSQL
rounds to microseconds instead. DATE and timestamp assignment retain
their input grammar through SqlTemporalText, including TIMESTAMP ignoring a written offset.

INTERVAL is day-time only. An unqualified bare signed number means seconds, as in PostgreSQL:
INTERVAL '12' and CAST('12' AS INTERVAL) both yield twelve seconds. Qualifiers DAY, HOUR, MINUTE and SECOND and descending TO
ranges are consumed greedily; multi-field values use a space after DAY and colons between
clock fields. Subordinate hours are below 24, and subordinate minutes/seconds below 60.
Only SECOND admits a fraction in qualified forms. A leading sign applies to the whole
qualified value. Unit strings accept signed decimal weeks, days, hours, minutes, seconds, milliseconds
and microseconds (including w/d/h/m/s, hr/hrs, min/mins, sec/secs, ms/msec and us/usec aliases),
once per field, following PostgreSQL datetime.c:188-250. An implicit day before an hour
unit is accepted: 1 2 hours is 1 day 02:00 (datetime.c:3748). Optional @ and final ago
are accepted; ago negates the whole interval (datetime.c:3807-3818). ISO durations use P with D and
T with H/M/S in descending order, or a standalone PnW week duration, with an optional leading sign. All durations must fit
TimeSpan exactly at 100 ns resolution; fractions have at most seven digits. Canonical
text is optional minus, PT, total seconds and S. Year/month fields in every spelling
(including YEAR TO MONTH, year/yr/mon/mons, quarter/decade/century/millennium units, 1-2,
P1Y2M, P0001-02-03T04:05:06 and the basic P00010203T040506) report CK00001;
PT1M is one minute. Field precision, temporal precision,
numeric/UUID/JSON/BYTEA typed strings and B/E/U& string prefixes also report CK00001.
TIME WITH TIME ZONE is recognized but refused: CS00005 in declarations and CAST
(see [Type names](#type-names-the-sqltypenames-table)), CK00001 as a typed literal.
INTERVAL arithmetic and unary minus on INTERVAL execute under the #1120 contract below.

Unqualified PostgreSQL clock forms accept `1 day 02:03:04`, `1 02:03:04` and
`02:03:04`, including fractional seconds. Days and clocks have independent signs:
`-1 day -02:03:04` is -93784 seconds; `-1 02:03:04` is -79016 seconds
(datetime.c:3504-3596). `hh:mm` means hours/minutes; a fraction in a two-field clock
makes it minutes/seconds (`2:03.5` is 123.5 seconds). Clock hours may exceed 23;
minutes are 0..59 and seconds 0..60, following DecodeTimeCommon
(datetime.c:2656-2740). The complete duration must fit TimeSpan, including both endpoints;
overflow is CS00008. Empty minute/second fields mean zero and a seconds fraction may omit its integer or
fractional digits (`02::04`, `02:03:.5`, `02:03:04.`), as those PostgreSQL readers allow.
The pure day-time ISO form `P1DT2H` is supported, as PostgreSQL's
DecodeISO8601Interval supports day and time designators (datetime.c:3990-4026, 4126-4138).

In-process binary row values are result-owned snapshots. Changing a returned byte array
(including the backing array of GetBytes) cannot change another output cell, later row,
function call, default-driven INSERT or stored/indexed value. Binary parameters are
snapshotted into completed results before execution returns.
Shared constants, function results, parameters, defaults and retained execution slots copy
when supplying a row value. Fresh stored payloads transfer directly; repeated uses detach
at their source. Result materialization adds no blanket binary copy.

The engine infers and evaluates an integer literal as INTEGER when it fits, otherwise BIGINT,
and a fractional or exponent
literal as an exact `System.Decimal`, whatever the target. A literal its type cannot
hold reports `CS02002` and names itself, for example
`integer literal 9223372036854775808 exceeds BIGINT.`: an integer literal above
9223372036854775807 (write `12345678901234567890.0` for a larger exact value), or
a fractional literal Decimal cannot hold exactly, with more than 28 decimal places
or beyond Decimal's range (`1e-30`, `1e40`), even when it is stored into a REAL or
DOUBLE column. Bind such approximate values as parameters. A negated integer
literal is read as one signed literal, so `-9223372036854775808` is the BIGINT
minimum. Inside `CAST` an unrepresentable literal stays a CAST failure.

## Type names (the `SqlTypeNames` table)

| SQL names | Shared type identity |
|---|---|
| `BOOLEAN`, `BOOL` | `Boolean` |
| `TINYINT` | `Int8` |
| `SMALLINT`, `INT2` | `Int16` |
| `INT`, `INTEGER`, `INT4` | `Int32` |
| `BIGINT`, `INT8` | `Int64` |
| `REAL`, `FLOAT4` | `Float32` |
| `FLOAT[(p)]` | `Float32` for p = 1..24; `Float64` for p = 25..53 or no argument |
| `FLOAT8`, `DOUBLE`, `DOUBLE PRECISION` | `Float64` |
| `DECIMAL[(p[,s])]`, `NUMERIC[(p[,s])]` | `Decimal` (single argument = precision) |
| `CHAR[(n)]`, `CHARACTER[(n)]`, `NCHAR[(n)]`, `NATIONAL CHAR[(n)]`, `NATIONAL CHARACTER[(n)]` | `Character` (bare forms have length one) |
| `VARCHAR[(n)]`, `TEXT[(n)]`, `NVARCHAR[(n)]`, `CHARACTER VARYING[(n)]`, `CHAR VARYING[(n)]`, `NCHAR VARYING[(n)]`, `NATIONAL CHAR VARYING[(n)]`, `NATIONAL CHARACTER VARYING[(n)]` | `String` |
| `BINARY[(n)]`, `VARBINARY[(n)]`, `BLOB[(n)]`, `BYTEA[(n)]`, `BINARY VARYING[(n)]` | `Binary` |
| `DATE` | `Date` |
| `TIME`, `TIME WITHOUT TIME ZONE` | `Time` |
| `TIMESTAMP`, `TIMESTAMP WITHOUT TIME ZONE`, `DATETIME`, `DATETIME2` | `DateTime` |
| `TIMESTAMPTZ`, `TIMESTAMP WITH TIME ZONE`, `DATETIMEOFFSET` | `DateTimeOffset` |
| `INTERVAL` | `TimeSpan` |
| `UUID`, `GUID`, `UNIQUEIDENTIFIER` | `Guid` |
| `JSON` / `JSONB` | `Json` / `JsonBinary` |

DDL and CAST share one contextual type-name grammar, including case, whitespace and comments
between words (`double /*x*/ precision`). No additional words are reserved. CREATE TABLE,
both column ALTER forms, function signatures and domain bases resolve built-ins at parse time;
columns expose `SqlColumnDefinition.ResolvedType`. Unknown names remain engine-resolved domains.
All 19 current INFORMATION_SCHEMA DATA_TYPE spellings parse back to the same shared type,
including CHARACTER added by #1437. The catalog stores type identity and modifiers, not aliases.

`VARCHAR(MAX)`, `NVARCHAR(MAX)` and `VARBINARY(MAX)` mean unbounded String or Binary;
MAX is accepted only on these three spellings. NCHAR, NATIONAL CHAR and NATIONAL CHARACTER
are padded Character exactly like CHAR, including DATA_TYPE CHARACTER. Adding VARYING to any
of those three names selects unpadded String exactly like VARCHAR; bare varying forms are
unbounded. NATIONAL and VARYING remain usable as unquoted table and column names.
UNIQUEIDENTIFIER is UUID/Guid and takes no arguments. DATETIME and DATETIME2 are lexical TIMESTAMP aliases: SQL Server's
DATETIME 1753 lower bound and 1/300-second rounding are not emulated. DATETIMEOFFSET is
TIMESTAMPTZ. These aliases add no Unicode storage distinction: all character spellings store Unicode.

| Rejected spelling | Diagnostic and reason; replacement |
| --- | --- |
| TIME WITH TIME ZONE | CS00005: no shared storage type; use TIME WITHOUT TIME ZONE or TIMESTAMP WITH TIME ZONE |
| BIT, BIT(n) | CS00005: PostgreSQL bit-string meaning wins under D1; use BOOLEAN for truth values |
| SMALLDATETIME | CS00005: range and minute precision cannot be honored; use TIMESTAMP |
| SERIAL, BIGSERIAL, SMALLSERIAL | CK00001: use GENERATED BY DEFAULT AS IDENTITY; these names cannot declare user domains either |
| INT[], INTEGER ARRAY (and other scalar array forms) | CK00001: arrays are unsupported; use a scalar type |
| INTERVAL field qualifiers | CK00001: field restrictions are not enforced; use bare INTERVAL |

PostgreSQL's grammar anchors the numeric, character and temporal spellings
(`src/backend/parser/gram.y:14923-15016,15081-15181`, local reference clone). Every national
character spelling has optional VARYING (`gram.y:15130-15140`).

Coercion rules are an engine concern (planner/executor); the language layer
guarantees only that declared names resolve to shared type identities so every
model orders and stores values identically.

**A name that is not in this table** (phase E3) is not an error of the language
layer: a SQL engine resolves it against its type catalog, where an application
registers domains (`SqlType.CreateDomain`, `SqlDatabaseEngineBuilder.Types`). DDL
(`CREATE TABLE`, `ALTER TABLE ADD COLUMN`) and `CAST` read a built-in name from this
table first and fall back to the engine's domains for any other name; a domain takes
no type arguments. A name neither resolves fails while planning with `CS02010`
(SQLSTATE 42704), the same over an empty table as over a populated one. A column of
a domain stores its base type and keeps the domain's name in the catalog; every
write that assigns the column, its INSERT default included, and every `CAST` to the
domain applies the base type's length, precision and scale, then evaluates the
domain's check, of which only FALSE refuses the value (`DOMAIN constraint 'email' on
'dbo.contacts' was violated.`, a `SqlConstraintViolationException` of kind `DOMAIN`,
which a `CAST` raises too, with an empty table). Plain columns and domain base types
use the same bounds, described next. A native domain's name is an ASCII identifier. SQL-defined
domains and functions may use delimited names containing punctuation, such as `"positive.type:amount"`.
Function parameters, results, columns and CAST targets resolve the same domain name ignoring case.

## Type modifiers at declaration (#1438)

Every type spelling uses these ranges: CREATE TABLE, ALTER TABLE ADD COLUMN,
ALTER TABLE ALTER COLUMN, CREATE DOMAIN, CREATE FUNCTION parameters and RETURNS,
CAST, declared scripts, and typed schemas (including SDK extraction).

| Type names | Accepted arguments | No arguments |
| --- | --- | --- |
| NUMERIC, DECIMAL | Precision p = 1..28; optional scale s = 0..p; omitted scale is zero | No declared precision or scale; values must still fit System.Decimal |
| CHAR, CHARACTER, NCHAR, NATIONAL CHAR, NATIONAL CHARACTER | One length n = 1..10485760 Unicode scalar values | CHAR(1) |
| VARCHAR, TEXT, NVARCHAR and varying character spellings | One length n = 1..10485760 Unicode scalar values; MAX on VARCHAR/NVARCHAR | No declared length limit |
| BINARY, VARBINARY, BLOB, BYTEA, BINARY VARYING | One length n = 1..10485760 bytes; MAX on VARBINARY | No declared length limit |
| FLOAT | One binary precision p = 1..53: 1..24 resolves to REAL (Float32), 25..53 to DOUBLE PRECISION (Float64) | Float64 |
| TIME, TIMESTAMP, TIMESTAMPTZ, INTERVAL; DATETIME, DATETIME2, DATETIMEOFFSET and time-zone spellings | One nonnegative precision is recognized but unsupported (CK00001), including 7 | The type's normal meaning |
| INTERVAL field qualifiers | YEAR, MONTH, DAY, HOUR, MINUTE, SECOND and the ranges below are recognized but unsupported (CK00001); only SECOND accepts `(p)` | Bare INTERVAL keeps its normal meaning |
| Every other built-in type and every domain | None (INT(5), BOOLEAN(1) and domain(1) are invalid) | The type's normal meaning |

Except for the three MAX aliases, arguments are one or two unsigned integer literals within Int32; negative, fractional,
overflowing, empty or extra arguments are invalid. CHAR and CHARACTER share the length validator
with VARCHAR, but have their own padded type identity. The explicit string
ceiling follows PostgreSQL's anychar_typmodin (src/backend/utils/adt/varchar.c:33-57)
and MaxAttrSize (src/include/access/htup_details.h:620-626). The same ceiling for binary
makes explicit bounded declarations consistent; a bound does not allocate a buffer.
Unbounded VARCHAR and the other unbounded aliases remain unbounded. The numeric ceiling
is Cohesion's System.Decimal policy, rather than PostgreSQL's wider NUMERIC range
(src/backend/utils/adt/numeric.c:1332-1397). Types without modifiers follow PostgreSQL's
refusal in src/backend/parser/parse_type.c:358-365.

FLOAT follows PostgreSQL's storage selection (src/backend/parser/gram.y:14991-15016).
FLOAT(0) reports that precision must be at least 1 bit; FLOAT(54) and greater report that
precision must be less than 54 bits. Both are CS00005 and name the declaration or CAST target.
The catalog reports the resolved type; binary precision is not retained as a decimal modifier.
REAL/FLOAT4 and DOUBLE/FLOAT8 do not take arguments. Function overloads use the resolved storage
type, so FLOAT(24) is a REAL signature and FLOAT(53) is a DOUBLE signature.

**PostgreSQL difference: temporal precision is recognized, not supported.** TIME(p), TIMESTAMP(p),
TIMESTAMPTZ(p), INTERVAL(p), DATETIME(p), DATETIME2(p) and DATETIMEOFFSET(p) report CK00001, naming both the
object and precision modifier, including in CAST and declared scripts. The engine does not yet
implement fractional-second rounding and never silently ignores a precision. PostgreSQL admits
these modifiers (gram.y:15146-15181 and timestamp.c:1051-1110), and reduces precision above six
with a warning (date.c:57-81; timestamp.c:110-132). A single unsigned Int32 precision above six
is therefore also unsupported here. Malformed argument lists remain CS00005, including empty,
negative, nonnumeric and excess arguments before WITH TIME ZONE. Declarations and CAST report
only the modifier error for those lists; the temporal suffix never adds an unsupported-WITH
diagnostic. A well-formed precision still reports CK00001 with or without that suffix. Typed declarations
retain UnsupportedType and SDK diagnostics retain COHDBSDK106, with CK00001 in their detail.
Every temporal precision, including 7, remains CK00001; #1509 is the rounding follow-up.

**PostgreSQL difference: interval field qualifiers are recognized but unsupported.** The complete
recognized set is YEAR, MONTH, DAY, HOUR, MINUTE, SECOND, YEAR TO MONTH, DAY TO HOUR,
DAY TO MINUTE, DAY TO SECOND, HOUR TO MINUTE, HOUR TO SECOND, and MINUTE TO SECOND.
SECOND, including the last field of a range, may carry one unsigned Int32 precision `(p)`.
PostgreSQL accepts these forms (`src/backend/parser/gram.y:15197-15267`); Cohesion does not yet
implement their field restriction/truncation or precision rounding. Every declaration site and
CAST therefore reports CK00001 naming the object and complete qualifier, with or without
precision. The same refusal reaches declared scripts, typed SQL names and SDK extraction.
Malformed arguments remain CS00005; invalid ranges such as YEAR TO DAY remain CS00003 in SQL.
Delimited domain names such as `"INTERVAL DAY"` remain identifiers, not field qualifiers.

Invalid modifiers produce CS00005 before execution, naming the column, domain, function
parameter or return type, or CAST target. In process the text execution API throws
DatabaseParseException; Database.Sql.Client receives ParseFailure with CS00005 and the
same object name in its message, and ConnectionUsable stays true. Malformed surrounding
statement syntax remains CS00003. Declared scripts are parsed during engine Build before
any database is opened, and preserve CS00005. Typed schema builders and compiled schema
validation use SqlSchemaValidationException with UnsupportedType and the table/column
or domain name; Sdk.Database uses COHDBSDK106 at the source location. CS03201-005 are
script receipt/ownership codes, not the typed-schema validation enum.

Raw compiled decimal columns and domains with precision but no scale normalize to scale zero
before validation and hashing, just like SQL and CAST resolution. They apply, re-apply, verify
and reopen with the same metadata as an explicit zero scale; bare decimals remain unconstrained.

The supported alteration is the full definition, for example
ALTER TABLE t ALTER COLUMN c NUMERIC(28,2). PostgreSQL's ALTER COLUMN c TYPE ...
form executes with optional COLLATE and USING (#1447). Function overload identity and invocation semantics are
unchanged apart from FLOAT(p)'s resolved storage selection.

## Declared bounds on writes (#1418)

INSERT VALUES, INSERT ... SELECT and UPDATE apply a destination column's declared
length, precision and scale. Numeric text rounds to a declared scale before
conversion to its storage type; other values are bounded after conversion.
DEFAULT validation uses the same rule, including CREATE
TABLE defaults and ADD COLUMN's existing-row backfill. Domain checks see the
bounded value. NULL remains NULL, subject to the column's nullability and the
domain's check. Comparison literals retain their comparison meaning: a bound that
would change or reject an index-seek comparand falls back to residual evaluation.
Character join probes retain the complete text, since strings of different
lengths can compare equal under the index's collation.

- **VARCHAR(n), CHAR(n), CHARACTER(n), TEXT(n):** length counts Unicode scalar
  values, so a supplementary character is one character. If a value is longer than
  n, assignment accepts it only when everything past n is an ASCII space (U+0020),
  discarding those spaces. Other excess characters fail with `CS02012`, SQLSTATE
  22001 (`value too long for type character varying(n)`). Explicit CAST truncates to
  n characters instead. PostgreSQL makes the same assignment/explicit distinction
  in `varchar()` and `bpchar()` (`src/backend/utils/adt/varchar.c:264-268`,
  `:605-647`). A bound never splits a surrogate pair. Bare VARCHAR and TEXT have no
  declared length limit.
- **CHAR padding:** CHAR and CHARACTER are the distinct shared `Character` type. A short
  value stores and returns ASCII spaces through n Unicode scalar values. Bare CHAR is CHAR(1).
  Comparison, sorting, grouping, DISTINCT, hashes, index keys, UNIQUE/primary keys and CHAR foreign
  keys ignore trailing U+0020 before applying the collation; tabs and nonbreaking spaces remain
  significant. An untyped string literal or bound parameter compared with CHAR acquires CHAR
  comparison semantics without assignment truncation. A simple CASE literal input remains TEXT,
  as PostgreSQL forces it to TEXT before comparing WHEN values (src/backend/parser/parse_expr.c:1668-1669).
  A typed VARCHAR/TEXT operand retains its
  spaces, with the CHAR side converted to text. CHAR-to-TEXT/VARCHAR casts strip trailing spaces;
  CHAR-to-CHAR casts repad to the target length. Concatenation converts CHAR to text first, so
  `CAST('a' AS CHAR(3)) || 'b'` yields `'ab'`. LENGTH ignores padding and counts Unicode scalar
  values for CHAR (TEXT/VARCHAR LENGTH also counts Unicode scalars, #1120).
  LIKE sees the stored padded left operand; its pattern is text, so a CHAR pattern is stripped.
  Thus CHAR(3) 'a' fails LIKE 'a', but matches LIKE 'a  ' and LIKE 'a%'. There is no separate
  pattern operator class or OCTET_LENGTH builtin in this dialect. The compatibility invariant
  collation's LIKE wildcard advances Unicode scalars, as do the three index-backed collations
  advance Unicode scalars. PostgreSQL's bpchar pattern
  ordering operators trim spaces too; they do not define LIKE's input conversion.
  Native scalar and aggregate CHAR arguments apply their declared lengths even when an argument
  is already CHAR, before STRICT skips a NULL row. A native domain check also receives the
  CHAR width its own parameter declares. CHECK accepts CHAR-returning functions in character
  comparisons, BETWEEN and IN. Runtime unary-sign errors report Character, as planning errors do.
  Domains over CHAR and CHAR function
  parameters/results retain and enforce their declared
  length, including after reopen. This differs from PostgreSQL function signatures, which discard
  typmods when resolving parameter/result type OIDs (functioncmds.c:114, :250, :276).
  Mixed CHAR and TEXT/VARCHAR CASE or COALESCE results use TEXT and strip CHAR branches.
  BETWEEN resolves each comparison independently (`src/backend/parser/parse_expr.c:1325-1341`),
  so `'a ' BETWEEN CAST('a' AS CHAR(3)) AND CAST('a' AS TEXT)` returns FALSE.
  IN binds candidates containing table-column references as independent comparisons, including references
  inside calls, casts and other expressions (`parse_expr.c:1163-1176,1253-1295`). For a table column
  `c CHAR(3)` containing `'a'`, `'a ' IN (c, CAST('b' AS TEXT))` therefore returns TRUE, like its
  `= OR =` expansion. Only when more than one remaining candidate has no table-column reference does IN
  choose a common character type, considering the left operand first (`:1182-1190`). The first
  non-unknown CHAR/TEXT/VARCHAR type wins: the one-way implicit-cast replacement rule in
  `parse_coerce.c:1397-1440` cannot replace any of these with another, because
  `src/include/catalog/pg_cast.dat:313-325` permits implicit casts in both directions. Untyped string
  literals/statement parameters do not set the type; all-unknown inputs use TEXT (`parse_coerce.c:1444-1456`).
  SQL function parameters and domain VALUE are typed values in that common group, including when
  nested inside calls or other expressions. PostgreSQL binds them as `Param`
  (`src/backend/executor/functions.c:491-505`) and `CoerceToDomainValue`
  (`src/backend/commands/typecmds.c:3603-3618`), neither a table Var. Consequently
  `'a ' IN (CAST('b' AS TEXT), p)` returns FALSE for a CHAR parameter `p` containing `'a'`.
  A domain `CHECK ('a ' IN (text_b(), VALUE))`, where `text_b()` returns TEXT `'b'`, rejects
  `'a'` for the same reason: the common TEXT comparison sees trimmed CHAR `'a'`.
  Domain CHECK rejects FALSE and accepts UNKNOWN (`execExprInterp.c:4498-4508`).
  Thus `'a ' IN (CAST('b' AS CHAR(3)), CAST('a' AS TEXT))` returns TRUE; reversing those candidates
  returns FALSE. VARCHAR first preserves significant text spaces too. A CHAR left operand wins
  before either: `CAST('a' AS CHAR(3)) IN ('a ', CAST('b' AS TEXT))` returns TRUE. Common coercion
  applies no operand's declared length (`parse_coerce.c:1583-1585`); CHAR equality ignores trailing
  spaces on both sides (`varchar.c:746-776`), while TEXT equality retains them (`varlena.c:1429-1480`).
  ALTER COLUMN validates every CHAR length change, widening included, and refuses changes that
  would repad existing non-null values. An unchanged length can change nullability when existing
  values satisfy it. Empty or all-null columns can change length. Table rewrites remain deferred
  to #1447; both rewrite refusals name the column and direct callers to an explicit table-copy
  migration. Code-first validation on populated tables runs on the provisioning worker.
  Branch string literals retain this dialect's TEXT identity, unlike PostgreSQL's unknown literals.
  CHARACTER VARYING is accepted as VARCHAR; bpchar, :: casts and character typed-string prefixes
  remain excluded. See [Type names](#type-names-the-sqltypenames-table) and [Literals](#literals).
  The overflow code is CS02012/22001; its shared message still names character varying(n).
  PostgreSQL anchors: varchar.c:130 (input), :219 (padded output), :271 (explicit coercion),
  :673-712 (trimmed LENGTH versus padded octet length), :746, :912, :933, :991 (comparison,
  sorting and hashing), :1112-1119 (pattern ordering); pg_cast.dat:318-325 and pg_proc.dat:1178
  (trimmed text conversion), pg_proc.dat:3669-3670 (bpcharlike uses textlike),
  gram.y:15114-15119 (bare CHAR defaults to one). Paths are under PostgreSQL src/backend/utils/adt,
  src/include/catalog, src/backend/commands and src/backend/parser respectively.
- **NUMERIC(p,s), DECIMAL(p,s):** round to s fractional digits, with midpoint ties
  away from zero, then reject a rounded magnitude of `10^(p-s)` or more with
  `CS02002`, SQLSTATE 22003 (`numeric field overflow`). NUMERIC(p) and DECIMAL(p)
  mean scale zero. For NUMERIC(3,2), `1.235` becomes `1.24`, `-1.235` becomes `-1.24`,
  and `9.995` fails because it rounds to `10.00`. CAST uses the same numeric rule.
  Text is rounded directly from all its digits before conversion to `System.Decimal`,
  so `'1.23499999999999999999999999995'` becomes `1.23` in NUMERIC(4,2), and
  `'9.94999999999999999999999999999'` becomes `9.9` in NUMERIC(2,1). Only the rounded
  value must fit Decimal exactly; rounding to Decimal's capacity first would change
  both answers. This applies equally to quoted literals, string parameters,
  text-source INSERT ... SELECT, UPDATE, CAST, DEFAULT and ADD COLUMN backfill,
  for plain columns and domain base types.
  PostgreSQL rounds before checking precision in `apply_typmod`
  (`src/backend/utils/adt/numeric.c:7693-7750`), with midpoint handling in
  `round_var` at `:11720-11748`. Bare NUMERIC/DECIMAL requires text to fit the existing
  `System.Decimal` representation exactly, without rounding. An unrepresentable
  unscaled value, or a rounded value that still cannot fit Decimal exactly, fails
  with `CS02002` too. Numeric text follows CAST's grammar: surrounding whitespace
  and a leading `+` or `-` are accepted; exponent notation such as `'1.5e2'`,
  grouping, and a decimal point without digits on both sides are rejected.
  Unquoted SQL numeric literals retain their separate lexical rules.
  Declared scale does not promise display padding or arbitrary precision.
  Scale 28 needs no rounding of a value Decimal can hold. Declaration validation
  rejects precision outside 1..28 or scale outside 0..precision.

Bounds are checked before DML writes or DDL publication, so a failing statement
stores no partial rows or added column. Its code leads the `DatabaseException`
message in process and the `ExecutionFailure` message over the wire; the SQL
client's `SqlClientException` retains `ConnectionUsable = true`. Earlier successful
statements of an explicit transaction remain available to commit or roll back.
The shared rule corrects the earlier CAST, DEFAULT and domain behavior too: CAST
now truncates character values, and all four paths round NUMERIC to its scale.

## Explicit conversion (`CAST`, #1022, E3)

A target that is a built-in type name resolves through `SqlTypeNames` into
`DatabaseTypeInfo`, with the shared declaration modifier checks: every built-in
type is a valid target. Any other name is left to the engine's type catalog (a
domain, or `CS02010` while planning). Which conversions exist is the engine's
**cast registry** (phase E3), PostgreSQL's `pg_cast` (`castfunc`, `castcontext`): the
engine's own conversions and an application's casts, to and from its domains and
between built-in types the engine does not convert, are entries of the same
registry, and `CAST` applies an entry of any context. A `CAST` picks the entry by
the operand's storage type at run time, preferring a cast from the operand's domain
when the plan knows it (a column of a domain, or a `CAST` to one, under any
`COLLATE`; any other expression over a domain's value has the base type, so
`CAST(COALESCE(amount, 0) AS TEXT)` runs the base type's cast where
`CAST(amount AS TEXT)` runs the domain's), and for a domain target a cast to the
domain before one to its base type; a value that already has the target's storage
type needs no entry, and a value of a domain cast to that same domain takes none.
Then the target's length, precision and scale apply, then a domain target's check.
The built-in non-null conversions form the following 19×19 matrix. Y permits the pair;
– rejects it unless an application registers a cast. CHAR has its own identity;
TEXT covers VARCHAR and the varying national character spellings, and BYTEA covers
BINARY/VARBINARY. The shared type-name grammar owns all aliases and modifiers.

| Source \ target | BOOLEAN | TINYINT | SMALLINT | INTEGER | BIGINT | REAL | DOUBLE | NUMERIC | TEXT | BYTEA | DATE | TIME | TIMESTAMP | TIMESTAMPTZ | INTERVAL | UUID | JSON | JSONB | CHAR |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| BOOLEAN | Y | – | – | – | – | – | – | – | Y | – | – | – | – | – | – | – | – | – | Y |
| TINYINT | – | Y | Y | Y | Y | Y | Y | Y | Y | – | – | – | – | – | – | – | – | – | Y |
| SMALLINT | – | Y | Y | Y | Y | Y | Y | Y | Y | – | – | – | – | – | – | – | – | – | Y |
| INTEGER | – | Y | Y | Y | Y | Y | Y | Y | Y | – | – | – | – | – | – | – | – | – | Y |
| BIGINT | – | Y | Y | Y | Y | Y | Y | Y | Y | – | – | – | – | – | – | – | – | – | Y |
| REAL | – | Y | Y | Y | Y | Y | Y | Y | Y | – | – | – | – | – | – | – | – | – | Y |
| DOUBLE | – | Y | Y | Y | Y | Y | Y | Y | Y | – | – | – | – | – | – | – | – | – | Y |
| NUMERIC | – | Y | Y | Y | Y | Y | Y | Y | Y | – | – | – | – | – | – | – | – | – | Y |
| TEXT | Y | Y | Y | Y | Y | Y | Y | Y | Y | Y | Y | Y | Y | Y | Y | Y | Y | Y | Y |
| BYTEA | – | – | – | – | – | – | – | – | Y | Y | – | – | – | – | – | – | – | – | Y |
| DATE | – | – | – | – | – | – | – | – | Y | – | Y | – | Y | Y | – | – | – | – | Y |
| TIME | – | – | – | – | – | – | – | – | Y | – | – | Y | – | – | Y | – | – | – | Y |
| TIMESTAMP | – | – | – | – | – | – | – | – | Y | – | Y | Y | Y | Y | – | – | – | – | Y |
| TIMESTAMPTZ | – | – | – | – | – | – | – | – | Y | – | Y | Y | Y | Y | – | – | – | – | Y |
| INTERVAL | – | – | – | – | – | – | – | – | Y | – | – | Y | – | – | Y | – | – | – | Y |
| UUID | – | – | – | – | – | – | – | – | Y | – | – | – | – | – | – | Y | – | – | Y |
| JSON | – | – | – | – | – | – | – | – | Y | – | – | – | – | – | – | – | Y | Y | Y |
| JSONB | – | – | – | – | – | – | – | – | Y | – | – | – | – | – | – | – | Y | Y | Y |
| CHAR | Y | Y | Y | Y | Y | Y | Y | Y | Y | Y | Y | Y | Y | Y | Y | Y | Y | Y | Y |

Identity conversions still apply target bounds. A pair without a conversion fails
with CS02011, naming CAST and both SQL types. When the source is static (a
column, typed literal, nested CAST, predicate, resolved call, scalar subquery, or a
CASE, COALESCE, unary-sign or arithmetic result the binder can infer), planning rejects
the pair before reading any rows, including empty input and NULL-valued columns.
This uses the existing binder inference (PostgreSQL src/backend/parser/parse_expr.c:2764-2788).
Parameter-dependent result types are checked when evaluated, including CASE result arms
and polymorphic calls; parameters used only in CASE conditions leave its static result type
intact. NULLIF now executes as an evaluator form; its result participates in the same CAST signature checks (#1120).
A NULL operand returns NULL with target metadata; it does not bypass a statically
unsupported pair or invalid target. SQL integer literals are Int32 when they fit, otherwise Int64, and fractional
literals are Decimal, including exponent and leading decimal-point forms. To
supply a floating operand, cast text to REAL or DOUBLE first.

- **Contexts.** Each built-in entry carries PostgreSQL's context for the same pair
  (`src/include/catalog/pg_cast.dat`): widenings among the numbers implicit,
  narrowings and conversions to text assignment, conversions from text explicit. In
  this iteration only `CAST` consults the registry; assignment coercion and overload
  resolution keep their own rules, so a literal string still does not reach a DATE
  function parameter without a `CAST` (`day_of_year(CAST('2026-02-01' AS DATE))`).
- **Temporal input** calls the public strict literal readers in `SqlLiteralText`,
  trimming surrounding whitespace. DATE, TIME, TIMESTAMP, TIMESTAMPTZ and INTERVAL
  use the [typed-literal grammar](#literals); a TIMESTAMP input containing an offset
  is rejected. INTERVAL input is day-time only; year-month fields fail, and bare signed
  numbers such as '12' mean seconds (PostgreSQL datetime.c:3625-3657). Plain-string TIME and
  INTERVAL assignment use these same readers on INSERT, UPDATE, INSERT ... SELECT and
  DEFAULTs, including domain columns. TIME accepts single-digit hours, AM/PM, a trailing dot,
  and discarded written zones under the typed-literal grammar above. INTERVAL day-dot-clock
  input such as '1.02:03:04' fails.
  INTERVAL accepts unit/clock strings and ISO day-time durations. In particular, assigning
  '12' now stores twelve seconds rather than twelve days (#1422). Typed schema TimeSpan defaults
  render signed total seconds with a `seconds` unit, preserving the exact duration.
  All zone-dependent temporal conversions follow the
  [timestamp normalization rule](#timestamp-normalization-1110).
  TIMESTAMP to DATE/TIME extracts fields. INTERVAL to TIME requires
  `0 <= interval < 24 hours`; it never wraps. UUID input uses invariant Guid parsing.
- **Floating input and exact targets:** REAL/DOUBLE text accepts invariant
  `NumberStyles.Float` syntax without grouping, including NaN, Infinity and
  -Infinity. Finite text that overflows to infinity or nonzero text that underflows
  to zero fails. DOUBLE to REAL applies the same finite overflow and nonzero
  underflow checks; exact numeric to float rounds to the represented binary value.
  NUMERIC-to-REAL parses the exact Decimal text directly to Single, without an intermediate
  DOUBLE conversion (PostgreSQL src/backend/utils/adt/numeric.c:4691-4696).
  Float to integer requires a finite whole number in range, without rounding or
  truncation. NaN and either infinity to any integer target fail with CS02002/22003
  (src/backend/utils/adt/float.c:1267-1270). Float to NUMERIC parses its shortest round-trip text (including an
  exponent when present) exactly as Decimal before applying declared scale/precision;
  non-finite values and values Decimal cannot represent fail.
- **Character input:** surrounding whitespace is trimmed for numeric, Boolean,
  temporal, interval, UUID and hexadecimal BYTEA input. Exact numeric text uses
  `[+-]?[0-9]+(\.[0-9]+)?`; grouping, exponent notation, empty and invalid text fail.
  Integer and unscaled NUMERIC input must fit Decimal exactly; a declared NUMERIC
  scale rounds the original digits first, as described below. Boolean input is
  case-insensitive TRUE/FALSE. TEXT identity preserves whitespace; CHAR-to-TEXT
  removes trailing ASCII spaces before conversion. JSON text preserves whitespace.
- **Canonical character output:** Boolean is TRUE/FALSE; exact numbers use invariant
  decimal formatting. REAL/DOUBLE use shortest round-trip formatting, including
  NaN, Infinity, -Infinity and -0 (the zero sign is preserved). DATE is yyyy-MM-dd;
  TIME is HH:mm:ss[.f]; TIMESTAMP is yyyy-MM-dd HH:mm:ss[.f]; TIMESTAMPTZ appends
  a +/-HH:mm offset. Fractions retain up to seven significant fractional digits,
  omitting trailing zeros. INTERVAL follows PostgreSQL's postgres style: positive one
  uses `1 day`, other nonzero day counts use `days` (including `-1 days`), followed by a
  signed HH:mm:ss[.f] clock only when nonzero. Zero is `00:00:00`; negative durations
  give both day and clock negative signs. TimeSpan first normalizes mixed-sign fields:
  `2 days -02:03:04` becomes `1 day 21:56:56`. These forms round-trip through
  SqlLiteralText even at the temporal and interval extrema. PostgreSQL's emission rules
  are src/backend/utils/adt/datetime.c:4675-4694,4875-4890.
  UUID is lowercase hyphenated; BYTEA is `\x` followed by lowercase hex digits,
  including `\x` for empty bytes. `||` renders each non-null operand with this
  built-in CAST-to-TEXT formatting, so `TRUE || ''` is TRUE and
  `DATE '2024-01-01' || ''` is 2024-01-01. NULL still propagates.
- **BYTEA input:** accepts `\x` plus an even number of hex digits, or the UTF-8
  bytes of the original backslash-free text, preserving leading/trailing whitespace and
  whitespace-only payloads (PostgreSQL src/backend/utils/adt/bytea.c:230-231).
  Hex input retains its separate rules: surrounding whitespace is trimmed, its digits
  must be contiguous, and `\x` alone produces empty bytes. Invalid hex, an unpaired Unicode surrogate, any
  other backslash form and a binary length overflow fail; bytes never truncate.
- **JSON and JSONB:** casts into either validate one JSON value using
  System.Text.Json JsonDocument with MaxDepth 128; comments, trailing commas,
  extra values and malformed JSON fail. JSON retains its original text; JSONB
  stores its validated UTF-8 bytes without normalization. JSON ↔ JSONB validates
  and preserves the original spelling, property order, duplicates and whitespace.
  JSONB equality is byte equality, unlike PostgreSQL's normalized JSONB equality;
  another binary encoding would require a data migration. JSONB-to-text validates
  the stored UTF-8 JSON before decoding. JSON columns may still accept unvalidated
  text on INSERT; that assignment behavior is outside CAST. JSON-to-scalar casts
  remain outside Tier 1.
- **NULL:** returns NULL for every supported target, with the target's result
  type metadata even when every row is null or the result has no rows. Invalid
  target names/modifiers are still errors with a NULL operand.
- **Integers:** signed ranges are -128..127, -32768..32767, -2147483648..2147483647,
  and -9223372036854775808..9223372036854775807. Overflow and nonzero fractional
  parts are errors; there is no rounding, truncation, wrapping, or zero fallback.
- **Decimal:** the implementation uses `System.Decimal` (96-bit coefficient,
  scale 0..28), not arbitrary precision. Bare DECIMAL/NUMERIC has no additional
  bound. `DECIMAL(p)` means scale 0; `DECIMAL(p,s)` requires `1 <= p <= 28` and
  `0 <= s <= p`. The value rounds to s fractional digits, with midpoint ties away
  from zero, then must satisfy `abs(value) < 10^(p-s)` or it fails with `CS02002`
  (22003). Text rounds directly to the target scale before Decimal conversion,
  avoiding a second rounding caused by Decimal's own precision limit. If the rounded
  text still cannot fit Decimal exactly, or unscaled text cannot fit it exactly,
  conversion fails with the same `CS02002` code.
  The output does not promise padding to the declared scale.
- **Strings:** CHAR/CHARACTER and VARCHAR/TEXT permit an optional length
  n from 1 through 10485760; bare CHAR is CHAR(1), while bare VARCHAR/TEXT have no CAST length limit.
  Length counts Unicode scalar values. Longer results truncate at a character boundary; CHAR pads shorter results. The
  binary aliases permit the same optional length in bytes. Other targets follow the
  shared [type-modifier rules](#type-modifiers-at-declaration-1438); domains take no parameters.
- **Diagnostics and metadata:** a name that is not a built-in type and that the
  engine does not register as a domain fails while planning with `CS02010`
  (`CS00004` is retired: the parser no longer knows which names exist); invalid
  target arguments produce `CS00005`; recognized but unsupported temporal precision
  produces `CK00001`; malformed CAST syntax produces `CS00003`.
  A pair no registered cast converts is `CS02011` (SQLSTATE 42846). A conversion
  that fails on the value (a `FormatException`, `OverflowException` or
  `InvalidCastException`, the only failures the built-in casts throw, an
  application's alike) throws `DatabaseException` naming CAST and its target, with
  the original as the inner exception, surfaced as a query error over the wire.
  A built-in NUMERIC conversion's representation failure or modifier overflow
  instead carries `CS02002` (22003), after rounding to its declared scale when
  present. A cast that throws anything else, a cancellation the statement did not ask for
  included, fails with `CS05007` (`Cast TEXT AS cents failed: ...`), as a
  function does, and the connection stays usable; a `DatabaseException` it throws
  passes unchanged. A value a domain target's check refuses is the
  `SqlConstraintViolationException` of kind `DOMAIN` a write raises. The AST
  retains full `DatabaseTypeInfo`; in-process result columns expose the base
  `DatabaseType` and conservative nullability. The existing wire/client contract
  exposes the base type, with a matching boxed runtime value. Neither result
  contract exposes precision/scale fields; CHAR result metadata includes its length,
  and the client has no nullability field.

This is a deliberate ISO/IEC 9075 subset: integer conversion requires an exact
value, declared numeric scale rounds, explicit character conversion truncates,
and CHAR pads shorter results. CAST executes in DEFAULT; CHECK rejects it during planning.
CAST values in INSERT/UPDATE are converted before the existing destination-column
storage coercion; nested arithmetic therefore sees the converted numeric value.

## Builtin functions

The executable profile contains the 42 T1 scalar names below, the seven aggregate names
COUNT/SUM/AVG/MIN/MAX/STRING_AGG/LISTAGG and CAST. COALESCE, NULLIF, CASE, CAST and EXTRACT remain
evaluator special forms, outside the catalog calling convention (owner decision 66).
SUBSTRING, TRIM and POSITION grammar normalizes to calls; directional TRIM becomes
LTRIM or RTRIM. Applications and built-ins use the same reflection-free catalog.
Arity and argument-type errors report CS01006 while planning, including empty
inputs; the message names the accepted signatures. UPPER/LOWER no longer pass
non-text values through, and LENGTH no longer stringifies them.

In this table, `text` includes CHAR (trailing ASCII spaces are removed when converting
CHAR to TEXT), `integer` includes all signed widths, and `numeric` includes every
integer width, REAL, DOUBLE PRECISION and DECIMAL. Strict means NULL if any argument
is NULL. I means IMMUTABLE, S means STABLE, V means VOLATILE. Integer numeric
results widen to BIGINT unless a row explicitly says otherwise; DECIMAL and
approximate inputs otherwise keep their type. Text searches and character sets
use ordinal Unicode scalar matching.

| Function and signature | Result | NULL rule | Volatility | Origin / deviations |
| --- | --- | --- | --- | --- |
| COALESCE(value [, value ...]) | Common alternative type | First non-NULL; lazy | I | ISO / PG; permits one operand |
| NULLIF(a, b) | Type of a | NULL if equal; otherwise a | I | ISO / PG |
| UPPER(text) | TEXT | Strict | I | ISO / PG; invariant case conversion |
| LOWER(text) | TEXT | Strict | I | ISO / PG; invariant case conversion |
| LENGTH(text) | BIGINT | Strict | I | PG; Unicode scalars |
| CHAR_LENGTH(text) | BIGINT | Strict | I | ISO / PG; Unicode scalars |
| CHARACTER_LENGTH(text) | BIGINT | Strict | I | ISO / PG; Unicode scalars |
| TRIM(text [, chars]); TRIM([LEADING/TRAILING/BOTH] [chars] FROM text) | TEXT | Strict | I | ISO / PG; default ASCII space |
| LTRIM(text [, chars]) | TEXT | Strict | I | PG; default ASCII space |
| RTRIM(text [, chars]) | TEXT | Strict | I | PG; default ASCII space |
| SUBSTRING(text FROM integer [FOR integer]); SUBSTRING(text, integer [, integer]) | TEXT | Strict | I | ISO / PG; one-based, negative length fails; regex form excluded |
| REPLACE(text, from_text, to_text) | TEXT | Strict | I | PG; empty search leaves input unchanged |
| CONCAT(value [, value ...]) | TEXT | Skip NULLs; all NULLs gives empty text | I | PG; uses the existing CAST-to-TEXT formatter, including BOOLEAN TRUE/FALSE |
| CONCAT_WS(separator, value [, value ...]) | TEXT | NULL separator gives NULL; skip other NULLs | I | PG; uses the existing CAST-to-TEXT formatter, including BOOLEAN TRUE/FALSE |
| POSITION(sub IN text) | BIGINT | Strict | I | ISO / PG; one-based scalar position, zero when absent |
| STRPOS(text, sub) | BIGINT | Strict | I | PG; POSITION with reversed arguments |
| LEFT(text, integer) | TEXT | Strict | I | PG; negative count omits trailing scalars |
| RIGHT(text, integer) | TEXT | Strict | I | PG; negative count omits leading scalars |
| LPAD(text, integer [, fill]) | TEXT | Strict | I | PG; nonpositive length gives empty text; default space |
| RPAD(text, integer [, fill]) | TEXT | Strict | I | PG; nonpositive length gives empty text; default space |
| REVERSE(text) | TEXT | Strict | I | PG; reverses Unicode scalars |
| ABS(numeric) | Numeric result | Strict | I | ISO / PG; BIGINT minimum overflows |
| ROUND(numeric); ROUND(exact_numeric, integer_scale) | Numeric result | Strict | I | ISO / PG; exact halves away from zero, approximate halves to even; negative exact scale decides once from the exact discarded remainder, without division rounding (#1661) |
| FLOOR(numeric) | Numeric result | Strict | I | ISO / PG |
| CEILING(numeric) | Numeric result | Strict | I | ISO / PG |
| CEIL(numeric) | Numeric result | Strict | I | PG alias of CEILING |
| POWER(numeric, numeric) | DECIMAL for exact base/integer exponent; base approximate type for integer exponent; otherwise DOUBLE PRECISION | Strict | I | ISO / PG; deviation for owner approval: result type follows operand types, with fractional-typed exponents returning DOUBLE PRECISION even when their value is integral |
| SQRT(numeric) | DOUBLE PRECISION | Strict | I | ISO / PG; PG numeric-result deviation |
| MOD(numeric, numeric) | BIGINT for integers; DECIMAL for exact decimal; DOUBLE PRECISION for approximate | Strict | I | ISO / PG; zero divisor fails |
| SIGN(numeric) | BIGINT for every numeric input | Strict | I | PG; deviation: PG preserves NUMERIC or DOUBLE PRECISION |
| EXP(numeric) | DOUBLE PRECISION | Strict | I | ISO / PG; PG numeric-result deviation |
| LN(numeric) | DOUBLE PRECISION | Strict | I | ISO / PG; PG numeric-result deviation |
| LOG10(numeric) | DOUBLE PRECISION | Strict | I | PG; PG numeric-result deviation |
| CURRENT_DATE | DATE | Never NULL | S | ISO / PG; UTC transaction date |
| CURRENT_TIME | TIME | Never NULL | S | ISO / PG; UTC; TIME WITH TIME ZONE is unavailable |
| CURRENT_TIMESTAMP | TIMESTAMPTZ at +00:00 | Never NULL | S | ISO / PG; transaction start |
| LOCALTIME | TIME | Never NULL | S | ISO / PG; UTC transaction wall clock |
| LOCALTIMESTAMP | TIMESTAMP | Never NULL | S | ISO / PG; UTC wall clock, Kind Unspecified |
| NOW() | TIMESTAMPTZ at +00:00 | Never NULL | S | PG; CURRENT_TIMESTAMP alias |
| EXTRACT(field FROM temporal) | DECIMAL | Strict | I | ISO / PG; fields below |
| DATE_TRUNC(field_text, temporal) | Input temporal type | Strict | I | PG; DATE stays DATE, a PG deviation |
| GEN_RANDOM_UUID() | UUID version 4, per call | Never NULL | V | PG |

For an exact value and negative scale, ROUND compares the exact discarded remainder with
half the scale's power of ten, then rounds once, with ties away from zero. Thus
`ROUND(4.9999999999999999999999999999, -1)` is 0, while `ROUND(5.0, -1)` is 10
and `ROUND(-5.0, -1)` is -10. The rule applies at every supported negative scale,
including -28; division must not first round a value onto a midpoint (#1661).
Reference: PostgreSQL `src/backend/utils/adt/numeric.c:11683-11756`, `round_var`.

EXTRACT fields are YEAR, QUARTER, MONTH, WEEK (ISO week number), DAY, DOW
(Sunday zero), DOY, HOUR, MINUTE, SECOND (with fractional seconds) and EPOCH.
DATE admits only calendar fields and EPOCH; TIME admits hour/minute/second/epoch;
day-time INTERVAL additionally admits day. Field names are case-insensitive; unknown or
incompatible constant fields fail planning for every temporal type. INTERVAL extraction
uses the normalized TimeSpan components: `INTERVAL '36' HOUR` has DAY 1 and HOUR 12.
This departs from PostgreSQL's separate day/time components (DAY 0, HOUR 36).
EPOCH always returns the full duration in seconds.
DATE_TRUNC accepts year/quarter/month/week/day/hour/minute/second/millisecond/
microsecond; TIME and INTERVAL admit day and finer units. Invalid field values
fail with CS01006. There is no session time zone.

The five bare CURRENT_*/LOCAL* forms require no parentheses: empty parentheses
report CS00003. A precision argument on CURRENT_TIME, CURRENT_TIMESTAMP,
LOCALTIME or LOCALTIMESTAMP reports CK00001 naming the precision argument,
including in DEFAULT grammar. NOW is written NOW(). Missing separators, unknown
EXTRACT fields and missing closing parentheses report CS00003.

All six clock forms read the instant captured at transaction start: BEGIN or
BeginTransactionAsync for explicit transactions, statement start for auto-commit.
Planning a seek and evaluating every row, child query and later statement in the
transaction use the same instant. COMMIT/ROLLBACK ends that lifetime. The engine's
join pushdown uses this same capture: NOW(), CURRENT_TIMESTAMP and the other four
clock forms are statement-stable, so a plan-time seek bound agrees with its run-time
input filter even if the wall clock advances. Its existing internal TimeProvider
seam remains internal (owner decision 42); DEFAULT
expression defaults use this carrier for omitted columns, explicit DEFAULT and referential SET DEFAULT (#1121). Stable ADD COLUMN defaults freeze the ALTER instant for missing fields; volatile defaults require an empty table.

Numeric domain errors, nonfinite finite-input results, negative SUBSTRING lengths,
numeric overflow and temporal range overflow use CS02002; MOD by zero uses
CS02001. String-producing scalars limit results and Unicode work buffers to 1,048,576
UTF-16 code units (2 MiB of character data). LPAD/RPAD conservatively budget two
UTF-16 units per requested Unicode scalar; REPLACE checks its expanded length and
CONCAT/CONCAT_WS check binary-to-hex expansion and joined length before allocation.
UPPER/LOWER use the same cap; LENGTH/CHAR_LENGTH count without allocating a rune buffer. Oversized requests raise
CS02002 (requested length too large); long loops observe statement cancellation. CHECK accepts only immutable functions, so clocks and random UUIDs are
refused before mutation. Volatile calls never become seek bounds.

Every scalar position uses these rules, including JOIN ON, grouping/HAVING,
ordering and INSERT/UPDATE expressions. LENGTH, CHAR_LENGTH, CHARACTER_LENGTH,
SUBSTRING, LEFT, RIGHT, LPAD, RPAD, REVERSE, POSITION and STRPOS count Unicode
scalar values and never split a surrogate pair. CAST/VARCHAR(n) bounds use the
same character unit.

The newly executable names and new T1 names are reserved for built-ins at every
arity: TRIM, LTRIM, RTRIM, SUBSTRING, REPLACE, CONCAT, CONCAT_WS, POSITION, STRPOS,
LEFT, RIGHT, LPAD, RPAD, REVERSE, CHAR_LENGTH, CHARACTER_LENGTH, ROUND, FLOOR,
CEILING, CEIL, POWER, SQRT, MOD, SIGN, EXP, LN, LOG10, CURRENT_DATE, CURRENT_TIME,
CURRENT_TIMESTAMP, LOCALTIME, LOCALTIMESTAMP, NOW, DATE_TRUNC, GEN_RANDOM_UUID.
NULLIF and EXTRACT remain reserved special forms. Existing application overloads
of UPPER/LOWER/LENGTH/ABS at arities the standard library does not take remain
possible. Function words still bind as ordinary identifiers without a call;
unquoted niladic names read their values and need quoting to name a column.
Existing join-keyword restrictions on LEFT/RIGHT aliases remain. Canonical expression rendering quotes BOTH/LEADING/TRAILING when used as identifiers, preserving TRIM round trips. Applying an
existing compiled schema with newly quoted function-named columns is a no-op.

COUNT/SUM/AVG/MIN/MAX keep their existing signatures: COUNT(*)/COUNT(value),
SUM(numeric), AVG(numeric), MIN(value), MAX(value). Only COUNT accepts `*`.
Window functions remain recognized-unsupported (CK00001). LOG(base, x), trig,
regex functions, TO_CHAR and other unknown names remain CS01016.

PostgreSQL implementation references (local reference clone):
`src/backend/parser/gram.y:16249-16371,17203` (special grammar),
`src/backend/utils/adt/varlena.c:586-678,916-940,4695,4735` (scalar string semantics),
and `src/backend/utils/adt/timestamp.c:1619-1621,1677,1691` (transaction clocks).
No reference implementation code is copied.

**Registered functions.** An application function has a name (an identifier), parameter
types, a result type, a volatility and a NULL rule. A name and parameter-type list is
registered once, and a name is either scalar or aggregate. A built-in cannot be replaced: an
overload of a built-in's name is accepted only for a number of arguments the built-in does
not take (`upper(TEXT, BIGINT)` beside `UPPER(TEXT)`). The new T1 built-in names above, a name in the recognized-unsupported vocabulary (`RANK`, `LEAD`, ...), a type name (`INT`, `DATE`), `ANY`,
`SOME`, `LOCALTIME` and `LOCALTIMESTAMP` cannot be registered. An argument converts to its
parameter's type along INT8 → INT16 → INT32 → INT64 → NUMERIC → DOUBLE, or REAL → DOUBLE;
nothing converts from text, so a DATE or UUID parameter takes a column or a parameter of
its type, not a string literal. Inside a grouping, a grouping key or an aggregate result
has its type for that choice (`describe(COUNT(*))` calls `describe(BIGINT)`). A strict
function (the default) returns NULL over a NULL argument without being called, and a strict
aggregate skips the row. An `IMMUTABLE` call whose arguments are constants or parameters is
computed once, while the statement is planned. Immutable constant calls in shared SQL function/domain
bodies, table CHECKs and DEFAULT expressions can also fold when the definition is bound, using that
operation's cancellation token. Remaining calls use the evaluating statement's token. A `CHECK` admits only `IMMUTABLE` functions,
so a function registered with the default `VOLATILE` volatility is refused there, naming its
volatility; one returning BOOLEAN is a predicate by itself, and DDL refuses a CHECK that
compares an application function's result with a value of a type it does not compare with,
or uses one that is not a number in arithmetic. What a function throws fails the statement
as `CS05007`, which names it, and so does a result of another type than the function
declares (a type that widens to the declared one is converted); the statement writes
nothing and an explicit transaction stays usable, as for every coded failure. A sign over a
constant is itself a constant, so `ABS(-5)` is computed once too. An application function's
`OperationCanceledException` is also `CS05007` unless the statement's own token is
canceled, regardless of the exception's token. This applies to every aggregate phase and
to engine/SQL domain checks and application calls inside SQL functions, including LIMIT/OFFSET,
seek-bound evaluation and constant folding during planning. Actual cancellation stops folding
immediately; it is never deferred and retried as a function failure. The coded error
leaves the wire connection usable; actual statement cancellation retains its cancellation behavior.

A statement waiting for another statement to bind the same shared function body, collation variant
or table definition can cancel its own wait. It does not cancel the computing statement. Different
entries and unrelated metadata queries proceed independently. Canceled or failed binding attempts
are not cached; a subsequent caller retries using its own token. Definition DDL remains committed
if its following body preparation fails, and later statements prepare only the entries they need.

**A stored CHECK whose function is gone.** A CHECK stores canonical SQL and each call's
resolved function identity. A later engine build may omit that exact signature, change
its volatility from Immutable, or register a result type that no longer fits where the
CHECK uses it (a BOOLEAN predicate, a comparison). Another overload never replaces it.
The database still opens and
its reads proceed; every write that would evaluate the CHECK (an `INSERT`, an `UPDATE`, or a
DDL backfill over existing rows) fails with `CS01009`, naming the constraint, the table and
the call as the CHECK makes it, on a session or connection that stays usable. A `DELETE`, an
unrelated `DROP COLUMN` and `ALTER TABLE ... DROP CONSTRAINT` still run, and dropping the
constraint lets the table's writes through again. An engine that declares the database (its
builder's `AddDatabase`) fails its build with the same code instead, before it accepts work:

```text
CS01009: CHECK constraint 'ck_email' on table 'dbo.customers' calls function 'is_email(TEXT)', which this engine does not register, so the constraint cannot be evaluated and the write is refused. Register the function on the engine's builder (SqlDatabaseEngineBuilder.Functions), or drop the constraint.
```

Native, built-in and SQL calls all reload by recorded identity. Special forms such as
`COALESCE` have no function identity; its wrong arity is the #1189 case under
[Persisted definitions are canonical](#persisted-definitions-are-canonical), which still
fails the open.

A call found in neither the engine catalog nor the profile's executable and recognized-
unsupported lists fails at plan time with `CS01016`, `Unknown function '<name>'.`
before any row is read (#1068). Recognized unsupported functions fail at parse time with
exactly one `CK00001` naming the function, even over an empty table (#1103):
the nine window functions ROW_NUMBER, RANK, DENSE_RANK, LEAD, LAG, FIRST_VALUE,
LAST_VALUE, NTH_VALUE and NTILE. Window rejection also works when these names
lex as identifiers. An OVER clause keeps its existing clause diagnostic.

The ISO datetime value functions execute as described in the T1 table above.
Their unqualified, unquoted spellings never bind a column; quote an identifier
to read a same-named column. Schema rendering quotes both executable function
names and recognized-unsupported vocabulary. Existing compiled schemas reapply
without a migration solely because an identifier now needs delimiters.
`SqlLanguageConformanceTests` is the single function-coverage guard: every declared
function needs a passing live case, and a case for a retired name fails too. A feature
that starts executing a function adds its name and live case and flips its `CK00001`
pin in the same change.

## Diagnostics

| Code | Severity | Meaning |
|---|---|---|
| `CK00001` | Error | Recognized clause, keyword, function, operator or type modifier is not supported by the SQL model surface, including prefix and infix `~` (#1101), temporal precision and INTERVAL field qualifiers in declarations and CAST (#1438); the message names the construct and, for a modifier, its declaration |
| `CS00001` | Error | Empty query text |
| `CS00002` | Error | Unknown command (recognized unsupported clauses use `CK00001`) |
| `CS00003` | Error | Malformed syntax: text after a complete statement or after its terminating `;`; a missing expression, closing token, keyword, name or `VALUES` row; an unterminated string, quoted identifier or block comment; a character outside the dialect, such as `?`, `#`, `^` or a non-ASCII digit, which binds no alias or column (#1101); a numeric literal whose exponent has no digits; an incomplete or malformed IS predicate; `NOT` after an operand without a recognized predicate; an incomplete LIKE ESCAPE; mixed or duplicate pagination clauses, incomplete FETCH, or malformed TOP; an incomplete `IF [NOT] EXISTS`; `ALTER` without `TABLE`; an unknown or incomplete `ALTER TABLE` action; and missing set operands or an unparenthesized operand window before a set operator; malformed transaction-control, JOIN, GROUP BY, HAVING, CAST, COLLATE, or constraint/DDL syntax |
| `CS00004` | — | Retired (E3): an unknown CAST target type was a parse error; a name that is not a built-in type is now resolved by the engine, and one it does not know is `CS02010` |
| `CS00005` | Error | Invalid type modifiers at any declaration or CAST: malformed arguments (including CHAR(MAX)), out-of-range length/precision/scale (including FLOAT outside 1..53 bits), arguments a type does not take, or a rejected type spelling (BIT, SMALLDATETIME, TIME WITH TIME ZONE); names the declaration and reason. Temporal precision and INTERVAL qualifiers use CK00001 |
| `CS00006` | Error | Expression nesting exceeds the limit (256 levels by default, configurable within 32..4096), or parentheses nest deeper than the limit; an `AND`/`OR` chain counts one level. The rest of the statement is not parsed. An engine also reports it for a typed request whose statement nests deeper than the engine's limit (#1151) |
| `CS00007` | Error | The statement is within the nesting limit, but the thread parsing it has too little stack left to recurse that deep; the same text parses on a thread with more stack. An engine parsing statement text reports this as `CS05004` (#1151) |
| `CS00008` | Error | Invalid DATE, TIME, TIMESTAMP, TIMESTAMPTZ, day-time INTERVAL or X binary literal content; the span covers the complete literal. Recognized unsupported forms use CK00001 |
| `CS00009` | Information | Statement does not end with `;` |
| `CS02001` | Error | Division by zero during evaluation (ISO SQLSTATE 22012) |
| `CS02002` | Error | Numeric value out of range during evaluation, store assignment, DEFAULT conversion or built-in NUMERIC CAST, including an inexact Decimal representation after any declared-scale rounding (ISO SQLSTATE 22003) |
| `CS02003` | Error | Datatype mismatch, including non-numeric signs, non-Boolean search conditions (WHERE, JOIN ON, HAVING, ON CONFLICT DO UPDATE WHERE, searched CASE WHEN and AND/OR/NOT operands), non-Boolean IS TRUE/FALSE/UNKNOWN operands, and incompatible set-operation column types; set errors name both types and the one-based position (ISO SQLSTATE 42804) |
| `CS05004` | Error | Statement too complex: parsing it or a walk over it needs more stack than the executing thread has left, which a statement within a high configured nesting limit or a thread created with a small stack can reach (ISO SQLSTATE 54001, #1151) |
| `CS01005` | Error | Column reference in a clause with no columns in scope: an `INSERT ... VALUES` row or a `LIMIT`/`OFFSET` count, raised while planning (ISO SQLSTATE class 42, #1165) |
| `CS01006` | Error | Function call whose arguments no overload of its function accepts: a count outside every overload's (`ABS(1, 2)`, `UPPER()`, `COALESCE()`, `COUNT(a, b)`, `SUM()`), `*` for a function other than a parameterless aggregate such as `COUNT`, or argument types no overload takes (`SUM(name)` over text). Raised while planning, in every expression position, `CHECK` and `DEFAULT` included; by the evaluator for a call that reaches it unplanned; and by opening a database whose catalog stores a CHECK holding `COALESCE` with no operand (ISO SQLSTATE class 42; PostgreSQL's 42883, undefined function, #1189) |
| `CS05007` | Error | A function threw while the statement ran: anything but a `DatabaseException`, cancellation of the statement itself or an exhausted stack or memory, which is the inner exception; or a `CAST`'s registered cast threw anything but those and a value's fault (`FormatException`, `OverflowException`, `InvalidCastException`), a cancellation the statement did not ask for included (`Cast TEXT AS cents failed: ...`, E3); or a function returned a value of another type than it declares (one that widens to the declared type is converted), or NULL when it declares it never returns NULL. The message names the function (ISO SQLSTATE 38000, external routine exception; E2) |
| `CS01008` | Error | A function call that more than one overload accepts equally well, such as an overloaded function over a NULL literal. Raised while planning; a `CAST` on an argument chooses (ISO SQLSTATE 42725, ambiguous function; E2) |
| `CS01009` | Error | A write would evaluate a stored CHECK that calls a function the engine does not register, registers as an aggregate, registers with no overload, or more than one, that accepts the stored call, or registers with a result that no longer fits where the CHECK uses it. The database opens and its reads proceed; the message names the constraint, the table and the call. An engine build that declares the database fails with it (ISO SQLSTATE 42883, undefined function, as PostgreSQL raises at use for a missing implementation; E2, owner decision 65) |
| `CS02010` | Error | A type name that is neither a built-in type nor a domain in the merged engine/SQL type environment: a column type in DDL or a `CAST` target, raised while planning; or a write that assigns a stored column whose domain the opening engine cannot resolve, or resolves over another storage type, length, precision or scale than the column stores. The database opens and its reads, and writes that do not assign the column, proceed; an engine build that declares the database fails with it (ISO SQLSTATE 42704, undefined object, PostgreSQL's "type ... does not exist"; E3, decision 65's rule applied to domains). The one code names both causes, an unknown name while planning and a stored column's unregistered domain, where E2 split a function's two (`CS01006`, `CS01009`); whether to split it is an open owner question |
| `CS02011` | Error | A `CAST` whose operand's type no registered cast converts to the target, nor is of its storage type: raised while planning when the plan knows the operand's type, the same over an empty table as over a populated one, and while evaluating for a parameter, whose type only its value reveals (ISO SQLSTATE 42846, cannot coerce, PostgreSQL's "cannot cast type ... to ..."; E3) |
| `CS02012` | Error | Character value exceeds its destination's declared length and the excess is not entirely ASCII spaces: INSERT, INSERT ... SELECT, UPDATE, DEFAULT or ADD COLUMN backfill, for plain columns and domain base types. Explicit CAST truncates instead (ISO SQLSTATE 22001, string data right truncation; #1418) |
| `CS05013` | Error | Invalid statement, expression, DDL or query shape for the executor |
| `CS01014` | Error | Table does not exist |
| `CS01015` | Error | Unknown or ambiguous column |
| `CS01016` | Error | Unknown function, absent from executable and recognized-unsupported vocabulary (SQLSTATE 42883, undefined_function) |
| `CS05017` | Error | Invalid aggregate placement or grouping expression |
| `CS02018` | Error | Invalid ordering ordinal, row count or CAST target declaration |
| `CS01019` | Error | Duplicate target column, key constraint column, PRIMARY KEY declaration, table binding or ambiguous output alias |
| `CS01020` | Error | No value supplied for a parameter; rejected before rows are read or written |
| `CS01023` | Error | Caller parameter keys collide after one leading sigil is stripped |
| `CS03027` | Error | ADD COLUMN with a volatile DEFAULT on a table with live rows requires a rewrite; names the column (#1121, #1447) |
| `CS01028` | Error | SQL namespace does not exist for schema/table/index DDL (PostgreSQL SQLSTATE 3F000); DROP TABLE IF EXISTS remains a no-op |
| `CS03029` | Error | CREATE SCHEMA names an existing namespace without IF NOT EXISTS (PostgreSQL SQLSTATE 42P06) |
| `CS03030` | Error | DROP SCHEMA RESTRICT finds dependent objects; the message offers CASCADE (PostgreSQL SQLSTATE 2BP01) |
| `CS03031` | Error | DROP SCHEMA targets dbo, which code-first provisioning requires |
| `CS05032` | Error | DISTINCT/ALL/input ORDER BY on a scalar function, or DISTINCT input ordering whose keys are absent from the arguments (PostgreSQL 42809 / 42P10) |
| `CS03033` | Error | Invalid persisted view declaration/replacement, changed recorded column identity, oversized view record, or otherwise uncoded stored-body binding failure |
| `CS05040` | Error | Invariant LIKE or NOT LIKE exceeded its polynomial work budget; located ExecutionFailure leaves the connection usable (#1166) |
| `CS06041` | Error | Storage/index operation failed (I/O SQLSTATE 58030); ExecutionFailure preserves the connection and aborts an explicit transaction (#1364) |
| `CS05042` | Error | Encoded row, index key or catalog definition exceeds its size limit, or catalog DDL generation is exhausted (SQLSTATE 54000, program_limit_exceeded); the statement writes nothing and an explicit transaction remains active (#1266, #1653) |
| `CS05043` | Error | Server-side statement timeout (SQLSTATE 57014, query_canceled); failed statement changes and new lock resources are rolled back/released, and the session and explicit transaction remain usable (#1231) |
| `CS04044` | Error | Individual lock-wait timeout, including unique-key waits (SQLSTATE 55P03, lock_not_available); same statement-atomic failure contract as CS05043 (#1231) |
| `CS05045` | Error | Result encoding/preparation failed. Writing statements prepare header/rows before commit and any result frame; DML failures roll back only the statement. Read-only SELECT, EXPLAIN, system views and SHOW encode during delivery and may fail after partial frames, which the client discards. Includes encoding failures or a header/row above ProtocolFrameHeader.MaxPayloadLength (currently 16 MiB); there is no aggregate result cap. Size refusals map to SQLSTATE 54000 and report encoded size and frame limit. The connection and explicit transaction remain usable on result failures; transport failures are terminal (#1651). |
| `CS02034` | Error | Invalid LIKE ESCAPE: escape is neither empty nor one Unicode scalar, or a pattern ends with the escape character (SQLSTATE 22025); ExecutionFailure leaves the connection usable |
| `CS03035` | Error | ON CONFLICT has no matching unique arbiter, a named target is not a unique index, or DO UPDATE omitted its target (PostgreSQL 42P10) |
| `CS05036` | Error | ON CONFLICT DO UPDATE or MERGE cannot affect a row a second time (PostgreSQL 21000) |
| `CS03037` | Error | UNIQUE, PRIMARY KEY or unique-index violation, including a second unique index during ON CONFLICT DO UPDATE (SQLSTATE 23505, unique_violation) |
| `CS03038` | Error | Table CHECK or domain-check violation, including writes, CAST to a domain and function domain arguments/results (SQLSTATE 23514, check_violation) |
| `CS03039` | Error | FOREIGN KEY violation: missing parent on INSERT/UPDATE, or referenced key protected by RESTRICT/NO ACTION on DELETE/UPDATE, including final-row checks (SQLSTATE 23503, foreign_key_violation) |

Each SqlConstraintViolationException carries exactly one Error diagnostic. Its message names
the constraint and table (no table for a domain CAST), without raw unique keys or unrelated
row values. The enclosing statement span is attached when available. SQLSTATE mappings
follow PostgreSQL `src/backend/utils/errcodes.txt:237-240`; SQLSTATE is not a separate field
in the diagnostic carrier. NOT NULL remains CS03024 (23502). The live constraint corpus
in SqlLanguageConformanceTests and both-transport SqlConstraintDiagnosticWireTests pin the
codes, locations, statement rollback and continued use of the session/transaction.

**Row and index-key limits (#1266).** Every encoded row, including its MVCC header and
physical column layout, must fit the storage record limit (currently 8,092 bytes).
INSERT VALUES, INSERT SELECT, UPDATE, ON CONFLICT DO UPDATE and referential SET NULL/SET DEFAULT
check final replacement records before physical apply. Encoded index keys, including collation
transforms and component delimiters, are limited to 1,024 bytes for both unique and nonunique
indexes; index construction uses the same check. Equality with either limit is accepted.
ON CONFLICT checks sizes only for rows and keys it writes: DO NOTHING may discard an oversized
proposal, and DO UPDATE may replace it with a fitting row even if the proposal's row or a
non-arbiter UNIQUE key exceeds its limit. Lock hashes, arbiter lookups and old-value tombstones
do not enforce write-size limits. This follows PostgreSQL's arbitration before insertion
(`src/backend/executor/nodeModifyTable.c:1121-1232`).
Server-side execution limits (#1231) are configured through SQL engine options and nullable
session-object overrides, not SQL SET statements. StatementTimeout and LockTimeout default to
zero (off); a null session override inherits its engine default. A statement timer is cooperative
and begins after parsing; each contended lock wait gets its own lock-timeout budget. Timers may
stop execution, never a started commit or mandatory cleanup after a DDL self-commit. SQL keeps
its statement-atomic transaction contract after either timeout; grants from earlier statements
and upgrades of already-held resources remain until transaction end. Caller cancellation keeps
its cancellation meaning. The cause that ends a lock wait determines its diagnostic even if a
later statement deadline expires before its continuation runs. Provisioning schema sessions set
both timeouts to zero; their caller/lifecycle cancellation still applies, including to deferred
index builds. CREATE/DROP FUNCTION and DOMAIN finish binding after self-commit without observing
cancellation again. SqlTimeoutConformanceTests and SqlTimeoutRegressionTests are the executable conformance corpus for
these engine-visible limits; no grammar/profile clause count changes.

A refusal carries CS05042 / SQLSTATE 54000, naming the table, actual byte size and limit.
The failure leaves the session usable and an explicit transaction active, with earlier statements
intact. Failed upsert preparation releases its new key and row grants, including a row retained
after waiting, while preserving earlier transaction locks. SQLSTATE is a documented mapping,
not a new wire field. PostgreSQL's early oversized-tuple refusal is in
`src/backend/access/heap/hio.c:527-534`. `SqlProgramLimitConformanceTests` exercises both execution
boundaries and exact limits; `SqlUpsertArbitrationConformanceTests` exercises release after a wait.

Positions are absolute character offsets into the statement text; line/column
presentation is computed by tooling from the source (offset → line mapping), not
carried per node.

**Keyword disposition (#1101).** Every keyword of the profile, and every entry of
the internal recognized-unsupported table (`SqlUnsupportedVocabulary`), either
parses inside a supported clause or reports exactly one `CK00001` naming its
construct. The table holds the clause keywords the preflight scan rejects,
such as `WINDOW` in the statement matrix. UNION, INTERSECT and EXCEPT remain profile keywords and execute; CORRESPONDING is rejected contextually after an operator and optional quantifier, without reserving that identifier. It also holds every other word or
operator outside the profile's keywords that a dedicated rule recognizes and
rejects with `CK00001`: `RECURSIVE`, `[NOT] MATERIALIZED`, `SEARCH`/`CYCLE`,
`SELECT ALL`, SELECT-tail `FOR UPDATE` / `FOR NO KEY UPDATE` / `FOR SHARE` /
`FOR KEY SHARE`,
`CREATE COLLATION`, `CREATE FULLTEXT`, `GROUPING SETS` and
the `GROUPING`, `ROLLUP`, `CUBE` and `GROUPING_ID` calls, and
`WITHIN GROUP` after a non-LISTAGG call, LISTAGG ON OVERFLOW (contextual), the `ANY`/`SOME` (and `ALL`) array comparisons,
`LATERAL`, `~`, `SEQUENCE`, `SERIAL`, `BIGSERIAL`, `SMALLSERIAL`, `OVERRIDING`,
`MINVALUE`, `MAXVALUE`, `CYCLE`, `CACHE`, `RESTART`, plus `PERCENT` and `WITH TIES` after
FETCH or TOP. Identifiers spelled like these recognized-unsupported words,
including `MATERIALIZED`, `SEARCH`, `percent` and `ties`,
render quoted in canonical SQL. `WITH` remains a profile keyword; its nested-form
refusal is a dedicated parser rule. The table also holds the recognized
non-executing functions listed above, including the window and niladic datetime names (#1103). Profile keywords
that start an unsupported form by another rule, such as `LEFT` or `CROSS`, need no entry.
A recognized construct is skipped once it is reported, so the text after it adds
no second diagnostic: `= ANY (array_operand)` no longer parses as a call to a
function named `ANY`, `LATERAL` is no longer bound as a table name, and `WITHIN`
is no longer read as a column alias. `SqlKeywordDispositionTests` (Sql.Language)
holds one case per word. It fails when a keyword or table entry is added without
one, so the item that adds a word adds its case in the same change. It also fails
when an entry's diagnostic does not name the construct the table records for it.

Every planner rejection carries structured `DatabaseException.Diagnostics`: a stable code,
severity, message and the offending AST location where available. The in-process message
contains no code prefix. SQL composes `CODE: message` from the structured code
for `ExecutionFailure`; [negotiated diagnostics](#wire-diagnostics-1105) add structured delivery.
Text factories and sessions throw `DatabaseParseException` containing every statement
diagnostic, ordered by start offset. A parsed typed request with errors throws the same
exception with the same complete list; no recovery AST executes. The first-error message
retains its existing `SQL parse error CODE: message` spelling.
While planning, the engine raises `CS02003` for a sign over an operand it knows
is not a number, `CS01005` for a column reference where no columns are in
scope, `CS01006` for a function call whose arguments no overload of its function
accepts, and `CS01008` for one several accept equally well; a `LIMIT`/`OFFSET` count
is evaluated while planning, so its arithmetic
faults (`CS02001`, `CS02002`) surface there too, and `CS05004` can come
from any planner walk. Comparison-literal coercion for an access path falls back
to residual evaluation if the value fails the destination's declared bounds.

The `COHSQLE` codes are engine diagnostics, not parser diagnostics. They are structured
in process, with source offsets when planning has an offending AST location; a runtime
failure without a source location leaves its offsets null. The wire's
`ExecutionFailure` text leads with the code. The arithmetic fault contract above
defines when each of `CS02001`–`CS02003` is raised, the expression
nesting limit when `CS05004` is, the column-scope rule for VALUES and
counts when `CS01005` is, the function-argument and overload rules under Statement
completeness when `CS01006` and `CS01008` are, and the registered-function rules
under Builtin functions when `CS05007` and `CS01009` are, the type and cast rules
under Explicit conversion when `CS02010` and `CS02011` are, and Declared bounds
on writes when `CS02012` is. The engine's transaction-state codes,
`CS04001`–`CS04003`, its offline-database code, `CS04004`, and its aborted-transaction
code, `CS04005`, are documented in the SQL engine design.

## Code-first literal defaults (B2)

The typed schema's `table.Default(selector, value)` renders C# values into SQL literals: strings
and characters are quoted with apostrophes doubled, numbers use invariant culture, Boolean
values use `TRUE`/`FALSE`, and null uses `NULL`. Temporal values and GUIDs use invariant quoted
text converted to the column type. Enums use their numeric storage values.
`table.DefaultSql(selector, sqlLiteral)` is the escape hatch for a raw supported SQL literal,
for example `DefaultSql(row => row.Placed, "DATE '2026-01-01'")`. All supported typed literal forms are accepted. Both declarations store the rendered literal text in the
`cohesion/database-schema/v8` document, so equal text gives the same hash. This format records CHAR separately from VARCHAR and normalizes
an omitted decimal scale to zero when precision is present. Earlier artifacts, applied markers
and pending targets are refused before hash comparison (SqlSchemaValidationException,
InvalidDocument at schema.format; COHDBSDK206 for SDK migration input). Rebuild artifacts and
recreate databases recording an earlier schema format; there is no upgrade or old-hash reconciliation.

`DEFAULT -7` and `DEFAULT -1.2500` accept a single numeric minus. Arithmetic, nested unary expressions,
functions and other computed expressions remain unsupported in typed defaults. SQL column defaults
admit scalar expressions, including in ADD COLUMN under its nonvolatile fast-default rules (#1121); see E4 below. `DEFAULT NULL` has the same
stored meaning as no default. A binary default is a quoted `\x` prefix followed by even-count
hexadecimal digits, for example `DEFAULT '\x007FFF'`; no whitespace or octal escapes are accepted,
and the column's length bounds still apply. The typed `byte[]` renderer emits uppercase digits.
The ISO `DEFAULT X'007FFF'` spelling also works. Quoted backslash-x defaults keep
resolving after reopen; COLUMN_DEFAULT reports the resolved value as X hex.
General text-to-BYTEA conversion is separately defined by the [CAST contract](#explicit-conversion-cast-1022-e3).

While a compiled-schema apply runs, ordinary session DDL is refused with
`DatabaseObjectLockedException`. Writers to tables the apply creates or alters wait until the
apply, including any compensation, finishes; unrelated table reads and writes continue.

## SQL definitions and declared scripts (E4)

~~~sql
CREATE FUNCTION increment(p INT) RETURNS INT IMMUTABLE STRICT RETURN p + 1;
DROP FUNCTION increment(INT);
CREATE DOMAIN positive AS INT DEFAULT 1 NOT NULL CONSTRAINT positive_value CHECK (VALUE > 0);
DROP DOMAIN positive;
~~~

Functions have one expression body. Parameters are named; tables, subqueries, aggregates,
external parameters and recursion are refused. Defaults are VOLATILE and CALLED ON NULL INPUT,
as in PostgreSQL src/backend/commands/functioncmds.c:1095-1098. Domain arguments are coerced
and checked before STRICT bypasses a body on NULL. Thus a bad domain value still fails
when another argument is NULL; a NOT NULL domain rejects NULL before strict dispatch.
A function may call only functions whose volatility is at least its own; CHECK, including a
domain CHECK, admits Immutable functions only. Functions share the engine namespace and E2's
storage-type overload identity. Domain names in SQL function signatures persist, and their
bounds and checks apply to arguments before dispatch and results after body execution.
Parameters keep their domain identity inside the body: a domain-specific cast is selected for
both a direct domain-column argument and an argument explicitly CAST to that domain, also after
reopen. Return-domain checks accept the same supported widening as column and argument checks,
including INTEGER to a BIGINT check parameter. Strict checks skip NULL; CALLED ON NULL INPUT
checks receive it, and a NOT NULL result domain still rejects it.
The invocation collation becomes the parameter collation inside the body: a case-insensitive
column or explicit COLLATE affects comparisons and LIKE, and string results use it for ordering
and grouping. A call without an input collation uses the database default. Body variants are
bound once per collation and reused. Iteration 1 has no CREATE OR REPLACE, procedural body, LANGUAGE,
CASCADE, qualified function name or domain-over-domain, unlike PostgreSQL's broader facilities.

Every persisted function call in table/domain CHECKs, column/domain DEFAULTs and SQL bodies
records its normalized name and resolved parameter signature beside canonical SQL. A later
overload never rebinds these calls, including after reopen with different engine registrations.
DROP dependencies use the same identities. A missing recorded identity or a native signature
whose volatility has weakened below the persisted use's requirement follows CS01009. The
database opens and stored rows remain readable; affected writes fail, including through SQL
wrappers and domain CHECKs, including domains in casts and function parameter/result types.
Reopening with the original compatible registration restores use.
New declarations still reject incompatible volatility. CHECK rechecks Immutable-only admission
on every bind. Domain defaults remain literal-only
and carry an empty call list. Call vectors were introduced in format 10; the current record-space format is 28, with no compatibility reader.
Format 28 adds the internal sequence definition and reservation records (#1717). Relation-name
collisions involving an existing sequence use CS03070 (42P07), as sequence creation does for any
occupied relation name; the diagnostic names the existing kind. Table-on-table collisions retain
CS03049. Standalone sequence SQL syntax is delivered by SQ-S2; identity integration remains SQ-S3 work.

Domains share E3's type namespace and coercion/check path. Columns inherit their domain's
literal default unless they supply one; explicit DEFAULT NULL overrides inheritance. Domain
bounds and NOT NULL apply on writes and CAST. SQL definitions persist as canonical expressions.
DROP refuses dependencies in other function/domain bodies, CHECK, DEFAULT, casts and domain
columns. Ordinary DDL cannot drop Schema-owned objects. Engine registrations cannot be dropped.
DROP FUNCTION checks the requested normalized signature: an engine-registered overload is
CS03303; an absent overload is CS01009 (42883) even when the engine registers another overload
with the same name. Type aliases and function-name casing do not change that identity.

Column defaults admit variable-free scalar expressions with Immutable, Stable or Volatile calls. Domain, typed DefaultSql and SDK defaults remain literal-only. ADD COLUMN freezes a nonvolatile expression once into the independent missing-value literal; volatile defaults on live rows report CS03027 naming the column because table rewrites are tracked by #1447. SET/DROP DEFAULT changes only the insert default. Canonical expression text and resolved call identities survive reopen through the persisted-definition ceiling parser.

| Code | Meaning |
| --- | --- |
| CS03301 | Invalid, recursive or volatility-incompatible SQL definition; absent domain requested by DROP. Duplicate functions use CS03065 (42723); missing/unresolved functions use CS01009 (42883). |
| CS03302 | A persisted definition or column depends on the object being dropped. |
| CS03303 | SQL attempted to drop an engine-registered function/type. |
| CS03201 | A script checksum, identity, completed target or object claim is inconsistent, or publication does not match the actual mutation and exact preceding receipt/generation. |
| CS03202 | Verify requires a complete receipt with the declared checksum. |
| CS03203 | Transaction control inside a declared script; the runner owns transactions. |
| CS03204 | A script statement failed or attempted to adopt an existing table or index. |
| CS03205 | A script attempts ALTER TABLE, DROP TABLE or DROP INDEX against an object declared by the typed schema; declare the change in the typed schema. |

Definition-binding CS03301 rejections carry the code separately from their message in
DatabaseException.Diagnostics. The span identifies the offending expression, or the statement
when a parameter/type has no separate token location. Binding retained catalog metadata uses its
canonical expression/type fragment when the original declaration is unavailable. In particular,
CREATE TABLE c (id INT CHECK (id)); locates id in CHECK, and DROP DOMAIN absent; locates DROP.
The in-process message is unprefixed; the SQL server composes CODE: message.

SqlDatabaseBuilder.Script(name, sql) runs after the typed schema in declaration order. Names are
case-sensitive, unique and at most 128 characters. Exact UTF-8 bodies are SHA-256 checked;
whitespace changes the checksum. Parsing precedes file I/O and lexical semicolon splitting
respects strings, quoted names and comments. Deleting a declaration undoes nothing. Each statement
commits with a receipt; crash recovery resumes after its committed prefix, including DML.
DDL receipts certify the catalog's actual mutation: its target must match the parsed statement,
and its preceding receipt and generation must still match before commit. The catalog derives the
object claims. An IF EXISTS drop of an absent object completes its receipt without advancing
DDL generation. Outside the unchanged digest/generation O(1) skip, Apply and Verify validate every
completed statement's target and created-object claims against the declared SQL. Function receipts
retain the CREATE definition's or bound DROP request's parameter type spellings together with the
actual signature, so removed or redefined domains do not prevent validation of completed statements.
An inconsistency is `CS03201`; Verify still requires every declared script to be complete.
The typed schema exclusively owns its declared tables and indexes. Script ALTER TABLE and DROP
TABLE against a typed table, and DROP INDEX against a typed index (including its primary key),
are refused before Build opens or changes the database. Scripts may create and later alter their
own tables, create their own indexes on typed tables, and run DML against typed tables.
Scripts run inline, including data-scaling DDL, and force preceding typed migrations inline.
SDK v8 artifacts do not carry scripts; extraction refuses Script calls with COHDBSDK110.

## Quoted calls (#1103 review)

A quoted identifier followed by parentheses is a function call. Recognized unsupported
built-ins use their lowercase lookup spelling for quoted names: `"rank"(id)` reports
parse-time `CK00001`, while `"RANK"(id)` and `"Rank"(id)` are distinct names outside
that vocabulary. Unquoted names match the vocabulary without regard to case. Quoting a name
without parentheses keeps it a column reference, including `"sqrt"` and `"current_date"`.
Declared executable calls such as `"abs"(id)` remain executable. Unknown function names
continue to be resolved by the engine's function catalog during planning.

PostgreSQL preserves delimited spelling in `src/backend/parser/scan.l:773-783`, folds
unquoted identifiers at `scan.l:1067`, and looks up that name through
`src/backend/parser/parse_func.c:1608` and `src/backend/catalog/namespace.c:1237`.
This is the reference for the quoted unsupported-vocabulary comparison; it must not fold
quoted names to match uppercase vocabulary entries.

## Diagnostic completion (#1103 review)

| Code | Meaning | PostgreSQL SQLSTATE |
| --- | --- | --- |
| `CS01015` | Unknown column, including a DDL column reference | 42703 (`undefined_column`) |
| `CS04021` | A mutation targets a read-only system relation | 25006 (read-only SQL transaction, analogous restriction) |
| `CS02022` | A non-temporal CAST input or stored-column assignment has invalid text representation | 22P02 |
| `CS03024` | A column that disallows NULL receives NULL, including DEFAULT with no declared default | 23502 (`not_null_violation`) |
| `CS03025` | An identity definition, explicit assignment, ALTER, backfill or allocation violates its generation contract | 428C9 (`generated_always`) for explicit ALWAYS assignments; identity range and definition failures share the local code |
| `CS01026` | Duplicate CTE name or missing relation matching a later sibling, before reading rows | 42712 / 42P01 |
| `CS02003` | Set-operation column types cannot be matched by implicit conversion; names both types and the one-based position | 42804 |

NOT NULL failures carry one error diagnostic naming the column and the executing statement's
span when source text is available. The existing missing-default and explicit-NULL message texts are unchanged.
PostgreSQL raises the same constraint category in `src/backend/executor/execMain.c:2268-2274`.
Execution-time diagnostic work #1653 now treats NOT NULL as done; `CS01023` belongs to #1102.

Execution and DDL diagnostic completion (#1653) uses the following additional mappings.
Codes are separate from in-process message text; wire errors compose `CODE: message` once.
Unlocated execution failures receive the statement span; a more precise current-statement expression span is
preserved. Locations from persisted CHECK/DEFAULT/function text and synthetic provisioning carriers are discarded
before the statement fallback, even when their numeric offsets happen to fit inside the statement. These are the parser's existing locations: SELECT query tails can extend beyond
the root location (parser span completion remains separate work). Failures during database open have no statement text. DEFAULT wrappers retain
the original diagnostic carrier and cause. The live execution conformance corpus covers these
families in process and over the wire, including transaction and connection reuse. Previously uncoded
execution failures carry categorized `CS` identities in diagnostics and begin with `CSxxxxx:` in
wire error text (`CS033xx` for registered definitions), as assigned by the
[Database error-code registry](../../Assimalign.Cohesion.Database/docs/ERROR-CODES.md). Framing is unchanged;
the diagnostic fixes introduce no persisted-format change beyond #1422's format-26/v8 fence.

| Code | Meaning | PostgreSQL SQLSTATE |
| --- | --- | --- |
| `CS02046` | Scalar subquery returns more than one row | 21000 (`cardinality_violation`) |
| `CS02047` | Malformed temporal text in assignment, DEFAULT or CAST | 22007 (`invalid_datetime_format`) |
| `CS02048` | Calendar, clock or offset field out of range in assignment, DEFAULT or CAST | 22008 (`datetime_field_overflow`) |
| `CS03049` | Table or index name already exists, including provisioning collisions | 42P07 (`duplicate_table`) |
| `CS03050` | Constraint or non-function SQL definition name already exists | 42710 (`duplicate_object`) |
| `CS01051` | Missing table during execution/DDL | 42P01 (`undefined_table`) |
| `CS01052` | Missing constraint, index, collation or other catalog object; missing schemas retain CS01028 / 3F000 | 42704 (`undefined_object`) |
| `CS03053` | Invalid table definition, including nullable primary key or dropping the last column | 42P16 (`invalid_table_definition`) |
| `CS03054` | Invalid FOREIGN KEY column lists or referenced key is not unique | 42830 (`invalid_foreign_key`) |
| `CS02055` | Incompatible store value, DEFAULT, FOREIGN KEY column type, CHECK function result or compared values | 42804 (`datatype_mismatch`) |
| `CS03056` | DDL refused because constraints or indexes depend on the object; namespace RESTRICT retains CS03030 | 2BP01 (`dependent_objects_still_exist`) |
| `CS05057` | Unsupported execution/DDL operation, including a subquery encountered by CHECK admission | 0A000 (`feature_not_supported`) |
| `CS06058` | Damaged or incompatible loaded persisted expression or missing-value literal | XX001 (`data_corrupted`) |
| `CS06059` | Malformed persisted SQL catalog metadata, including truncated typed records; retains page/slot and original cause | XX001 (`data_corrupted`) |
| `CS06060` | Unsupported SQL catalog metadata format or version; earlier/future metadata is refused without an upgrade reader | 0A000 (`feature_not_supported`) |
| `CS03059` | Expression cannot be stored as a valid deterministic row definition | 42P17 (`invalid_object_definition`) |
| `CS05060` | Executor/catalog invariant failure, including unavailable constraint indexes or freshly rendered CREATE VIEW text that cannot reload | XX000 (`internal_error`) |
| `CS05068` | UPDATE target matched by multiple source associations; refused before writes | 21000 (`cardinality_violation`) |
| `CS03061` | Column already exists | 42701 (`duplicate_column`) |
| `CS02062` | INTERVAL duration overflow in assignment, DEFAULT or CAST | 22015 (`interval_field_overflow`) |
| `CS02063` | FOREIGN KEY columns have incompatible collations | 42P21 (`collation_mismatch`) |
| `CS02064` | ALTER COLUMN TYPE old value, USING result or dependent DEFAULT has no assignment cast | 42804 (`datatype_mismatch`) |
| `CS04064` | A bound table was replaced while DDL waited; retry the statement | 40001 (`serialization_failure`) |
| `CS04066` | TRUNCATE inside an explicit transaction | 25001 (`active_sql_transaction`) |
| `CS04065` | Hard lock deadlock; roll back the victim transaction before retrying | 40P01 (`deadlock_detected`) |
| `CS03065` | Function signature already exists | 42723 (`duplicate_function`) |
| `CS03067` | TRUNCATE omits a required referencing table | 0A000 (`feature_not_supported`) |
| `CS03066` | Compiled-schema ownership refuses session DDL | 42501 (`insufficient_privilege`) |

Definition dependency refusals retain `CS03302` (2BP01), and engine-registered
function/type drops retain `CS03303` (0A000), now carried as structured diagnostics.
The mappings follow PostgreSQL `src/backend/utils/errcodes.txt:119,157,166-179,206,280,346-398,499-500`.

Numeric CAST range/precision faults use existing `CS02002` (22003), division/remainder by zero
uses `CS02001` (22012), and operand-type errors including NOT and binary arithmetic
use `CS02003` (42804). Unsupported evaluator nodes/operators use `CS05013`.
The carrier retains an available expression span at runtime and during LIMIT/OFFSET
planning. Column-scope failures use the offending column span, including INSERT VALUES.
PostgreSQL's corresponding data exception codes are declared in
`src/backend/utils/errcodes.txt:168,206,197` (22012, 22P02, 22003).

## Wire diagnostics (#1105)

On negotiated protocol 2.1 with the diagnostics capability, Warning and Error diagnostics retain code, severity, message and source span over the model wire within the 4,096-entry and 1 MiB limits. Overflow retains a leading prefix plus CK07001 with exact omission counts, preserving the original failure text or successful result and a usable session. Null/empty code and message fields decode as null. Information diagnostics are omitted. A negotiated 2.0 connection retains the existing Error text and result frames. Language admission and execution semantics are unchanged.

## Concurrent foreign-key catalog changes (#1198)

Before reading rows, DML refreshes adjacent foreign-key definitions changed during an
object-lock wait, including ADD COLUMN and removal of a child table or constraint. A
changed target table invalidates its already-bound plan: structured code `CS04006`
is a retryable transaction abort; retry the statement in a fresh transaction.

### Text conversion diagnostics and persisted defaults (#1422)

Temporal assignment, DEFAULT and CAST use one reader classification: CS02047 / 22007 for
bad format, CS02048 / 22008 for calendar, clock or offset fields outside range, and
CS02062 / 22015 for interval duration overflow. The reader records the reason explicitly;
SQL does not infer it from a CLR exception type. This follows PostgreSQL datetime.c:4247-4290.
REAL and DOUBLE text retain CS02022 / 22P02 for malformed input and CS02002 / 22003
for overflow/underflow. Floating assignment rejects 1,000 and overflowing 1e400, as CAST does.

Catalog format 28 and compiled-schema v8 refuse earlier definitions rather than reinterpret
retained default text. Format-25 INTERVAL defaults such as '12' (formerly days) and
'2.03:04:05.1234567' are never rebound by this engine. A catalog refusal carries CS06041;
a schema refusal uses InvalidDocument at schema.format. Export with the writing engine,
recreate, rebuild and reapply the declaration (#1152); no compatibility reader is provided.

Unknown COLLATE names are syntactically valid identifiers and fail during binding with
CS01052 / 42704, including declarations with DEFAULT context. A missing collation in loaded
persisted text retains CS06058 / XX001. Culture-aware and user-defined collation creation
remain unsupported. Quoted names use the ordinary delimited-identifier decoding.

## DDL workspace verification (#1444)

Constraint validation streams rows and unique-index validation walks adjacent live keys.
The cross-platform regression guard attributes row/key retention, separately from unrelated
managed-heap activity; this verification change adds no SQL syntax, error code or format version.

## Structured failure tracing (#1350)

Thrown statement failures report their structured diagnostic code without exposing statement
text in tracing. Offline and unconfirmed-commit exception detail is unprefixed; Diagnostics
carries the existing model code. This adds no language syntax or diagnostic number.

### Cascade work guard (#1285)

ON DELETE CASCADE keeps its existing results and transaction semantics. Its fan-out regression guard now compares physical B-tree entry-directory reads for four 4,000-child cascades against one 16,000-child cascade in the same tree, with less than 2× work per child. It verifies the surviving bystander and uses no duration to decide correctness. No SQL syntax, diagnostic or format changes accompany the test change.

## Safe index-history reclamation (#1195)

Background and unique-insert B-tree pruning preserve the statement's snapshot and all existing
language results. The shared coordinator supplies the safe horizon; ending the oldest reader
allows later maintenance to remove its dead physical index versions. This changes no syntax,
diagnostic code, wire contract or on-disk format.


## Storage decode failures (#1372)

A malformed confirmed stored record fails execution with the existing corruption diagnostic
and physical page/slot context. It is not a missing row, document or graph element. D1 confirms
the copy before decoding; this adds no syntax, SQLSTATE or format version. Valid data retains the same language results.

## Cascading DELETE lock retries (#1420)

DELETE's cascade, SET NULL and SET DEFAULT actions are prepared before physical mutation.
A refused row, table-intent, parent-reference or unique-key lock releases new attempt grants
on the first two refusals, waits, keeps that waited grant and restarts preparation. From the
third refusal of that statement onward, new closure grants are retained and refused resources
queue normally with deadlock detection. Earlier transaction grants and their
upgrades remain held. A failed unapplied preparation, including caller cancellation, releases
its new statement grants, including cancellation at the apply gate before admission;
an offline owner or started physical apply keeps its existing lock
ownership. The statement deadline spans all retries. Fixed snapshots and first-updater-wins
remain enforced, so rebuilding an undone child candidate can instead expose the direct parent's
committed-replacement conflict. Soft queue cycles may be reordered without a victim; genuine
held-lock cycles remain retryable deadlocks. Hard deadlocks carry structured `CS04065`, mapped
to SQLSTATE `40P01` (PostgreSQL `src/backend/utils/errcodes.txt:334`). Under statement atomicity,
the explicit victim transaction remains active with earlier work and locks; its caller must
roll it back before retrying so surviving waiters can progress. Auto-commit rolls back its
one-statement transaction. SQLSTATE remains a documented mapping of the diagnostic code,
without a separate wire field. Results are returned only after the complete statement succeeds.

### Probe costs and SELECT analysis (#1522, D3)

No persisted statistics or standalone ANALYZE maintenance exists. Candidate index ranges are
counted under the statement snapshot with limits of 512 physical entries and 32 page visits;
invisible entries and the stopping key consume physical work. An unfinished count is a lower
bound, explicitly labelled in EXPLAIN. A selected index range of at most 16 candidates needs
no base-table count. Join estimates are collected only for hash or competing index choices;
complete unique probes need none. Counting uses the tree read latch and validates catalog
identities without taking transaction object locks. Unindexed equijoins can use HashJoin
regardless of actual size: an unknown input estimate is 4096, including an empty or tiny input
without a countable index. Full ON/WHERE residuals, duplicate keys, NULL and LEFT semantics remain.
An ON condition referencing any derived String/Json/Character column retains nested loops to
preserve deferred collation errors. No hash spill exists.

`SqlDatabaseEngineOptions.StatementMemoryBudget` defaults to 67108864 bytes and must be positive.
It limits accounted concurrently live hash-build storage per statement. MERGE, UPDATE FROM and
DELETE USING also charge retained decoded targets, inputs, associations and selected write storage,
including normalized buffers and referential writes. These conservative reservations last until
statement exit; shared payload already charged by a hash build is not charged again for an alias.
An exhausted joined statement refuses before any write and releases memory on success, failure or
cancellation. CTAS also charges its retained source result and copy buffers, as described in the CTAS contract. Budget exhaustion reports
`CS05067` (53200, out_of_memory). It does not cap result materialization or transient planning.
SELECT-only `EXPLAIN ANALYZE` runs normal query expressions, including application functions, and
returns the existing eight-column plan description. `actual_rows` counts candidates before
residuals on instrumented nodes, rather than final output cardinality. `estimated_rows` and
probe/heuristic qualifications appear in access and JoinStep nodes. Plain EXPLAIN never runs its
wrapped statement and has NULL actual counts. Its index probes can still fail with `CS04006`
when committed concurrent DDL changes a bound table definition. Unmeasured counts remain NULL in either form.
Repeated correlated invocations accumulate on the child nodes. A parent's candidate count
excludes descendant work, while statement-wide physical-work totals include every invocation.
Single-source derived SELECTs count the candidates read from that source. Repeated view
references keep separate analysis counters even when their immutable body plan is cached.

## Sql diagnostic conventions (#1470)

Exception and diagnostic templates use the project's build-generated `ErrorMessages` resources.
Parameterized templates format with the current culture; abort/cancellation templates used in
normal cleanup are cached. The [Database error-code registry](../../Assimalign.Cohesion.Database/docs/ERROR-CODES.md)
assigns this family its two-letter prefix and categorized five-digit identities.
Existing SQLSTATE meanings, numeric wire error kinds, frame layouts and disk formats are preserved.
Structured diagnostics keep their code outside the message. Compiler diagnostic identifiers and
standard .NET argument guards retain their own contracts.

## Modifier diagnostic templates (#1470)

Type-modifier refusal templates come from each consuming assembly's generated resources. Runtime, schema and SDK consumers share the same grammar and text. Diagnostic identities, SQLSTATEs, accepted declarations and canonical modifier spelling are unchanged.
