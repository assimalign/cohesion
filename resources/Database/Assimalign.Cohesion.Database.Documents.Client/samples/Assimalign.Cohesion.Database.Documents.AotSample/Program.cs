using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Documents.Client;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Documents.AotSample;

/// <summary>A real server/client consumer, executed after NativeAOT publishing on both transports.</summary>
internal static class Program
{
    private static int _passed;

    private static async Task<int> Main()
    {
        try
        {
            await RunAsync(false);
            await RunAsync(true);
            Console.WriteLine($"SUMMARY: {_passed} passed, 0 failed, 0 skipped");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            Console.WriteLine($"SUMMARY: {_passed} passed, 1 failed, 0 skipped");
            return 1;
        }
    }

    private static async Task RunAsync(bool tcp)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        IDuplexConnectionListener listener;
        IDuplexConnectionFactory factory;
        if (tcp)
        {
            listener = new TcpConnectionListener(new() { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) });
            factory = new TcpConnectionFactory();
        }
        else
        {
            var memory = new InMemoryConnectionListener();
            listener = memory;
            factory = memory.CreateFactory();
        }
        await using var engine = await DocumentDatabaseEngine.CreateBuilder("native-documents")
            .AddDatabase("documents").AddServer(options => options.Listener = listener).BuildAsync(token);
        var server = (DocumentDatabaseServer)engine.Servers.Single();
        await server.StartAsync(token);
        await using var client = DocumentClient.Create(new()
        {
            ConnectionFactory = factory,
            Settings = new DatabaseConnectionSettings { Database = "documents", EndPoint = listener.EndPoint, MaxPoolSize = 1 },
        });
        await using (var connection = await client.ConnectAsync(token))
        {
            Check(await connection.ExecuteAsync("CREATE COLLECTION items", cancellationToken: token) == 0, "create collection");
            Check(await connection.ExecuteAsync("CREATE COLLECTION IF NOT EXISTS items", cancellationToken: token) == 0, "existing collection");
            Check(await connection.GetCollectionAsync("items", token) == "items", "get collection");
            var saved = await connection.PutAsync("items", "one", "{\"name\":\"東京\",\"score\":3,\"nested\":[1,null]}"u8.ToArray(), cancellationToken: token);
            Check(saved.Version.Value > 0, "put version");
            Check((await connection.GetAsync("items", "one", token))?.Version == saved.Version, "get version");
            var rows = await connection.QueryAsync("SELECT name, score + @offset AS n FROM items WHERE score >= 2 ORDER BY n DESC",
                new Dictionary<string, object?> { ["offset"] = 4 }, token);
            Check(rows.Count == 1 && (decimal)rows[0]["n"]! == 7m && rows.AffectedCount == -1, "rows and parameters");
            var presence = await connection.QueryAsync("SELECT absent AS absent_value, nested[1] AS null_value FROM items WHERE absent IS MISSING", cancellationToken: token);
            Check(presence.Count == 1 && presence[0].SupportsMissing && presence[0].IsMissing(0) &&
                !presence[0].IsNull(0) && presence[0].GetValue(0) is null, "MISSING projection");
            Check(!presence[0].IsMissing(1) && presence[0].IsNull(1), "explicit NULL projection");
            var predicates = await connection.QueryAsync("SELECT score IN (1, 3, NULL) AS i, score BETWEEN 2 AND 3 AS b, name LIKE '__' ESCAPE '' AS l, CASE WHEN score > 2 THEN 'big' ELSE 'small' END AS c FROM items", cancellationToken: token);
            Check(predicates.Count == 1 && predicates[0][0] is true && predicates[0][1] is true &&
                predicates[0][2] is true && predicates[0][3] is "big", "IN BETWEEN LIKE CASE");
            var conditionalNull = await connection.QueryAsync("SELECT CASE absent WHEN 1 THEN 2 END AS c, 0 BETWEEN 1 AND MISSING AS b FROM items", cancellationToken: token);
            Check(conditionalNull[0].IsNull(0) && conditionalNull[0].IsMissing(1), "CASE NULL and BETWEEN MISSING");
            var shaped = await connection.QueryAsync("SELECT DISTINCT VALUE {n: name, a: [score, MISSING, NULL]} FROM items", cancellationToken: token);
            Check(shaped.Count == 1 && shaped.Documents[0].GetProperty("a")[0].GetInt32() == 3 &&
                shaped.Documents[0].GetProperty("a")[1].ValueKind == System.Text.Json.JsonValueKind.Null, "DISTINCT VALUE constructors");
            var scalar = await connection.QueryAsync("SELECT RAW score FROM items", cancellationToken: token);
            Check(scalar.Count == 1 && scalar.Columns[0].Name == "value" && scalar.Documents[0].GetInt32() == 3, "RAW scalar");
            var whole = await connection.QueryAsync("SELECT * FROM items", cancellationToken: token);
            Check(whole.IsDocumentQuery && whole.Documents[0].GetProperty("nested")[0].GetInt32() == 1, "whole documents");
            Check((await connection.QueryAsync("SELECT SUM(score) AS total FROM items HAVING SUM(score) > 0", cancellationToken: token))[0][0] is 3m, "aggregate");
            Check(await connection.ExecuteAsync("CREATE INDEX by_score ON items (score)", cancellationToken: token) == 0, "index create");
            var explained = await connection.QueryAsync("EXPLAIN SELECT score FROM items WHERE score >= 2", cancellationToken: token);
            Check(explained.IsDocumentQuery && explained.Documents.Any(document =>
                document.GetProperty("operator").GetString() == "IndexSeek" && document.GetProperty("index").GetString() == "by_score" &&
                document.GetProperty("estimated_rows").ValueKind == System.Text.Json.JsonValueKind.Null &&
                document.GetProperty("actual_rows").ValueKind == System.Text.Json.JsonValueKind.Null), "EXPLAIN documents");
            Check((await connection.QueryAsync("SELECT * FROM COHESION_SCHEMA.INDEXES", cancellationToken: token)).Count == 1, "catalog");
            Check(await connection.ExecuteAsync("DROP INDEX by_score ON items", cancellationToken: token) == 0, "index drop");
            try
            {
                await connection.QueryAsync("SELECT * FROM items LIMIT 1", cancellationToken: token);
                throw new InvalidOperationException("Unsupported OQL succeeded.");
            }
            catch (DocumentClientException exception)
            {
                Check(exception.Code == ProtocolErrorCode.ParseFailure && exception.Diagnostics.Any(d => d.Code == "COHDBL001"), "typed diagnostics");
            }
            Check((await connection.GetCollectionsAsync(token)).SequenceEqual(["items"]), "reuse after failure");
            var updated = await connection.PutAsync("items", "one", "null"u8.ToArray(), saved.Version, token);
            Check(updated.Version.Value > saved.Version.Value, "versioned update");
            Check(await connection.DeleteAsync("items", "one", updated.Version, token), "versioned delete");
            Check(await connection.GetAsync("items", "one", token) is null, "missing document");
            Check(await connection.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('e', {'score': MISSING, 'amount': NULL}), ('f', 1)", cancellationToken: token) == 2, "INSERT affected count");
            var metadata = await connection.QueryAsync("SELECT META().id AS id, META().version AS v FROM items ORDER BY id", cancellationToken: token);
            Check(metadata.Count == 2 && (string)metadata[0]["id"]! == "e" && (decimal)metadata[1]["v"]! > (decimal)metadata[0]["v"]!, "META identity and version");
            var insertedDocument = await connection.GetAsync("items", "e", token);
            Check(insertedDocument.HasValue, "INSERT readback");
            using var inserted = System.Text.Json.JsonDocument.Parse(insertedDocument.GetValueOrDefault().Content);
            Check(!inserted.RootElement.TryGetProperty("score", out _) && inserted.RootElement.GetProperty("amount").ValueKind == System.Text.Json.JsonValueKind.Null, "INSERT MISSING member");
            Check(await connection.ExecuteAsync("CREATE COLLECTION archive", cancellationToken: token) == 0, "INSERT SELECT collection");
            Check(await connection.ExecuteAsync("INSERT INTO archive (KEY k, VALUE v) SELECT META(d).id AS k, d AS v FROM items d", cancellationToken: token) == 2, "INSERT SELECT affected count");
            try
            {
                await connection.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('e', 0)", cancellationToken: token);
                throw new InvalidOperationException("Duplicate INSERT succeeded.");
            }
            catch (DocumentClientException exception)
            {
                Check(exception.Code == ProtocolErrorCode.ExecutionFailure && exception.Diagnostics.Any(d => d.Code == "COHDOC012"), "INSERT duplicate diagnostic");
            }
            Check(await connection.ExecuteAsync("DROP COLLECTION archive", cancellationToken: token) == 0, "reuse after duplicate");
            Check(await connection.ExecuteAsync("DROP COLLECTION items", cancellationToken: token) == 0, "OQL drop collection");
            Check(await connection.ExecuteAsync("DROP COLLECTION IF EXISTS items", cancellationToken: token) == 0, "absent collection");
            Check((await connection.GetCollectionsAsync(token)).Count == 0, "drop collection");
        }
        var sessionId = server.Sessions.Single().Id;
        await using (var rented = await client.ConnectAsync(token))
        {
            Check((await rented.GetCollectionsAsync(token)).Count == 0 && server.Sessions.Single().Id == sessionId, "pooled connection");
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) { throw new InvalidOperationException(name); }
        _passed++;
        Console.WriteLine($"PASS {name}");
    }
}
