# Database.Sql.Client — Design

## Partial indexes over the existing exchange (#1630, SQ-P1)

InMemory and TCP execute CREATE INDEX WHERE, predicate transitions, NULL-distinct partial
uniqueness, canonical metadata views and coded predicate refusals. Failed transitions leave
rows intact and connections usable. No client API or protocol format changes are required.


## Foreign-key ON UPDATE actions (#1723, SQ-F1/SQ-F2)

InMemory and TCP execute all five ON UPDATE actions on UPDATE, UPDATE FROM, ON CONFLICT DO UPDATE
and MERGE, for separate and self-referencing tables. The omitted action is NO ACTION. RESTRICT
rejects existing children immediately, without final-parent substitution; NO ACTION accepts a
replacement final parent. CASCADE/SET NULL/SET DEFAULT compose before final validation, and
conflicting assignments preserve CS05085 through the existing diagnostic exchange. Failures leave
rows unchanged and connections usable. UPDATE_RULE retains truthful metadata, including views over
INFORMATION_SCHEMA. No public client or wire format change is required.

## Window ranking, navigation and frames (#1526, SQ-W1/SQ-W2/SQ-W3)

InMemory and TCP use the existing result and diagnostic exchanges for window rankings,
navigation, named definitions and EXPLAIN. Tests pin BIGINT ranks, DOUBLE PRECISION
distributions, INTEGER tiles, numeric navigation default unification, coded placement/name/
bucket and CS05079 budget refusals and reusable connections. ROWS/default-peer aggregate frames,
FILTER, Boolean/statistical and value functions preserve types, NULLs and frame diagnostics on
both transports; EXPLAIN includes frame boundaries and transition visits. RANGE offsets,
GROUPS, every exclusion and IGNORE NULLS use the same exchange. The wire corpus also checks
CS02074-02076 and CS05086-05088 with reusable connections. No client surface or protocol format changes.

## Owned identity wire surface (#1717, SQ-S3)

InMemory and TCP tests cover SERIAL, populated ADD identity, sequence session state,
SET GENERATED, RESTART, both override policies, protected owned-sequence drops and
TRUNCATE RESTART IDENTITY. Identity/backfill allocations report retained session state so
the existing pool reset clears currval/lastval on physical connection reuse. No client API
or protocol changes are required.

## Sequence wire surface (#1717, SQ-S2)

The ordinary execute exchange carries sequence DDL, functions, NEXT VALUE FOR, EXPLAIN and INFORMATION_SCHEMA.SEQUENCES on InMemory and TCP. Undefined session state and explicit-transaction DDL refusals remain structured execution failures with usable connections. Session currval/lastval survive rollback and remain isolated between logical connections: pool return clears them with the family-declared DISCARD ALL only when Ready reports retained state, after any required rollback. The two-byte Ready reports transaction state and retained state. A one-slot pool and queued waiter exercise exact exchange counts on both transports: idle 0, SELECT 1, BEGIN plus return 2, nextval plus reset 2, and nextval plus user DISCARD 2. Existing client methods and protocol identifiers remain unchanged.

## Terminal TCP conformance (#1087)

The real SqlClient and model server run missing-database handshakes on InMemory and
loopback TCP for at least one second and 200 consecutive coded refusals per transport. The
tests assert DatabaseNotFound and ConnectionFailure kind and reuse a one-slot pool to expose leaked failed rentals.
TCP also covers 200 authentication and capacity refusals through the typed client, 200
unsupported-major handshakes through the public protocol (production clients send Current),
and 200 idle and graceful-stop notices surfaced by the next concrete-client operation.
An in-memory regression returns an authenticated rental to its pool and leaves the client open
and unread throughout soft stop or idle eviction. Server completion must precede outer-budget
abort, and idle eviction must release the sole session slot for a fresh client after linger.

## Contract-only dependencies

Direct references are Database.Client, Sql.Protocol, Database.Types, Database.Protocol and Connections. Wire contracts live in each model Protocol assembly, shared by client and engine. This client has no engine, area-root, storage, transaction, indexing, model parser or schema reference. Cache.Client remains an empty placeholder. Engine-free trimming and NativeAOT coverage lives in Database.Client/samples/Assimalign.Cohesion.Database.Client.AotSample.

## MERGE (#1715, SQ-M2/SQ-M3)

The existing exchange carries MERGE actions, BY SOURCE and RETURNING with merge_action().
InMemory/TCP tests assert new UPDATE/INSERT targets, old DELETE targets, NULL absent-source
cells and reusable CS05036 failures with unchanged rows. The final-state corpus covers moved
children, overlapping CASCADE/SET NULL/SET DEFAULT writes, CHECK/unique/FK refusals and late
RETURNING rollback while preserving earlier transaction writes. Recorded row-lock waits drive
concurrent UPDATE/DELETE commits and assert CS04064 under wire BEGIN's default Snapshot
isolation, with reusable connections. ReadCommitted rechecks are exercised through the engine
transaction API. No client API or protocol changes.

## TRUNCATE (#1716, SQ-T1)

The existing execute exchange carries table lists, FK policies and identity policies with zero
completion count. InMemory and TCP tests pin old rows after publication, CS03067 for incomplete
FK sets, CS04066 inside BEGIN, identity continuity/restart, and usable connections after refusal.
Engine-owned wire tests also run reclamation while the old snapshot remains active and verify
no frees until it ends. No client surface, result shape or protocol version changes.

## Quantified comparisons (#1718)

Grouping sets (#1720, SQ-H2) use the existing execute exchange. InMemory and TCP
tests cover subtotal versus stored NULL masks, GROUPING_ID, HAVING/ORDER BY,
FILTER/DISTINCT and Boolean/statistical/string aggregates, empty grouping sets,
EXPLAIN expansion and reusable CS01062 failures. Mask metadata is non-null INTEGER;
subtotal grouping columns remain nullable. No client API or wire format changes.

The existing execute exchange carries scalar ANY/SOME/ALL subqueries without a
protocol change. InMemory and TCP tests measure all comparison operators against
empty, TRUE/FALSE/UNKNOWN and mixed sets, NULL operands, correlated scopes,
CS01015/CS02055/CS05013 failures and connection reuse. UNKNOWN is delivered as a
NULL Boolean cell through the ordinary result codec.

## Client exception ownership

`SqlClientException` derives directly from `System.Exception`. It preserves constructors, wire codes, inner exception and connection-usability behavior. Its locally declared Diagnostics is never null: explicit diagnostics are defensively copied into a read-only snapshot, stably ordered by offset with unlocated entries last. SQL and Graph wrappers copy from DatabaseClientException or their own nested client exception. Documents retains its explicit diagnostics parameter and returns an empty collection when none was supplied. Engine-side DatabaseException remains in the area root; catching it intentionally no longer catches remote client failures.

CREATE/DROP SCHEMA (#1030) travel through the existing execute exchange. Wire coverage pins
cross-connection namespace visibility, RESTRICT failure, CASCADE's retained external table,
and stable missing/reserved/protected namespace and unsupported-syntax diagnostics. Errors
leave connections usable; no client API or payload format changes.

ALTER TABLE rename/default/nullability actions (#1122) use the existing execute exchange.
Wire tests pin renamed query results, signed defaults on omitted inserts, and failed NOT NULL
validation as ExecutionFailure with a reusable connection. No client API or payload changes.

Non-recursive WITH queries and INSERT/UPDATE/DELETE (#1116) travel through the
existing SQL execute exchange. Wire tests pin results, exact affected counts and
modifying-body ParseFailure diagnostics, preserving connection reuse
and stored rows after rejection. WITH RECURSIVE (#1719, SQ-R1/SQ-R2) executes UNION ALL and DISTINCT fixpoints, shared scans and outer LIMIT demand. InMemory/TCP regressions cover values, NULL identity, EXPLAIN iteration/row counters, CS02066/CS02067 compatibility errors, CS05067 memory refusal and connection reuse. Invalid anchor references retain CS01055. Windows over finite fixpoints execute. For unbounded inputs, blocking consumers that retain input refuse with CS05067 when they exhaust StatementMemoryBudget; bounded-state consumers run until cancellation, including infinite COUNT and top-K sorts. No client API or wire payload changes.

Blank derived output names fail planning with CS05013 and arrive as an
ExecutionFailure with ConnectionUsable true. The connection and an explicit
transaction remain usable; the wire regression verifies pending writes and rollback.

Boolean predicate errors (#1190) retain ExecutionFailure, CS02003 diagnostics and
connection reuse over InMemory and TCP. Coverage includes every clause, supplied string
parameters and explicit transactions; failed writes preserve rows. No protocol changes.

## Constraint diagnostics (#1666)

Execution, conversion, persisted-expression and DDL failures also retain structured diagnostics
(#1653): CS02046–066, existing CS02022/024 and definition CS03302/003. The engine
discards persisted-expression offsets before attaching the statement span; the wire composes the code prefix
once. Both transports cover execution and DDL refusals; engine-owned wire regressions also
include a damaged stored DEFAULT and a default-conversion wrapper, preserving the carrier,
ExecutionFailure and a reusable connection. SQLSTATE mappings
remain documented in the SQL dialect; no client API or protocol format changes.

SqlConnection preserves the existing ExecutionFailure category and exposes the single
constraint diagnostic through SqlClientException.Diagnostics: CS03037 (23505) for
uniqueness, CS03038 (23514) for CHECK/domain checks, CS03039 (23503) for foreign
keys; NOT NULL stays CS03024 (23502). Read Diagnostics[0].Code for the engine code;
SqlClientException.Code remains the protocol error category. Negotiated diagnostics retain
the statement span and message. Without diagnostics negotiation, the composed message
still starts with the engine code. The connection and explicit transaction remain usable.
Both-transport tests assert rollback of earlier rows in multi-row INSERT, INSERT SELECT and
upsert failures. There is no API or wire-format change.

## FULL joins (#1525)

InMemory and loopback TCP conformance use the ordinary result codec for duplicate/NULL keys,
both unmatched bags, common-typed COALESCE USING outputs and empty inputs. EXPLAIN identifies
FullHashJoin. Physical eligibility CS05070, direct joined-DML CS05069 and statement hash-budget
CS05067 preserve structured diagnostics and connection reuse; derived FULL DML sources execute.
No client API, protocol version or header nullability field changes.

## Joined DML (#1714)

UPDATE FROM and DELETE USING use the existing command/result exchange. Both InMemory and TCP
cover source SET/WHERE/RETURNING, visible star metadata and LEFT null extension, affected counts,
empty results, DELETE deduplication, constraint rollback and CS05068 before writes. Multiple UPDATE
source matches leave the connection and earlier explicit-transaction work usable. No client API,
protocol version, frame or metadata format changes.

## UPDATE final foreign-key state (#1667)

Both transports pin dangling-reference rejection without writes, moving self-references,
and consistent mutually referenced key swaps with NO ACTION under Snapshot and ReadCommitted.
RESTRICT now refuses existing references immediately, including direct self-reference reassignment (#1723). Tests begin through the existing server-session API to choose isolation;
the UPDATE, error, result and COMMIT exchanges all use the wire. No protocol changes.

## INSERT ... ON CONFLICT (#1118)

Existing execute/query exchanges carry inserted-plus-updated affected counts and RETURNING
rows. DO NOTHING-skipped proposals return no row. Wire tests cover InMemory and TCP,
untargeted inserts without unique indexes, and reusable connections after arbiter,
repeated-row and constraint failures (ExecutionFailure) or excluded spellings (CK00001
ParseFailure). There is no client API, payload or protocol change.

## Derived relations and DML predicate subqueries (#1113, part b)

The existing query and command exchanges carry derived-table results and
UPDATE/DELETE predicate-subquery affected counts. Server materialization preserves
the existing column metadata and error taxonomy; no wire format changes. The
subquery wire corpus covers a renamed derived join and both DML statement kinds.

FROM-less SELECT (#1373) uses the existing query exchange, including scalar/IN/EXISTS
children and aggregate results. Both transports verify the implicit row and empty filters.
No client API or wire payload changes.

## Correlated children (#1113, part a)

Existing exchanges carry per-outer-row EXISTS, IN and scalar results and correlated
DML affected counts without a protocol change. A later outer row's scalar cardinality
failure arrives as ExecutionFailure with ConnectionUsable true; the next query on
the same connection succeeds. NativeAOT checks exercise correlation in process and
through the client.

## Parameter contract (#1102)

SqlParameterCollection strips one leading sigil and keeps ordinal case-sensitive bare
names; `$name`, `@name` and numeric names such as `$1` follow the
[shared parameter contract](../../Assimalign.Cohesion.Database.Language/docs/DESIGN.md#parameter-contract-1102).
Add and the indexer continue to replace a previously normalized name. A raw request
map containing two keys for one bare name is rejected by the server with CS01023.
An unbound reference fails with CS01020 before rows are accessed; wire execution
failures keep the connection usable, including on empty tables. No protocol changes.


## Character metadata (#1437)

SQL result-header format 2 carries name, DatabaseType and nullable MaxLength. Both SqlColumn and
DatabaseClientColumn retain the length. A CHAR(n) column reports Character (19) with n; its row
value remains the padded CLR string, so GetString/GetValue and parameter round trips preserve
padding. The header's Int32 -2 discriminator refuses older payloads. Rebuild client and server
together; the shared handshake and row tuple encoding are unchanged.

The SQL client owns parameter encoding, response decoding and materialization,
typed commands/results, and error/telemetry mapping. It delegates dialing,
startup/authentication, framing, and pooling to the shared client.

Expression defaults and referential SET actions (#1121) use existing result frames.
Client coverage verifies arithmetic ADD COLUMN backfill, SET NULL visibility on a second
connection, and CURRENT_TIMESTAMP assigned to TIMESTAMP with DateTime metadata and value.
Cancellation coverage distinguishes volatile defaults evaluated during writes from immutable
defaults evaluated during DDL; both use the current operation token, and failed immutable
declarations publish no table.

## Family position

The SQL client references shared mechanics and its model's codecs:

```mermaid
flowchart LR
    SqlClient["Database.Sql.Client"] --> Client["Database.Client"]
    SqlClient --> Sql["Database.Sql.Protocol"]
    Client --> Protocol["Database.Protocol"]
    Sql --> Protocol
```

| Package | Role |
| --- | --- |
| Database.Sql.Client | SQL materialization and typed APIs |
| Database.Client | Pooling, handshake, framed exchange lifetime |
| Database.Sql.Protocol | SQL identifiers and payload codecs |
| Database.Protocol | Shared framing and immutable family binding |

Other model clients do not depend on this package.

## Set operations (#1114)

Set roots and query operands use the existing execute exchange with common-typed column
names and values. InMemory and TCP cases exercise UNION ALL, INTERSECT and EXCEPT ALL,
combined OFFSET/FETCH, IN bodies and reusable coded parse/planning failures. Result metadata
still carries no nullability field. No client API or wire format changes.

## Explicit NULL placement (#1112)

The existing execute exchange carries results ordered with per-key NULLS FIRST/LAST,
including outer ordering over derived outputs. Wire tests assert all four direction and
placement combinations; GROUP BY ordinals keep CK00001 with a reusable connection.
No protocol or client API change is needed.

## Pagination (#1107)

OFFSET/FETCH, OFFSET before LIMIT and TOP use the existing SQL execute exchange and
result metadata. SqlPagingWireTests verifies exact windows and column names/types over
InMemory and TCP, plus coded parse/count refusals and subsequent connection reuse.
INSERT, UPDATE and DELETE TOP retain CK00001 naming DML TOP; bare FETCH arithmetic
receives CS00003, matching the language grammar.
No wire or client API changes.

## Expanded joins (#1109)

LEFT null values, N-way, CROSS, comma and USING results use the existing SQL frames and
production materializer. No nullability field or frame-writing change is introduced.
Wire tests run every join case over InMemory and loopback TCP, pin exact values and
multiplicities, and retain FULL/NATURAL and qualified-wildcard exclusions. RIGHT [OUTER] and RIGHT USING (#1525) carry null-extended written-left values, merged right spelling, coerced decimal values, original column ordinals and cleared lengths on both transports; direct joined-DML refusals carry CS05069 and keep the connection usable. Engine nullability remains engine metadata; no nullability field is added to the wire. USING
wildcard alias/ordinal ordering, decimal common-type values in both input orders, qualified
types and cleared character lengths use the same production frames and materializer.

## System relation joins (#1106)

Stored/system, system/system and grouped catalog joins use the existing SQL exchange.
Wire tests pin exact rows and String/Int64 metadata; there is no protocol format change.

## Identity values (#1123)

Identity DDL, omitted-column INSERT and typed generated results use the existing execute
exchange and integer codec. ALWAYS violations carry CS03025 as ExecutionFailure and leave
the connection usable. No client API or protocol shape changes.

## T1 scalar and clock wire behavior (#1120)

The 42 scalar names and temporal arithmetic use existing SQL value and metadata
frames. Wire tests cover every family, UTC transaction clock equality and a coded
SQRT domain failure followed by successful reuse of the same connection. No wire
format or client API changes.

## SQL-owned exchange

`SqlClient.Create` binds the pool to `SqlProtocol.Family`.
`SqlExecuteExchange` derives from `DatabaseProtocolExchange<DatabaseClientResult>`:
it encodes parameters with `DatabaseValueCodec`, writes the execute request,
consumes SQL's header/data/completion exchange, and materializes values.
`SqlConnection` projects this result into `SqlResultSet`.

`SqlProtocolConnectionExtensions`, an `extension(DatabaseConnection)` block, provides
the same operation as `ExecuteAsync` on a rented `DatabaseConnection` bound to SQL's
exact family instance. It stays an extension because the connection lives in
Database.Client, which references no model package.
`DatabaseClientResult` and `DatabaseClientColumn` moved to this package's
namespace. Typed SQL APIs and protocol 2.0 wire behavior are unchanged.

`Database.Sql.Protocol` supplies codecs without parsing SQL or inspecting plans.
Both engine and client consume that contract assembly independently; the client's
dependency closure contains no SQL engine, catalog or storage implementation. The model's
[design](../../Assimalign.Cohesion.Database.Sql/docs/DESIGN.md) defines wire ownership.

## Typed literal wire values (#1110)

DATE, TIME, TIMESTAMP, TIMESTAMPTZ, day-time INTERVAL and X binary literals use the existing
DateOnly, TimeOnly, DateTime, DateTimeOffset, TimeSpan and byte[] tuple encodings and DatabaseType
metadata. N strings remain String. No protocol format changes. CS00008 content errors and
CK00001 year-month exclusions arrive as ParseFailure and leave the connection usable.
TIMESTAMP assignment returns Kind Unspecified; TIMESTAMPTZ preserves its offset, following the
[dialect normalization rule](../../Assimalign.Cohesion.Database.Sql.Language/docs/DIALECT.md#timestamp-normalization-1110).

## Explicit casts (#1119)

The expanded built-in CAST matrix uses the existing scalar type tags and result metadata.
Wire tests cover float, temporal, interval, UUID, BYTEA, JSON and JSONB values, canonical text,
conversion refusals and connection reuse after a floating arithmetic overflow. JSONB remains
validated UTF-8 bytes with byte equality; there is no new wire encoding.

## RETURNING and affected counts (#1117)

ExecuteAsync receives and buffers RETURNING rows before discarding them and returning
the affected count, so it has the same row-transfer and buffering cost as QueryAsync.

SqlResultSet preserves ResultComplete's long AffectedCount: -1 for SELECT, n for a
write including RETURNING. QueryAsync is the rows-plus-count verb, ExecuteAsync returns
the count and discards rows, and ExecuteScalarAsync reads the first returned cell.
Observers see both row and affected counts. The existing exchange already materializes
both values; no new verb or frame is needed. Server-side projection errors precede any
result header and leave the connection usable. Results survive a later ROLLBACK.

## Typed projection

Column names map to ordinals once, shared by every result record. First column
wins duplicate names; ordinal access reaches every value. Exact runtime types are
returned directly; widening getters use `Convert.ChangeType` and invariant
culture, without reflection. Incompatible access produces `InvalidCast`.

Parameter collection names strip a leading `@` or `$`; bare names travel on the
wire. SQL parsing remains the server's responsibility.

## Lifecycle, errors, and telemetry

The typed connection captures its immutable database name when it receives the shared lease
(#1313). Operation diagnostics can finish after disposal returns that lease, whose members then
refuse access. The cached name keeps diagnostics independent of the returned rental.

Disposing a typed connection returns its shared authenticated session; disposing
the client closes its pool. Connections support one exchange at a time.

`SqlConnection.AbortAsync` discards a rental and closes its session instead of
returning it to the pool. Use it when SQL session state cannot be reset, including
a failed ROLLBACK or uncertain COMMIT. It delegates to the shared client's discard
operation and is not cancellable. Discarding prevents session leakage; it cannot
undo an already published transaction.

SQL transactions currently use ordinary `BEGIN`, `COMMIT` and `ROLLBACK` commands
on one connection. The wire protocol does not expose a durable transaction token,
status lookup or replay deduplication. A connection loss after the server publishes
COMMIT but before the response reaches the client has an unknown outcome. Client
exceptions do not promise that a failed COMMIT left no writes. Callers must reconcile
an uncertain outcome through application state before deciding whether to retry;
the client provides no automatic replay or reconciliation operation.

**Pool reset (#1540):** negotiated server status certifies a clean session. Return rolls back
an unfinished transaction before pooling; reset failure or legacy status support discards the
connector. `DisposeAsync_WithExplicitTransaction_ShouldRollbackBeforeNextRental` is enabled. A disposed core lease rejects SQL extension calls permanently,
even when its authenticated session is rented again.

In-process sessions carry engine codes in `DatabaseException.Diagnostics` (#1103).
The SQL server preserves the existing `CODE: message` Error text. On a negotiated 2.1 diagnostics session, client exceptions also retain the complete structured carrier. Cancellation tests that execute sessions directly
assert structured codes, while their wire counterparts keep their original text assertions.
CAST value failures follow the same contract: invalid text carries CS02022 (PostgreSQL
22P02), numeric overflow CS02002 (22003), and division by zero CS02001 (22012).
Protocol 2.0 clients retain the composed code prefix and ExecutionFailure category; a value
failure keeps the connection usable. In-process regression tests assert code and source span.

Negative and fractional LIMIT/OFFSET counts retain CS02018 in the composed wire text.
The Int64 row-window tests (#1100) assert that prefix alongside ExecutionFailure and connection
reuse, aligning their expected text with the carrier convention added by #1103.

NOT NULL failures retain CS03024 (SQLSTATE 23502) in the composed wire message,
with the unchanged column-specific missing-default or explicit-NULL detail. DEFAULT
VALUES, DEFAULT items and UPDATE DEFAULT use that same contract. SqlInsertValuesWireTests
covers success, syntax rejection and constraint rejection over InMemory and loopback TCP,
then queries and writes on the same connection to verify it remains usable.

`SqlClientException : Exception` maps stable wire codes to SQL error kinds
and retains the original code. The shared pool invalidates incomplete exchanges,
including cancellation and decoder failure. Completed parse/execution failures
keep the connection reusable.

An ExecutionFailure after ResultHeader or ResultRow also completes the statement (#1651).
The exchange discards buffered columns and rows, retains diagnostics and the original error,
and marks the response complete so the same connection remains usable. The server pre-encodes
writing statements' headers and rows before commit, inside DML's undo bracket, so an encoding
failure rolls back the statement. Read-only statements encode during delivery and may fail after
partial result frames. Both paths preserve the explicit transaction and connection. Uncoded
result failures and size refusals use CS05045 (size refusals: SQLSTATE 54000). Each header/row
has a 16 MiB frame limit; there is no aggregate result cap. Writes retain prepared payloads until
delivery; reads retain only the current encoded frame. Transport failures during delivery are
terminal. ParseFailure after result frames and terminal errors still invalidate the rental.

Application function cancellation follows the statement token (#1419): an
`OperationCanceledException` from a scalar, any aggregate phase or a domain CHECK is
`CS05007`, naming the function, unless the server statement's own token is canceled.
This includes application calls inside SQL functions and SQL domains, and plan-time calls in
LIMIT/OFFSET, seek bounds and constant folding. The coded error is
an `ExecutionFailure` with `ConnectionUsable = true`; the next command uses the same
connection. Actual server cancellation retains the existing session teardown behavior.
An explicit transaction also stays active: BEGIN, an earlier write, a coded function failure,
a later write and COMMIT retain both successful writes and none from the failed statement.
The DEFAULT cancellation fixture uses a separately named Immutable faulting scalar,
retaining default-evaluation cancellation/token and connection-reuse coverage.
Column DEFAULT expressions also admit Stable/Volatile functions since #1121.
The cancellation wire theories cover that sequence and verify its committed state from a second
connection, as well as checking the token on every planning and execution callback.

Definition DDL's mandatory post-commit refresh uses CancellationToken.None (#1231). Caller
cancellation cannot undo that publication or turn its in-process result into failure. A wire
disconnect can lose the response while the definition remains committed. Later function/domain
execution receives its own statement token; pre-commit table/default binding remains cancellable.

A failed dial reaches `ConnectAsync` as the core's `DatabaseClientException` with
`ProtocolErrorCode.ConnectionFailure` (owner decision 39; the core's `DESIGN.md`,
"Lifecycle and errors"). The mapper translates it like a handshake rejection, to
`SqlClientErrorKind.ConnectionFailure` with `ConnectionUsable` false, and the core
exception, which keeps the transport's exception, is the inner exception. A canceled
dial throws `OperationCanceledException` unchanged (`SqlClientDialFailureTests`).

Observers receive synchronous primitive callbacks before execution, on completion,
and on failure. Observer exceptions are swallowed so telemetry cannot change the
command outcome. An observer derives from the abstract `SqlClientObserver`, whose
three hooks are `protected internal virtual` with empty bodies: an observer overrides
only the hooks it records, and only the owning `SqlConnection` fires them.

## Diagnostics

The client reports through one internal event source named for its assembly,
`Assimalign.Cohesion.Database.Sql.Client` (`src/Internal/EventSource/SqlClientEventSource.cs`),
written by `SqlConnection`'s one command path. Start and stop carry the `Commands` keyword (`0x1`).
The Key-Value client's source has the same ids, names and payloads, so one query reads both.

| Id | Event | Level | Keyword | Payload |
| --- | --- | --- | --- | --- |
| 1 | `CommandStart` | Verbose | `Commands` | `database`, `parameterCount` |
| 2 | `CommandStop` | Verbose | `Commands` | `database`, `status` (`Success`, `Error` or `Cancelled`), `rowCount`, `affectedCount` (both -1 unless `Success`), `durationMilliseconds` |
| 3 | `CommandFailed` | Error | — | `database`, `errorKind` (`SqlClientErrorKind`; empty for an uncoded failure), `code` (the wire code; empty for an uncoded failure), `exceptionType`, `durationMilliseconds` |
| 4 | `ObserverFailed` | Warning | — | `database`, `callback` (`OnExecuting`, `OnExecuted` or `OnFailed`), `exceptionType`, `exceptionMessage` |

**Every start has a stop** (the area's convention, `docs/resources/Database/DESIGN.md`,
"Diagnostics"): the command writes its end from a `finally`, before its observer hears of it. A
failure writes event 3 and then `CommandStop` with `Error`: a coded failure the server or the
connection raised (`SqlClientException`, with its kind and code) or an uncoded one (an overlapping
exchange, a disposed connection: kind and code empty). A cancellation, which is how a timeout
surfaces, writes `CommandStop` with `Cancelled` and no failure. The failure is captured by an
exception filter that declines it and written from the `finally`, so it reaches the caller unchanged.
A command fails with an Error whatever its cause, a statement the server rejects included (owner
question Q1 of the event-source plan). As `System.Net.Http`'s `RequestStop`, `CommandStop` is
written only for a command whose `CommandStart` was written, so a listener that attaches mid-command
sees no stop without its start; event 3 is written either way.

**The failure rule.** The statement text and the parameter values are never written (Q3), and nor is
the server's message: a parse error's quotes a fragment of the statement, a literal included
(`Malformed numeric literal '…'`), so event 3 writes the error kind, the wire code and the
exception's type only (the area's failure rule). Event 4 makes visible an observer failure the
client swallows; the command's outcome is unchanged, and the hook's own exception keeps its message.
The connection itself is the shared core's (`Assimalign.Cohesion.Database.Client`). No counters: a
process-wide count updated per command would be a contention point. The command path already takes
the timestamp its observer receives, so the events add only `IsEnabled` checks while nobody listens.

`SqlClientEventSourceTests` checks the name, the strict manifest, a succeeding and a failing command
under an observer whose every hook throws (each event once, in order, with its payload, the failure's
stop after it, no statement text and no server message), a cancelled command (a `Cancelled` stop and
no failure), that start and stop need the `Commands` keyword, and, by direct writes, an uncoded
failure (empty kind and code) and a stop written only after a written start. The test assembly's
observers override the hooks as `protected internal`, which the project's test-only
`InternalsVisibleTo` requires (CS0507); an application overrides them as `protected`.

## AOT and non-goals

Encoding and materialization are hand-written, with no reflection or code
generation. Transports are typed options, never string-named plugins. Full result
materialization is SQL client policy; an incremental API can be added here
independently. Parsing, ORM behavior, retries, and explicit wire transactions
remain outside the current surface.

## Concrete types (concrete-types plan, phase 5, #1261)

The package has no public interface left and no `Abstractions/` folder
([plan](../../../../docs/programs/DATABASE_CONCRETE_TYPES_PLAN.md) §7, "P5, as landed").

| Type | Shape | Was |
|---|---|---|
| `SqlClient` | sealed; `Create(SqlClientOptions)` over a private constructor | the static `SqlClient` factory, `ISqlClient` and the internal `DefaultSqlClient` |
| `SqlConnection` | sealed; internal constructor; moved out of `Internal/` | `ISqlConnection` and the internal `SqlConnection` |
| `SqlClientObserver` | abstract; protected constructor; `protected internal virtual` hooks with empty bodies | `ISqlClientObserver` |
| `SqlProtocolConnectionExtensions` | `extension(DatabaseConnection)` block | an old-style `this IDatabaseConnection` extension |

- **Why the observer is abstract.** It is an inverted seam (`database-area.md`, rule 2): the
  application supplies it through `SqlClientOptions.Observer` and the connection fires it; no
  observer ships. Its constructor is protected because applications derive from it (rule 3),
  and it carries the deviation marker. Because the hooks have empty bodies, an observer overrides
  only what it records; one that implemented the interface had to implement all three.
  `SqlClientTests.QueryAsync_WithPartialThrowingObserver_ShouldKeepEachCommandOutcome` covers a
  one-hook observer that throws.
- **Typed surface.** `ConnectAsync` returns the sealed `SqlConnection`, which wraps the core's
  sealed `DatabaseConnection`; nothing casts. `Settings` reads the core's settings.
- **What changed for a caller.** `ISqlClient` and `ISqlConnection` become `SqlClient` and
  `SqlConnection` (the Sdk.ApplicationModel `EnabledWeb` fixture and Studio's SQL workspace were
  retyped), and an observer overrides `protected` hooks instead of implementing public ones.
  An observer cannot forward to another observer instance: outside this assembly the hooks are
  protected (CS1540), so an application that needs several sinks fans out inside one subclass
  (plan §7, "P5, as landed", owner review 38).
  The sealed `SqlConnection` shares its name with `Microsoft.Data.SqlClient.SqlConnection`; a
  file that imports both namespaces aliases one (plan §11, R11).
- **Disposal is final for the instance.** The pool now reuses an internal connector behind a new core lease on
  every rent (#1313); `SqlConnection` keeps its own disposed flag, as `GraphConnection` and
  `BlobConnection` do: after `DisposeAsync` or `AbortAsync` every command throws
  `ObjectDisposedException` before the observer runs, and a second dispose or an abort does
  nothing. Before the P5 review a stale `SqlConnection` ran its commands on whichever caller had
  rented the session next, and a second dispose returned that caller's rental
  (`SqlClientTests.QueryAsync_AfterDisposeAndReRent_ShouldThrowObjectDisposedException`).

## Aggregate modifiers (#1115)

FILTER and statistics (#1720, SQ-H1): InMemory and TCP conformance exercise filtered COUNT,
DISTINCT, ordered STRING_AGG/LISTAGG, Boolean aliases, exact and floating statistics, empty
results and EXPLAIN through the existing scalar codecs. Nullable Boolean/Decimal/Double
metadata is preserved. No client ABI or wire format changes.

COUNT/SUM DISTINCT and ordered STRING_AGG use the existing statement/result framing.
The live wire corpus verifies values, nullable String metadata and independent aggregate
slots; excluded grouping/window forms continue to report usable-connection CK00001.
There is no client ABI or protocol change.

## E4 SQL definitions

Typed-literal wire theories run over both InMemory and loopback TCP. Temporal, interval,
binary and national-string values retain the same runtime types and metadata. Malformed
literals (CS00008), year-month forms (CK00001) and interval overflow retain their parse
diagnostics and permit a subsequent query on the same connection.

Function/domain DDL and calls use the existing statement transport and server environment without
client ABI or wire-format changes. Dependency/admission refusals carry categorized `CS` identities
(`CS033xx` for registered definitions) in diagnostics and wire execution-failure text, as assigned
by the [Database error-code registry](../../Assimalign.Cohesion.Database/docs/ERROR-CODES.md), and leave connections usable. SqlDomainWireTests exercises SQL-defined
functions in SELECT, WHERE, CHECK and DEFAULT alongside SQL domain enforcement.

## Duplex transport composition

`ConnectionFactory` accepts `IDuplexConnectionFactory`. The shared client reads and writes
framed bytes through the returned `IDuplexConnection`; the common `IConnection` root carries
identity, features and lifecycle only. TCP, InMemory and TLS-layered duplex factories satisfy
this contract. The pool owns each established connection and disposes it when discarded or
when the pool closes; transport disposal has signaled `ConnectionClosed` before it completes.

### Breaking changes from 10.0.0-preview.1 (#1459)

`SqlClientOptions.ConnectionFactory` changes from `IConnectionFactory?` to
`IDuplexConnectionFactory?`. Changing the property's getter and setter signatures breaks
preview.1 binaries and source that supplies a factory typed only as `IConnectionFactory`.
This preview break is accepted under owner decisions 15 and 58. Rebuild consumers and use
`IDuplexConnectionFactory` for factory variables and custom implementations. Custom factories
can derive from `DuplexConnectionFactory`, implement `ConnectCoreAsync` returning
`ValueTask<IDuplexConnection>`, and produce a `DuplexConnection` implementation. Built-in TCP,
InMemory and TLS-layered duplex factories already satisfy the new contract. Database wire
framing, handshake and model messages are unchanged.

## Declaration modifier failures (#1438)

The server parses every SQL declaration and CAST against the shared modifier ranges before any
catalog mutation. CS00005 and the column/domain/function parameter or return-type name travel in
the existing ParseFailure message; SqlClientException reports ParseFailure with ConnectionUsable
true. No client ABI or wire format changes. Typed schema and declared-script validation happen at
host declaration/Build, before a server starts; these host APIs have no remote declaration verb.

Recognized but unsupported temporal precision uses CK00001 in the same ParseFailure exchange,
including CAST, naming the declaration and modifier. FLOAT(p) returns the resolved Float32
(1..24 bits) or Float64 (25..53 bits) in catalog queries and result headers; bare FLOAT stays
Float64. Invalid FLOAT precision remains CS00005. INTERVAL field/range qualifiers, including
SECOND(p), likewise report CK00001 at every declaration site and CAST, naming the complete
qualifier; no-precision forms are also unsupported. Neither path changes the wire format.

## Negotiated statement diagnostics (#1105)

The shared protocol is 2.1. Startup optionally appends the client's capability names;
Ready returns the agreed version and the two-sided intersection. A negotiated 2.0
session retains empty Ready and the existing model and Error bytes. An empty Ready
from an older endpoint means 2.0 with no capabilities. Different majors are refused.
Capability names are ordinal and append-only; model names are scoped with the
lowercase endpoint family followed by a dot. The core owns `diagnostics` and frame 14.

SQL and Graph register `ProtocolCapability.Diagnostics`. When agreed, one Diagnostics
frame carries the complete Warning/Error list immediately before Error on failure,
or immediately before ResultComplete (PathsComplete for graph paths) on success.
Information, including CS00009, is never sent. Error and result payloads are unchanged.
Statement failures leave the server session ready. Model clients validate diagnostic
ordering and preserve every code, severity, message, nullable offset and source line.

DatabaseClientResult.Diagnostics and SqlResultSet.Diagnostics expose negotiated ProtocolDiagnostic values. SqlClientException retains the shared core exception and copies its existing diagnostics carrier.

Set-operation type mismatches reuse CS02003 (42804), retaining the one-based output
position and both types. INSERT set-source width/type errors use the same exchange and
leave the connection usable.

## CREATE TABLE AS (#1716, SQ-T2)

CTAS uses the existing Execute request and affected-row completion, with no client ABI,
protocol or format change. InMemory and TCP tests verify inferred/renamed copies, WITH NO DATA,
conversion cleanup, structured CS03068 output-name refusals, CS04003 explicit-transaction
refusal with prior writes preserved, and connection reuse after every refusal. Publication is
atomic; as with other committed DDL, a lost response can leave the committed table present.
Disposing a running server session during a CTAS function call cancels its unpublished build;
both transport tests await session cleanup and successfully retry from a new connection.
Completion reports copied rows for WITH DATA and zero for WITH NO DATA or an IF NOT EXISTS skip.
The engine prepares the actual count before its catalog commit, preserving result-encoding
failure atomicity and committed-result delivery after cancellation.

## Read-only persisted views (#1124)

View DDL, SELECT/JOIN sources and INFORMATION_SCHEMA.VIEWS use ordinary SQL
requests and typed result sets. A table-only write against a view is ExecutionFailure;
the connection stays usable. The wire format and client API are unchanged.

## Distinct, truth tests and LIKE ESCAPE (#1111)

Existing SQL frames carry predicate truth tables and bound ESCAPE parameters on both
transports. Invalid escape sequences (CS02034) and non-Boolean truth tests
(CS02003) are ExecutionFailure responses; data and the connection remain usable.
A volatile application function that raises a stack refusal deterministically pins
execution-time CS05004 over the wire, including a reusable connection.
Invariant LIKE work exhaustion uses the existing located ExecutionFailure diagnostic
CS05040 and leaves the connection reusable (#1166). The real session lifetime token
reaches row-time LIKE without an application function; disconnect cancellation closes
the session on both InMemory and TCP. Its regression holds a hook inside Match,
asserts entry, waits for the server lifetime token after disconnect, then releases
matching. Reverting LIKE token routing must produce StatementStop Error instead of
Cancelled. The test uses AOT-compatible UnsafeAccessor seams without a public API
or friend-assembly grant. Event assertions wait for the matching SessionClosed and StatementStop
records with a hang guard: removal from the server's session table precedes close-event publication.
No client API or protocol format changes are needed.

## Dedicated test actors (#1285)

Deadline-dependent peers and busy test drivers use the linked Storage test helper's LongRunning threads. Ordinary async driver continuations stay on their actor scheduler, without changing SynchronizationContext. Existing deadlines of 25 seconds or more are hang guards. Runner settings impose no parallel-thread cap. See the [scheduling audit](../../Assimalign.Cohesion.Database.Storage/docs/TEST-HYGIENE-AUDIT.md) for every original Task.Run site, the exact journal counters and the physical index-work guards.

Reference consulted: RavenDB test/StressTests/Voron/InvalidReleasesOfScratchPages.cs:39–68 and :71–103 use LongRunning stress drivers. No code was copied.

## Transaction isolation text (#1012)

Outside a transaction block, SET TRANSACTION succeeds with Warning CS04071 through the existing
negotiated diagnostic frame; QueryAsync exposes the warning and leaves Ready idle. InMemory/TCP
conformance verifies the warning and continued usability. No client API or wire format changes.

BEGIN/START isolation modes and early SET TRANSACTION use existing Execute, diagnostic and Ready
frames on InMemory and TCP. READ COMMITTED and READ UNCOMMITTED see a concurrent commit on the next
statement; REPEATABLE READ keeps the begin snapshot. CS04071/CS04072/CS04073 preserve connection
usability and transaction state. A one-slot pool proves physical-session reuse after an early
isolation change, with optional DISCARD ALL, restores optionless BEGIN's Snapshot behavior.
No client API, capability bit or wire identifier changes.


## Server-reported transaction reset (#1540)

SQL/Graph transaction-status negotiation adds an existing Ready I/T/E frame after Execute
completion or a statement error. The shared client drains it before returning, rolls back open
or failed transactions on pool return, and only reuses an Idle session. Cleanup runs outside the
pool lock. Rollback reply and Ready drain each have a five-second internal bound (up to ten
seconds total). Missing/invalid state or legacy transaction-capable peers
are discarded. SQL's formerly skipped pool-rollback regression is enabled. No new wire identifier,
public interface, protocol version or persistent format is introduced.

An idle open wire transaction holds the database writer lock once it has acquired it, until
COMMIT, ROLLBACK, disconnect, pool return or IdleTimeout (30 minutes by default). Keep transactions
short: an idle writer blocks other writers. New clients discard sessions against pre-#1540 servers
that cannot negotiate transaction-status, so those servers are not pooled.

## Sql diagnostic conventions (#1470)

Exception and diagnostic templates use the project's build-generated `ErrorMessages` resources.
Parameterized templates format with the current culture; abort/cancellation templates used in
normal cleanup are cached. The [Database error-code registry](../../Assimalign.Cohesion.Database/docs/ERROR-CODES.md)
assigns this family its two-letter prefix and categorized five-digit identities.
Existing SQLSTATE meanings, numeric wire error kinds, frame layouts and disk formats are preserved.
Structured diagnostics keep their code outside the message. Compiler diagnostic identifiers and
standard .NET argument guards retain their own contracts.

## Rules audit after main cleanup

Private test constants use repository casing. The transaction-outcome guidance is self-contained:
connection loss after COMMIT can remain uncertain, and the client supplies no automatic replay
or reconciliation operation. The removed mapper package is no longer a documentation dependency.
Wire bytes, diagnostics, pooling and public APIs are unchanged.

## Debugger inspection

Concrete source types with explicit nonpublic instance state use a private nested DebugView
and debugger display/proxy attributes. The view is the last member in the same type file and
exposes only reviewed public/protected state through read-only properties. Exception and
EventSource types remain excluded. A type with no state to filter keeps its normal member view;
a type whose base supplies both debugger attributes inherits that view.

Inspection does not perform I/O, take lifecycle locks, mutate state, invoke application callbacks,
or materialize lazy results. The additions are debugger metadata and nested helper types; existing
instance fields, runtime operations, error identities, data formats and protocol contracts do
not change. SDK and Studio source are outside this rule's scope.

Connection inspection reads its existing database name without checking connection health,
taking the shared exchange lock, dialing, or returning a lease. Result views read already
materialized columns/diagnostics and row counts; row and parameter views do not enumerate values
or call typed conversions. Client exchange behavior and wire framing remain unchanged.
