# Documents client

`Assimalign.Cohesion.Database.Documents.Client` is the concrete, NativeAOT-compatible client for
`DocumentDatabaseServer`. It executes the OQL SQL++ profile, returns projection rows and whole JSON documents,
reads catalogs, and manages collections and versioned documents. It ships as a NuGet-only
package, outside the `App.Database` shared framework.

The application chooses a transport factory and endpoint. `DocumentClient` owns a shared
`Database.Client` pool; disposing a `DocumentConnection` returns its healthy session to that
pool. One database is selected during authentication and remains bound for the connection's
lifetime. Operations are serialized. OQL BEGIN/START, COMMIT and ROLLBACK control this session;
returning it rolls back open or failed work and requires negotiated Idle status before reuse.
The server refuses text controls without negotiated transaction-status with CD07002.

```csharp
await using var client = DocumentClient.Create(new()
{
    ConnectionFactory = new TcpConnectionFactory(),
    Settings = new DatabaseConnectionSettings
    {
        EndPoint = new IPEndPoint(IPAddress.Loopback, 5450),
        Database = "shop",
    },
});
await using var connection = await client.ConnectAsync();
await connection.CreateCollectionAsync("orders");
var saved = await connection.PutAsync("orders", "one", "{\"total\":42}"u8.ToArray());
var rows = await connection.QueryAsync("SELECT total FROM orders WHERE total >= @min",
    new Dictionary<string, object?> { ["min"] = 40 });
var documents = await connection.QueryAsync("SELECT * FROM orders");
await connection.DeleteAsync("orders", "one", saved.Version);
```

Examples use the Documents.Client, Database.Client, Connections.Tcp, System.Net and collection
namespaces. The client library itself references only the transport contracts. Query parameters
accept JSON scalars and `JsonElement`; object and array results remain owned `JsonElement`
values. A stored `Document` carries its exact UTF-8 content, identity and version.

OQL LIMIT/OFFSET accepts literals or JSON parameters in either order. Empty pages retain column
metadata, and Complete counts returned frames while query AffectedCount remains -1. Invalid
counts retain CD00007 and the operand location over both transports.
UNNEST and quantified predicates use the same query exchange. A star with UNNEST returns
synthetic whole-document objects; a MISSING LEFT binding is omitted while actual null elements
remain null. Grouped counts over expanded rows retain Int64 values and metadata.

OQL INSERT, UPSERT, UPDATE and DELETE return their affected counts through ExecuteAsync. UPDATE accepts
SET and UNSET paths and reports the matched count, with a new version for every match. DELETE
retains the collection and index definitions; EXPLAIN UPDATE/DELETE includes the chosen scan or
index seek. UPSERT inserts or wholly replaces using INSERT's KEY/VALUE grammar and reports
written-document count; EXPLAIN UPSERT describes its VALUES or SELECT source. A path-rule failure preserves its diagnostic and leaves the connection reusable.

`DocumentClientException.Code` describes the wire failure; its locally declared `Diagnostics`
retains the server's codes and source locations, and its message preserves the composed `CODE: message`
text. A rejected statement leaves the session usable. A malformed or
unfinished exchange invalidates it. Cancellation of a write does not establish whether it committed.

[DESIGN.md](DESIGN.md) explains the exchange and ownership contracts; the
[API reference (cohesion-docs)](https://github.com/assimalign/cohesion-docs) lists the
concrete public surface. The [consumer sample](../samples/Assimalign.Cohesion.Database.Documents.AotSample/Program.cs)
executes its checks against real in-memory and TCP servers after NativeAOT publishing.

## Dedicated test actors (#1285)

Deadline-dependent peers and busy test drivers use the linked Storage test helper's LongRunning threads. Ordinary async driver continuations stay on their actor scheduler, without changing SynchronizationContext. Existing deadlines of 25 seconds or more are hang guards. Runner settings impose no parallel-thread cap. See the [scheduling audit](../../Assimalign.Cohesion.Database.Storage/docs/TEST-HYGIENE-AUDIT.md) for every original Task.Run site, the exact journal counters and the physical index-work guards.

## Document access estimates (#1527)

EXPLAIN remains a nonexecuting query returning one JSON document per plan row. IndexSeek now
includes an Int64 index-entry estimate and labels complete counts or lower bounds in Detail.
IndexIntersection has IndexSeek children and a minimum-input upper bound, before JSON reads;
actual_rows remains JSON null. The existing nullable count members and Document frames carry
these values over InMemory and TCP, without a protocol-version or client API change. The AOT
consumer checks intersection results, both input descriptions and nonexecuting behavior.

## Documents diagnostic conventions (#1470)

Exception and diagnostic templates use the project's build-generated `ErrorMessages` resources.
Parameterized templates format with the current culture; abort/cancellation templates used in
normal cleanup are cached. The [Database error-code registry](../../Assimalign.Cohesion.Database/docs/ERROR-CODES.md)
assigns this family its two-letter prefix and categorized five-digit identities.
Existing SQLSTATE meanings, numeric wire error kinds, frame layouts and disk formats are preserved.
Structured diagnostics keep their code outside the message. Compiler diagnostic identifiers and
standard .NET argument guards retain their own contracts.

## Debugger inspection (#1470)

Client debugger inspection shows captured metadata and owned-array counts without transport work or result enumeration.

Eligible types follow the [Database conventions](../../Assimalign.Cohesion.Database/docs/CONVENTIONS.md) debugger display/proxy rules. Views are private and read-only; inspection does not change the public API, execution paths, error contracts, storage formats or protocol bytes.

## Engine-registered functions

QueryAsync and ExecuteAsync can call scalar and aggregate functions registered on the server's
engine builder. The client sends ordinary OQL text and receives the existing typed rows and
structured errors. CD05017 identifies an external callback/result/accumulator failure; its code
appears once in the composed wire message. No client registration or new protocol is required.


The existing NativeAOT consumer sample registers typed scalar/aggregate functions and a derived
NULL/MISSING-aware scalar on its real server, then asserts their values and declared column types
through both InMemory and TCP. These six additional checks run in the client sample itself,
independently of the engine's crash-recovery fixture.
