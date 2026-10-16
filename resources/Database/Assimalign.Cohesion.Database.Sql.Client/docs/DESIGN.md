# Database.Sql.Client — Design

## Character metadata (#1437)

SQL result-header format 2 carries name, DatabaseType and nullable MaxLength. Both SqlColumn and
DatabaseClientColumn retain the length. A CHAR(n) column reports Character (19) with n; its row
value remains the padded CLR string, so GetString/GetValue and parameter round trips preserve
padding. The header's Int32 -2 discriminator refuses older payloads. Rebuild client and server
together; the shared handshake and row tuple encoding are unchanged.

The SQL client owns parameter encoding, response decoding and materialization,
typed commands/results, and error/telemetry mapping. It delegates dialing,
startup/authentication, framing, and pooling to the shared client.

## Family position

The SQL client references shared mechanics and its model's codecs:

```mermaid
flowchart LR
    SqlClient["Database.Sql.Client"] --> Client["Database.Client"]
    SqlClient --> Sql["Database.Sql"]
    Client --> Protocol["Database.Protocol"]
    Sql --> Protocol
```

| Package | Role |
| --- | --- |
| Database.Sql.Client | SQL materialization and typed APIs |
| Database.Client | Pooling, handshake, framed exchange lifetime |
| Database.Sql | SQL identifiers and payload codecs |
| Database.Protocol | Shared framing and immutable family binding |

Other model clients do not depend on this package.

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
namespace. Typed SQL APIs and protocol 1.0 wire behavior are unchanged.

The model reference supplies codecs without parsing SQL or inspecting plans.
Its transitive assembly closure is an accepted packaging consequence; a dedicated
model protocol assembly can be a future refinement. The model's
[design](../../Assimalign.Cohesion.Database.Sql/docs/DESIGN.md) defines wire ownership.

## Typed projection

Column names map to ordinals once, shared by every result record. First column
wins duplicate names; ordinal access reaches every value. Exact runtime types are
returned directly; widening getters use `Convert.ChangeType` and invariant
culture, without reflection. Incompatible access produces `InvalidCast`.

Parameter collection names strip a leading `@` or `$`; bare names travel on the
wire. SQL parsing remains the server's responsibility.

## Lifecycle, errors, and telemetry

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
exceptions do not promise that a failed COMMIT left no writes. The SQL mapper
documents and enforces this boundary by faulting its scope/store and refusing replay;
see its [reconciliation contract](../../Assimalign.Cohesion.Database.Sql.Mapping/docs/DESIGN.md#commit-outcome-and-reconciliation).

`SqlClientException : DatabaseException` maps stable wire codes to SQL error kinds
and retains the original code. The shared pool invalidates incomplete exchanges,
including cancellation and decoder failure. Completed parse/execution failures
keep the connection reusable.

Application function cancellation follows the statement token (#1419): an
`OperationCanceledException` from a scalar, any aggregate phase or a domain CHECK is
`COHSQLE007`, naming the function, unless the server statement's own token is canceled.
This includes application calls inside SQL functions and SQL domains, and plan-time calls in
LIMIT/OFFSET, seek bounds and constant folding. The coded error is
an `ExecutionFailure` with `ConnectionUsable = true`; the next command uses the same
connection. Actual server cancellation retains the existing session teardown behavior.
An explicit transaction also stays active: BEGIN, an earlier write, a coded function failure,
a later write and COMMIT retain both successful writes and none from the failed statement.
The cancellation wire theories cover that sequence and verify its committed state from a second
connection, as well as checking the token on every planning and execution callback.

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
- **Disposal is final for the instance.** The pool rents the same core `DatabaseConnection` to
  the next caller, so `SqlConnection` keeps its own disposed flag, as `GraphConnection` and
  `BlobConnection` do: after `DisposeAsync` or `AbortAsync` every command throws
  `ObjectDisposedException` before the observer runs, and a second dispose or an abort does
  nothing. Before the P5 review a stale `SqlConnection` ran its commands on whichever caller had
  rented the session next, and a second dispose returned that caller's rental
  (`SqlClientTests.QueryAsync_AfterDisposeAndReRent_ShouldThrowObjectDisposedException`).

## E4 SQL definitions

Function/domain DDL and calls use the existing statement transport and server environment without
client ABI or wire-format changes. Dependency/admission refusals retain leading COHSQLD codes in
execution-failure messages and leave connections usable. SqlDomainWireTests exercises SQL-defined
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
catalog mutation. SQL0005 and the column/domain/function parameter or return-type name travel in
the existing ParseFailure message; SqlClientException reports ParseFailure with ConnectionUsable
true. No client ABI or wire format changes. Typed schema and declared-script validation happen at
host declaration/Build, before a server starts; these host APIs have no remote declaration verb.

Recognized but unsupported temporal precision uses COHDBL001 in the same ParseFailure exchange,
including CAST, naming the declaration and modifier. FLOAT(p) returns the resolved Float32
(1..24 bits) or Float64 (25..53 bits) in catalog queries and result headers; bare FLOAT stays
Float64. Invalid FLOAT precision remains SQL0005. INTERVAL field/range qualifiers, including
SECOND(p), likewise report COHDBL001 at every declaration site and CAST, naming the complete
qualifier; no-precision forms are also unsupported. Neither path changes the wire format.
