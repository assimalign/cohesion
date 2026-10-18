# Documents client

`Assimalign.Cohesion.Database.Documents.Client` is the concrete, NativeAOT-compatible client for
`DocumentDatabaseServer`. It executes the OQL SQL++ profile, returns projection rows and whole JSON documents,
reads catalogs, and manages collections and versioned documents. It ships as a NuGet-only
package, outside the `App.Database` shared framework.

The application chooses a transport factory and endpoint. `DocumentClient` owns a shared
`Database.Client` pool; disposing a `DocumentConnection` returns its healthy session to that
pool. One database is selected during authentication and remains bound for the connection's
lifetime. Operations are serialized; explicit wire transactions are outside this API.

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

OQL INSERT, UPSERT, UPDATE and DELETE return their affected counts through ExecuteAsync. UPDATE accepts
SET and UNSET paths and reports the matched count, with a new version for every match. DELETE
retains the collection and index definitions; EXPLAIN UPDATE/DELETE includes the chosen scan or
index seek. UPSERT inserts or wholly replaces using INSERT's KEY/VALUE grammar and reports
written-document count; EXPLAIN UPSERT describes its VALUES or SELECT source. A path-rule failure preserves its diagnostic and leaves the connection reusable.

`DocumentClientException.Code` describes the wire failure; its inherited `DatabaseException.Diagnostics`
retains the server's codes and source locations, and its message preserves the composed `CODE: message`
text. A rejected statement leaves the session usable. A malformed or
unfinished exchange invalidates it. Cancellation of a write does not establish whether it committed.

[DESIGN.md](DESIGN.md) explains the exchange and ownership contracts; the
[API reference](Assembly/Assimalign.Cohesion.Database.Documents.Client/OVERVIEW.md) lists the
concrete public surface. The [consumer sample](../samples/Assimalign.Cohesion.Database.Documents.AotSample/Program.cs)
executes its checks against real in-memory and TCP servers after NativeAOT publishing.
