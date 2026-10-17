# Graph client design

The client follows Sql.Client's shape: a sealed client with a `Create` factory over options, a
sealed connection, internal exchanges, and an exception that keeps the wire code. It references Database.Graph for its existing model protocol and graph values, and
Database.Client for the pool, handshake, frame transport, and connection lifetime. It has no
Hosting dependency, reflection, dynamic serialization, or dependency on Microsoft.Extensions.

```mermaid
sequenceDiagram
    participant App
    participant GraphConnection
    participant Pool as Database.Client
    participant Server as GraphDatabaseServer
    App->>GraphConnection: QueryAsync(statement)
    GraphConnection->>Pool: ExecuteAsync(GraphExecuteExchange)
    Pool->>Server: Execute + existing graph payload
    Server-->>Pool: ResultHeader / ResultRow / ResultComplete
    Pool-->>GraphConnection: GraphResultSet
    App->>GraphConnection: QueryPathsAsync(statement)
    GraphConnection->>Pool: ExecuteStreamingAsync(GraphPathsExchange)
    Pool->>Server: ExecutePaths
    Server-->>Pool: Path frames / PathsComplete
    Pool-->>GraphConnection: bounded Stream
    GraphConnection-->>App: GraphPath enumeration
```

## Scalar completion

GraphExecuteExchange derives from DatabaseProtocolExchange, whose run entry clears the completion
evidence before each run. A successful ResultComplete certifies completion. A ParseFailure or
ExecutionFailure received before any result header is the server's complete rejected-statement
response, so the exchange calls MarkResponseComplete and the pool may reuse that session. A malformed response, truncated transport,
cancellation, or error after result output cannot certify completion.

Rows require exactly the advertised number of scalar components. Columns preserve wire names
and shared DatabaseType identities; duplicate names resolve to the first column, with ordinals
available for unambiguous access. Scalar statements fully materialize before returning.
MATCH projection metadata remains the engine's DatabaseType.Null (unknown), because graph
properties may have heterogeneous types. The tuple codec preserves each actual value's runtime
type. SHOW metadata retains its precisely declared catalog types.

GraphClientException preserves the stable ProtocolErrorCode and inner failure. The typed
connection does not translate error codes into connection health decisions: completion belongs
to its exchange and pooling belongs to Database.Client.

A failed dial reaches ConnectAsync as the core's DatabaseClientException with
ProtocolErrorCode.ConnectionFailure (owner decision 39; the core's DESIGN.md, "Lifecycle and
errors"). GraphClientException keeps that code, and the core exception, which keeps the
transport's exception, is its inner exception. A canceled dial throws OperationCanceledException
unchanged (GraphClientDialFailureTests).

WHERE scalar value errors (#1140) arrive as ExecutionFailure text `COHDBG013: message`
before ResultHeader. GraphClientWireTests pins division by zero, overflow and invalid conditions,
then reuse of the exact authenticated pooled session followed by SHOW LABELS. A combined
OR/IN/string/arithmetic case compares wire rows with in-process rows. No wire fields change.

## Path lifetime

GraphPathsExchange derives from DatabaseStreamingExchange. OpenCoreAsync sends ExecutePaths and
validates the initial Path or PathsComplete frame. CopyToCoreAsync validates each existing path
payload and verifies the final path count. Its private stream handoff prefixes each payload
with its length to preserve boundaries through bounded byte buffering; this is entirely within
the client and does not alter the graph wire protocol. GraphConnection decodes those same
payloads into GraphPath values.

The shared ExecuteStreamingAsync owns frame I/O, applies backpressure, and keeps the connection
busy through completion. Consuming EOF verifies terminal completion before another operation.
Disposing an unfinished enumeration cancels the worker and discards the rental. Stream errors,
including an initial server rejection, discard the connection under the existing shared
streaming contract. This is intentionally stricter than materialized statement rejection;
DatabaseStreamingExchange has no failed-response completion capability.

The client holds a bounded number of path payloads for materialization plus the shared bounded
byte buffer. The engine currently materializes its path result, so end-to-end server memory still
depends on the number of matched paths. A single path must fit the existing maximum frame size.

## Supported surface and limits

ExecutePaths accepts exactly one bound path, node, or relationship projection from a read-only
MATCH. A node becomes a singleton path; a relationship includes its stored-direction endpoints;
a named path retains traversal order. Scalar or mixed projections and mutations are rejected,
so no path is reconstructed from scalar results. Execute serves scalar projection rows and
mutations; bound entity projections direct callers to ExecutePaths.

GraphResultSet.Diagnostics exposes negotiated success warnings, including COHDBG010 and COHDBG011 for unknown names. Path exchanges consume diagnostics before PathsComplete; the existing path enumeration has no result metadata surface. Its statement failures preserve diagnostics on GraphClientException, while the existing streaming contract discards a failed rental.

Graph transactions remain a deliberate limit: there are no connection transaction members;
BEGIN, COMMIT, ROLLBACK, and the reserved Transaction message are unsupported. Mutations retain
the engine's existing execution and validation semantics. Database binding remains fixed at
handshake and catalog SHOW keeps its existing read-only and scoping diagnostics.

## Diagnostics

The client reports through one internal event source named for its assembly,
`Assimalign.Cohesion.Database.Graph.Client` (`src/Internal/EventSource/GraphClientEventSource.cs`),
written by GraphConnection's three public members. Start and stop carry the `Queries` keyword
(`0x1`).

| Id | Event | Level | Keyword | Payload |
| --- | --- | --- | --- | --- |
| 1 | `QueryStart` | Verbose | `Queries` | `database`, `operation` (`Query`, `Execute` or `QueryPaths`) |
| 2 | `QueryStop` | Verbose | `Queries` | `database`, `operation`, `status` (`Success`, `Error` or `Cancelled`), `rowCount` (rows; for `QueryPaths`, the paths read by then; -1 for a scalar query that did not return), `durationMilliseconds` |
| 3 | `QueryFailed` | Error | — | `database`, `operation`, `code` (the wire code; empty for an uncoded failure), `exceptionType`, `durationMilliseconds` |

QueryAsync and ExecuteAsync share one private core that names the operation, so an Execute is
reported as such and not as the Query it runs. **Every start has a stop** (the area's convention,
`docs/resources/Database/DESIGN.md`, "Diagnostics"), written from a `finally`: a failure writes
event 3 and then `QueryStop` with `Error`, a cancellation (how a timeout surfaces) `QueryStop` with
`Cancelled` and no failure. The failure is captured by an exception filter that declines it, so it
reaches the caller unchanged. A path query ends when its enumeration does: `Success` once it
reached the server's terminal count; `Error`, after event 3, for a failed open or read;
`Cancelled` for a cancellation or for a caller that stopped reading and disposed the enumerator
early, with the paths read by then. An iterator cannot catch around a `yield`, so the open and each
read capture their own failure, and the iterator's `finally` writes the stop. Its start and stop are
written by different steps of the enumerator, each on its consumer's execution context, where the
start's activity is not current; so while the start was written, the enumerator captures the
start's execution context and writes the end in it (`ExecutionContext.Run`, allocating only then),
and an activity-tracking tool sees `QueryStop` close the activity `QueryStart` opened. As
`System.Net.Http`'s `RequestStop`, a stop is written only for a start that was written, so a
listener that attaches mid-query sees no stop without its start; event 3 is written either way.

The statement text and the parameter values are never written, nor is the server's message: a GQL
parse error's quotes a fragment of the statement, so event 3 writes the wire code and the
exception's type only (the area's failure rule). Timestamps are taken only while a listener takes
the source. No counters.

`GraphClientEventSourceTests` checks the name, the strict manifest, one event per query of each
member, a refused statement included (its failure, then its `Error` stop, with no statement text and
no server message), a path query its caller stopped reading (a `Cancelled` stop with the paths read),
a cancelled query (a `Cancelled` stop and no failure), a path query the server refused
(`ParseFailure`, then an `Error` stop with no paths), a query refused by the client itself because a
path query holds the exchange (an empty code and `InvalidOperationException`), a failure whose start
was not written (no stop), and, under `TplEventSource` activity tracking, a path query's stop
carrying its start's activity id whether its caller read every path or stopped early.

## Concrete types (concrete-types plan, phase 5, #1261)

The package has no public interface left and no `Abstractions/` folder
([plan](../../../../docs/programs/DATABASE_CONCRETE_TYPES_PLAN.md) `7, "P5, as landed").

| Type | Shape | Was |
|---|---|---|
| `GraphClient` | sealed; `Create(GraphClientOptions)` over a private constructor | the static `GraphClient` factory, `IGraphClient` and the internal `DefaultGraphClient` |
| `GraphConnection` | sealed; internal constructor; moved out of `Internal/` | `IGraphConnection` and the internal `GraphConnection` |

The two exchanges stay internal sealed leaves of the shared bases: `GraphExecuteExchange` of
`DatabaseProtocolExchange<GraphResultSet>` and `GraphPathsExchange` of
`DatabaseStreamingExchange`. `GraphResultSet.Columns` and its rows are never null (`database-area.md`,
rule 9): a statement without a result header yields empty ones. Studio's graph workspace was
retyped to the sealed types.

## Duplex transport composition

`ConnectionFactory` accepts `IDuplexConnectionFactory`. The shared client reads and writes
framed bytes through the returned `IDuplexConnection`; the common `IConnection` root carries
identity, features and lifecycle only. TCP, InMemory and TLS-layered duplex factories satisfy
this contract. The pool owns each established connection and disposes it when discarded or
when the pool closes; transport disposal has signaled `ConnectionClosed` before it completes.

### Breaking changes from 10.0.0-preview.1 (#1459)

`GraphClientOptions.ConnectionFactory` changes from `IConnectionFactory?` to
`IDuplexConnectionFactory?`. Changing the property's getter and setter signatures breaks
preview.1 binaries and source that supplies a factory typed only as `IConnectionFactory`.
This preview break is accepted under owner decisions 15 and 58. Rebuild consumers and use
`IDuplexConnectionFactory` for factory variables and custom implementations. Custom factories
can derive from `DuplexConnectionFactory`, implement `ConnectCoreAsync` returning
`ValueTask<IDuplexConnection>`, and produce a `DuplexConnection` implementation. Built-in TCP,
InMemory and TLS-layered duplex factories already satisfy the new contract. Database wire
framing, handshake and model messages are unchanged.

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
Information, including SQL0100, is never sent. Error and result payloads are unchanged.
Statement failures leave the server session ready. Model clients validate diagnostic
ordering and preserve every code, severity, message, nullable offset and source line.

GraphClientException copies the underlying database diagnostics carrier; GraphResultSet.Diagnostics exposes negotiated ProtocolDiagnostic values. The existing stricter streaming failure/discard policy remains in force.
