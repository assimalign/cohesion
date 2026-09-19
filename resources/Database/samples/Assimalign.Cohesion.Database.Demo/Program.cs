// Temporary demo, not a shipped sample. Consumer-facing examples live in the
// cohesion-examples repository (see .claude/rules/workflow.md).
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Blob;
using Assimalign.Cohesion.Database.Documents;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph;
using Assimalign.Cohesion.Database.KeyValuePair;
using Assimalign.Cohesion.Database.Sql;

namespace Assimalign.Cohesion.Database.Demo;

internal static class Program
{
    private static async Task Main()
    {
        Console.WriteLine("Cohesion Phase 23 - temporary in-memory demo");
        Console.WriteLine("All five engines run in this process; no database files or listeners are created.");
        Console.WriteLine("Wire support in this checkout: SQL and Key-Value servers; Documents has none; Graph is catalog-only; Blob supports streaming.");
        Console.WriteLine("This demo uses Documents, Graph and Blob in-process only.");
        Console.WriteLine("No hosting or application-model wiring in this demo: each engine is created directly, as requested by the owner.");
        Console.WriteLine("Engine registration hooks already exist in this checkout; this demo does not exercise them.");

        await using var sql = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions());
        await using var keyValue = KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions());
        await using var documents = DocumentDatabaseEngine.Create(new DocumentDatabaseEngineOptions());
        await using var graph = GraphDatabaseEngine.Create(new GraphDatabaseEngineOptions());
        await using var blob = BlobDatabaseEngine.Create(new BlobDatabaseEngineOptions());

        await ShowSqlAsync(sql);
        await ShowKeyValueAsync(keyValue);
        await ShowDocumentsAsync(documents);
        await ShowGraphAsync(graph);
        await ShowBlobAsync(blob);
    }

    private static async Task ShowKeyValueAsync(KeyValueDatabaseEngine engine, CancellationToken cancellationToken = default)
    {
        Console.WriteLine("\n== Key-Value: command API ==");
        // KeyValueCrudTests: the typed commands take a database session.
        var database = (IKeyValueDatabase)await engine.CreateDatabaseAsync("demo", cancellationToken);
        await using var session = await database.CreateSessionAsync(cancellationToken);
        byte[] key = Encoding.UTF8.GetBytes("user:1");
        var put = await database.PutAsync(session, key, Encoding.UTF8.GetBytes("Ada"), cancellationToken: cancellationToken);
        Console.WriteLine($"PUT user:1 = Ada -> applied: {put.Applied}");
        var entry = await database.GetAsync(session, key, cancellationToken);
        Console.WriteLine(entry is { } found
            ? $"GET {Encoding.UTF8.GetString(found.Key.Span)} -> {Encoding.UTF8.GetString(found.Value.Span)}"
            : "GET user:1 -> missing");
        Console.WriteLine($"EXISTS user:1 -> {await database.ExistsAsync(session, key, cancellationToken)}");
        Console.WriteLine($"EXISTS missing -> {await database.ExistsAsync(session, Encoding.UTF8.GetBytes("missing"), cancellationToken)}");
    }

    private static async Task ShowDocumentsAsync(DocumentDatabaseEngine engine, CancellationToken cancellationToken = default)
    {
        Console.WriteLine("\n== Documents: OQL (in-process) ==");
        // DocumentQueryTests: collection writes, then an OQL request through the session.
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("demo", cancellationToken);
        var collection = await database.CreateCollectionAsync("people", cancellationToken: cancellationToken);
        await using var session = await database.CreateSessionAsync(cancellationToken);
        var alice = await collection.PutAsync(session, "alice",
            Encoding.UTF8.GetBytes("""{"name":"Alice","profile":{"city":"Paris"}}"""), cancellationToken: cancellationToken);
        var bob = await collection.PutAsync(session, "bob",
            Encoding.UTF8.GetBytes("""{"name":"Bob","profile":{"city":"London"}}"""), cancellationToken: cancellationToken);
        Console.WriteLine($"PUT {alice.Id} -> {Encoding.UTF8.GetString(alice.Content.Span)}");
        Console.WriteLine($"PUT {bob.Id} -> {Encoding.UTF8.GetString(bob.Content.Span)}");
        const string oql = "SELECT d.name, d.profile.city AS city FROM people AS d WHERE d.profile.city = 'Paris'";
        Console.WriteLine($"> {oql}");
        await PrintResultAsync(await session.ExecuteAsync(DocumentQueryRequest.FromOql(oql), cancellationToken), cancellationToken);
    }

    private static async Task ShowGraphAsync(GraphDatabaseEngine engine, CancellationToken cancellationToken = default)
    {
        Console.WriteLine("\n== Graph: GQL (in-process) ==");
        // GraphQueryTests: literal relationship insertion and property projection.
        var database = await engine.CreateDatabaseAsync("demo", cancellationToken);
        await using var session = await database.CreateSessionAsync(cancellationToken);
        const string insert = "INSERT (a:Person {name: 'Alice'})-[r:KNOWS {weight: 2}]->(b:Person {name: 'Bob'})";
        Console.WriteLine($"> {insert}");
        await PrintResultAsync(await session.ExecuteAsync(insert, cancellationToken: cancellationToken), cancellationToken);
        const string gql = "MATCH (a:Person {name: 'Alice'})-[r:KNOWS]->(b) WHERE r.weight = 2 RETURN a.name, r.weight, b.name";
        Console.WriteLine($"> {gql}");
        await PrintResultAsync(await session.ExecuteAsync(gql, cancellationToken: cancellationToken), cancellationToken);
    }

    private static async Task ShowBlobAsync(BlobDatabaseEngine engine, CancellationToken cancellationToken = default)
    {
        Console.WriteLine("\n== Blob: container streams (in-process) ==");
        // BlobEngineTests: closing the write stream publishes the blob before reading.
        var database = (IBlobDatabase)await engine.CreateDatabaseAsync("demo", cancellationToken);
        var container = await database.CreateContainerAsync("files", cancellationToken);
        await using (var writer = await container.OpenWriteAsync("hello.txt", new() { ContentType = "text/plain" }, cancellationToken))
        {
            await writer.WriteAsync(Encoding.UTF8.GetBytes("Hello from Blob!"), cancellationToken);
        }
        var properties = await container.GetPropertiesAsync("hello.txt", cancellationToken);
        Console.WriteLine($"WRITE files/hello.txt -> length: {properties?.Length}; content-type: {properties?.ContentType}");
        await using var reader = await container.OpenReadAsync("hello.txt", cancellationToken: cancellationToken);
        using var contents = new MemoryStream();
        await reader.CopyToAsync(contents, cancellationToken);
        Console.WriteLine($"READ files/hello.txt -> {Encoding.UTF8.GetString(contents.ToArray())}");
    }

    private static async Task ShowSqlAsync(SqlDatabaseEngine engine, CancellationToken cancellationToken = default)
    {
        Console.WriteLine("\n== SQL ==");
        var database = await engine.CreateDatabaseAsync("demo", cancellationToken: cancellationToken);
        await using var session = await database.CreateSessionAsync(cancellationToken: cancellationToken);

        // Public session calls follow SqlJoinExecutionTests, SqlAggregateExecutionTests,
        // SqlSubqueryExecutionTests, SqlCollationIndexTests and SqlLanguageConformanceTests.
        await PrintSqlAsync(session, "CREATE TABLE customers (id INT PRIMARY KEY, name TEXT COLLATE case_insensitive UNIQUE);", cancellationToken);
        await PrintSqlAsync(session, "CREATE TABLE orders (id INT PRIMARY KEY, customer_id INT, amount INT);", cancellationToken);
        await PrintSqlAsync(session, "INSERT INTO customers VALUES (3, 'Grace'), (1, 'Alice'), (2, 'Bob');", cancellationToken);
        await PrintSqlAsync(session, "INSERT INTO orders VALUES (103, 2, 20), (101, 1, 40), (104, 3, 15), (102, 1, 10);", cancellationToken);

        await PrintSqlAsync(session,
            "SELECT c.name, o.amount FROM customers c INNER JOIN orders o ON c.id = o.customer_id ORDER BY o.id;", cancellationToken);
        await PrintSqlAsync(session,
            "SELECT customer_id, COUNT(*) AS orders_seen, SUM(amount) AS total, AVG(amount) AS mean, MIN(amount) AS smallest, MAX(amount) AS largest FROM orders GROUP BY customer_id HAVING COUNT(*) > 1;", cancellationToken);
        await PrintSqlAsync(session,
            "SELECT name FROM customers WHERE id IN (SELECT customer_id FROM orders WHERE amount >= 20) ORDER BY name;", cancellationToken);
        await PrintSqlAsync(session, "CREATE TABLE selected_customers (id INT, name TEXT);", cancellationToken);
        await PrintSqlAsync(session,
            "INSERT INTO selected_customers SELECT id, name FROM customers WHERE id IN (SELECT customer_id FROM orders WHERE amount >= 20);", cancellationToken);
        await PrintSqlAsync(session, "SELECT id, name FROM selected_customers ORDER BY id;", cancellationToken);

        await PrintSqlAsync(session, "SELECT id, name FROM customers WHERE name = 'aLiCe';", cancellationToken);
        const string duplicate = "INSERT INTO customers VALUES (4, 'ALICE');";
        Console.WriteLine($"> {duplicate}");
        try
        {
            await PrintResultAsync(await session.ExecuteAsync(SqlQueryRequest.FromSql(duplicate), cancellationToken), cancellationToken);
            Console.WriteLine("  Folded duplicate was accepted; UNIQUE rejection was not demonstrated.");
        }
        catch (SqlConstraintViolationException error)
        {
            Console.WriteLine($"  Folded duplicate rejected: {error.Message}");
        }

        Console.WriteLine("Insertion order above: Grace, Alice, Bob. Current unordered scan:");
        await PrintSqlAsync(session, "SELECT id, name FROM customers;", cancellationToken);
        await PrintSqlAsync(session, "SELECT name AS label FROM customers ORDER BY label;", cancellationToken);
        await PrintSqlAsync(session, "SELECT id, name FROM customers ORDER BY 2 DESC;", cancellationToken);
        await PrintSqlAsync(session, "ALTER TABLE customers ADD COLUMN credits INT NOT NULL DEFAULT 7;", cancellationToken);
        await PrintSqlAsync(session, "SELECT id, name, credits FROM customers ORDER BY id;", cancellationToken);
    }

    private static async Task PrintSqlAsync(IDatabaseSession session, string sql, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"> {sql}");
        await PrintResultAsync(await session.ExecuteAsync(SqlQueryRequest.FromSql(sql), cancellationToken), cancellationToken);
    }

    private static async Task PrintResultAsync(QueryResult result, CancellationToken cancellationToken = default)
    {
        if (result.Diagnostics is { } diagnostics)
        {
            foreach (var diagnostic in diagnostics)
            {
                Console.WriteLine($"  {diagnostic.Code}: {diagnostic.Message}");
            }
        }

        if (result is QueryResultSet rows)
        {
            await using (rows)
            {
                Console.WriteLine($"  {string.Join(" | ", rows.Columns.Select(column => column.Name))}");
                int count = 0;
                await foreach (var row in rows.GetRowsAsync(cancellationToken))
                {
                    var values = new string[row.FieldCount];
                    for (int index = 0; index < row.FieldCount; index++)
                    {
                        values[index] = FormatValue(row.GetValue(index));
                    }
                    Console.WriteLine($"  {string.Join(" | ", values)}");
                    count++;
                }
                if (count == 0) { Console.WriteLine("  (no rows)"); }
                if (result.Status != QueryResultStatus.Success) { Console.WriteLine($"  Status: {result.Status}"); }
            }
        }
        else
        {
            Console.WriteLine($"  {result.Status}; affected: {result.AffectedCount}");
        }
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "NULL",
        byte[] bytes => Encoding.UTF8.GetString(bytes),
        IFormattable formatted => formatted.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
