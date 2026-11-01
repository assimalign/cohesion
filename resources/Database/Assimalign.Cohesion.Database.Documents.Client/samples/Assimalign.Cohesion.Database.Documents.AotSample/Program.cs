using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Documents.Client;
using Assimalign.Cohesion.Database.Documents.Catalog;
using Assimalign.Cohesion.Database.Documents.Storage;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Transactions;

using Assimalign.Cohesion.Database.Documents.Protocol;

namespace Assimalign.Cohesion.Database.Documents.AotSample;

/// <summary>A real server/client consumer, executed after NativeAOT publishing on both transports.</summary>
internal static class Program
{
    private static int _passed;

    private static async Task<int> Main()
    {
        try
        {
            await RunArrayCatalogAsync(CancellationToken.None);
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

    private static async Task RunArrayCatalogAsync(CancellationToken cancellationToken = default)
    {
        using var storage = DocumentStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "array-aot");
        await using var coordinator = new TransactionCoordinator(storage, storage.WriteAheadJournal, storage.Records);
        var catalog = DocumentCatalog.Open(storage, coordinator);
        var collection = new DocumentCollectionMetadata(Guid.NewGuid(), "items");
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot, cancellationToken);
        await catalog.SaveCollectionAsync(collection, writer, cancellationToken);
        var content = await storage.WriteContentAsync(coordinator, writer,
            "{\"items\":[{\"price\":20,\"stock\":5},{\"price\":20.0,\"stock\":8},{\"price\":1,\"stock\":0}]}"u8.ToArray(), cancellationToken);
        await catalog.SaveDocumentAsync(new DocumentCatalogEntry(collection.Id, "a", 1, content.Head, content.Length, content.Checksum), writer, cancellationToken);
        var when = DocumentIndexPredicate.And(
            DocumentIndexPredicate.Compare("stock", DocumentIndexComparison.GreaterThan, 0),
            DocumentIndexPredicate.Not(DocumentIndexPredicate.IsNull("price")));
        var definition = await catalog.CreateIndexAsync(collection.Id, "prices", [DocumentIndexKey.DistinctArray("items", "price", when)],
            false, false, writer, cancellationToken);
        await coordinator.CommitAsync(writer, cancellationToken);
        var reader = await coordinator.BeginAsync(IsolationLevel.Snapshot, cancellationToken);
        try
        {
            var matches = await catalog.SearchIndexAsync(collection.Id, "prices", 10m, false, null, false, reader.Snapshot, cancellationToken);
            Check(matches.Count == 1 && matches[0].Id == "a", "catalog DISTINCT array candidate");
            var reopened = DocumentCatalog.Open(storage, coordinator);
            Check(reopened.GetIndexes(collection.Id, reader.Snapshot).Single() == definition, "catalog v3 descriptor round trip");
            writer = await coordinator.BeginAsync(IsolationLevel.Snapshot, cancellationToken);
            await catalog.DeleteDocumentAsync(collection.Id, "a", writer, cancellationToken);
            await coordinator.RollbackAsync(writer, cancellationToken);
            async Task<bool> HasPostingAsync()
            {
                var afterEnd = await coordinator.BeginAsync(IsolationLevel.Snapshot, cancellationToken);
                try
                {
                    return (await catalog.SearchIndexAsync(collection.Id, "prices", 20m, true, 20m, true, afterEnd.Snapshot, cancellationToken)).Count == 1;
                }
                finally { await coordinator.RollbackAsync(afterEnd, CancellationToken.None); }
            }
            Check(await HasPostingAsync(),
                "catalog array delete rollback");
            writer = await coordinator.BeginAsync(IsolationLevel.Snapshot, cancellationToken);
            await catalog.DeleteDocumentAsync(collection.Id, "a", writer, cancellationToken);
            await coordinator.CommitAsync(writer, cancellationToken);
            Check(!await HasPostingAsync(), "catalog array committed-delete negative control");
        }
        finally { await coordinator.RollbackAsync(reader, CancellationToken.None); }
    }

    private static async Task RunKeyJoinsAsync(DocumentConnection connection, CancellationToken cancellationToken = default)
    {
        await connection.CreateCollectionAsync("join_items", cancellationToken);
        await connection.QueryAsync("INSERT INTO join_items (KEY, VALUE) VALUES ('a',{n:1}), ('b',{n:2})", cancellationToken: cancellationToken);
        var joined = await connection.QueryAsync("SELECT META(a).id AS a, META(b).id AS b FROM join_items AS a JOIN join_items AS b ON META(b).id = META(a).id ORDER BY a.n", cancellationToken: cancellationToken);
        Check(joined.Count == 2 && joined.All(row => Equals(row[0], row[1])), "key JOIN metadata");
        var missing = await connection.QueryAsync("SELECT b, META(b).id AS id FROM [1] AS a LEFT JOIN join_items AS b ON META(b).id = 'absent'", cancellationToken: cancellationToken);
        Check(missing.Single().IsMissing(0) && missing.Single().IsMissing(1), "LEFT JOIN MISSING binding and metadata");
        var nest = await connection.QueryAsync("SELECT RAW b FROM ['a','absent',NULL] AS k LEFT NEST join_items AS b ON META(b).id = k", cancellationToken: cancellationToken);
        Check(nest.Documents[0].GetArrayLength() == 1 && nest.Documents[1].GetArrayLength() == 0 && nest.Documents[2].ValueKind == JsonValueKind.Null, "LEFT NEST scalar and unknown keys");
        var star = await connection.QueryAsync("SELECT b.* FROM [1] AS a JOIN join_items AS b ON META(b).id = 'b'", cancellationToken: cancellationToken);
        Check(star.Documents.Single().GetProperty("n").GetInt32() == 2, "qualified joined star");
        var correlated = await connection.QueryAsync("SELECT RAW (SELECT RAW b.n FROM [1] AS s JOIN join_items AS b ON META(b).id = META(a).id) FROM join_items AS a ORDER BY a.n", cancellationToken: cancellationToken);
        Check(correlated.Documents.Select(row => row[0].GetInt32()).SequenceEqual(new[] { 1, 2 }), "correlated key join");
        var plan = await connection.QueryAsync("EXPLAIN SELECT * FROM [1] AS a NEST join_items AS b ON META(b).id = 'a'", cancellationToken: cancellationToken);
        Check(plan.Documents.Any(row => row.GetProperty("operator").GetString() == "KeyNest"), "key join EXPLAIN");
        Check(await connection.ExecuteAsync("DROP COLLECTION join_items", cancellationToken: cancellationToken) == 0, "key join collection cleanup");
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
        var builder = DocumentDatabaseEngine.CreateBuilder("native-documents")
            .AddDatabase("documents").AddServer(options => options.Listener = listener);
        builder.Functions.Add(DocumentScalarFunction.Create<string, string>("lower_tag", value => value.ToLowerInvariant()));
        builder.Functions.Add(DocumentAggregateFunction.Create<decimal, decimal, decimal>("rank_total",
            () => 0m, (state, value) => state + value, state => state));
        builder.Functions.Add(new HasValueFunction());
        using var developerJson = JsonDocument.Parse(new string('[', 256) + "0" + new string(']', 256), new JsonDocumentOptions { MaxDepth = 256 });
        builder.Functions.Add(DocumentScalarFunction.Create<decimal, DocumentValue>("deep_value", _ => DocumentValue.FromJson(developerJson.RootElement)));
        await using var engine = await builder.BuildAsync(token);
        var database = await engine.OpenDatabaseAsync("documents", token);
        await using (var session = await database.CreateSessionAsync(token))
        {
            await session.ExecuteAsync("BEGIN", cancellationToken: token);
            Check(session.CurrentTransaction?.IsolationLevel == Transactions.IsolationLevel.Snapshot, "text default Snapshot");
            await session.ExecuteAsync("ROLLBACK", cancellationToken: token);
            await session.ExecuteAsync("START WORK ISOLATION LEVEL READ COMMITTED", cancellationToken: token);
            Check(session.CurrentTransaction?.IsolationLevel == Transactions.IsolationLevel.ReadCommitted, "text ReadCommitted");
            await session.ExecuteAsync("COMMIT TRAN", cancellationToken: token);
            Check(session.CurrentTransaction is null, "text transaction ended");
        }
        var server = (DocumentDatabaseServer)engine.Servers.Single();
        await server.StartAsync(token);
        await using var client = DocumentClient.Create(new()
        {
            ConnectionFactory = factory,
            Settings = new DatabaseConnectionSettings { Database = "documents", EndPoint = listener.EndPoint, MaxPoolSize = 1 },
        });
        await using (var connection = await client.ConnectAsync(token))
        {
            await connection.CreateCollectionAsync("savepoint_native", token);
            foreach (string command in new[] { "BEGIN", "SAVEPOINT parent", "SAVEPOINT duplicate", "INSERT INTO savepoint_native (KEY,VALUE) VALUES ('one', {n:1})", "SAVEPOINT duplicate", "INSERT INTO savepoint_native (KEY,VALUE) VALUES ('two', {n:2})", "ROLLBACK TO duplicate", "RELEASE duplicate", "ROLLBACK TO duplicate", "INSERT INTO savepoint_native (KEY,VALUE) VALUES ('three', {n:3})", "RELEASE parent", "COMMIT" })
            { await connection.ExecuteAsync(command, cancellationToken: token); }
            var savedScope = await connection.QueryAsync("SELECT n FROM savepoint_native", cancellationToken: token);
            Check(savedScope.Count == 1, "named savepoint result count");
            Check(Convert.ToInt64(savedScope[0][0]) == 3L, "named savepoint duplicate/release/undo/commit");
            await connection.DropCollectionAsync("savepoint_native", token);
            Check(await connection.ExecuteAsync("BEGIN", cancellationToken: token) == 0, "wire begin");
            Check(await connection.ExecuteAsync("ROLLBACK", cancellationToken: token) == 0, "wire rollback");
            Check(await connection.ExecuteAsync("CREATE COLLECTION items", cancellationToken: token) == 0, "create collection");
            Check(await connection.ExecuteAsync("CREATE COLLECTION IF NOT EXISTS items", cancellationToken: token) == 0, "existing collection");
            Check(await connection.GetCollectionAsync("items", token) == "items", "get collection");
            var saved = await connection.PutAsync("items", "one", "{\"name\":\"東京\",\"score\":3,\"nested\":[1,null]}"u8.ToArray(), cancellationToken: token);
            Check(saved.Version.Value > 0, "put version");
            await RunKeyJoinsAsync(connection, token);
            var functions = (await connection.QueryAsync("SELECT LOWER('A') AS s, ROUND(-2.5) AS n, ROUND(4.9999999999999999999999999999,-1) AS edge, MILLIS_TO_STR(0) AS d, TYPE(score) AS t, ARRAY_LENGTH(nested) AS a, IFMISSING(absent,1) AS c, LENGTH('😀a') AS l FROM items", cancellationToken: token)).Single();
            Check((string)functions["s"]! == "a", "scalar string");
            Check((decimal)functions["n"]! == -3m, "scalar rounding");
            Check((decimal)functions["edge"]! == 0m, "scalar exact negative-scale rounding");
            Check((string)functions["d"]! == "1970-01-01T00:00:00.000Z", "scalar date");
            Check((string)functions["t"]! == "number", "scalar type");
            Check(functions["a"] is long arrayLength && arrayLength == 2, "scalar array Int64");
            Check((decimal)functions["c"]! == 1m, "scalar conditional");
            Check(functions["l"] is long length && length == 2, "scalar code points Int64");
            var subqueries = await connection.QueryAsync("SELECT (SELECT RAW name FROM items) AS names, EXISTS (SELECT RAW name FROM items) AS present FROM items", cancellationToken: token);
            Check(subqueries.Count > 0 && ((JsonElement)subqueries[0][0]!).GetArrayLength() == subqueries.Count && Equals(subqueries[0][1], true), "nested SELECT arrays and EXISTS");
            var subqueryIn = await connection.QueryAsync("SELECT name FROM items WHERE name IN (SELECT RAW name FROM items)", cancellationToken: token);
            Check(subqueryIn.Count == subqueries.Count, "IN SELECT RAW");
            var correlated = await connection.QueryAsync("SELECT d.name, (SELECT RAW d.name FROM [1] AS v) AS names FROM items d", cancellationToken: token);
            Check(correlated.All(row => ((JsonElement)row[1]!)[0].GetString() == (string?)row[0]), "correlated expression arrays");
            var correlatedIdentity = await connection.QueryAsync("SELECT (SELECT RAW META(d).id FROM [1] AS v) FROM items d", cancellationToken: token);
            Check(correlatedIdentity.All(row => ((JsonElement)row[0]!)[0].ValueKind == JsonValueKind.String), "correlated stored metadata");
            var correlatedFrom = await connection.QueryAsync("SELECT d.name, (SELECT RAW v FROM [d.name] AS v) AS names FROM items d", cancellationToken: token);
            Check(correlatedFrom.All(row => ((JsonElement)row[1]!)[0].GetString() == (string?)row[0]), "correlated FROM");
            await connection.CreateCollectionAsync("nested_writes", token);
            await connection.QueryAsync("INSERT INTO nested_writes (KEY k, VALUE v) SELECT 'a' AS k, {n:1, names:(SELECT RAW name FROM items)} AS v FROM [1] AS d", cancellationToken: token);
            var nestedUpdate = await connection.QueryAsync("UPDATE nested_writes d SET n = 2 WHERE EXISTS (SELECT RAW v FROM [d.n] AS v) RETURNING (SELECT RAW s.n FROM nested_writes s) AS before", cancellationToken: token);
            Check(((JsonElement)nestedUpdate.Single()[0]!).GetRawText() == "[1]", "DML statement-start nested reads");
            var nestedDelete = await connection.QueryAsync("DELETE FROM nested_writes d WHERE n IN (SELECT RAW s.n FROM nested_writes s) RETURNING n", cancellationToken: token);
            Check(Equals(nestedDelete.Single()[0], 2m), "DELETE nested predicate");
            Check(await connection.ExecuteAsync("DROP COLLECTION nested_writes", cancellationToken: token) == 0, "nested DML collection cleanup");
            var lazySubquery = await connection.QueryAsync("SELECT COALESCE(1, (SELECT RAW 1/0 FROM items)) AS n FROM items LIMIT 1", cancellationToken: token);
            Check(lazySubquery.Count == 1 && Equals(lazySubquery[0][0], 1m), "lazy nested SELECT");
            using (var deep = JsonDocument.Parse(new string('[', 255) + "0" + new string(']', 255), new JsonDocumentOptions { MaxDepth = 256 }))
            {
                var parameters = new Dictionary<string, object?> { ["deep"] = deep.RootElement };
                foreach (string query in new[] {
                    "SELECT (SELECT RAW @deep FROM items LIMIT 1) AS v FROM items LIMIT 1",
                    "SELECT *, 1 AS w FROM items d UNNEST (SELECT RAW @deep FROM items LIMIT 1) AS v LIMIT 1",
                    "SELECT *, 1 AS w FROM items d UNNEST [@deep] AS v LIMIT 1",
                    "SELECT [@deep, 1/0], (SELECT RAW name FROM items) AS v FROM items LIMIT 1" })
                {
                    try
                    {
                        await connection.QueryAsync(query, parameters, token);
                        throw new InvalidOperationException("Overdeep subquery projection succeeded.");
                    }
                    catch (DocumentClientException error)
                    { Check(error.Diagnostics.Count == 1 && error.Diagnostics[0].Code == "CD05015" && error.Diagnostics[0].Start is not null, "located subquery depth"); }
                }
                var reusable = await connection.QueryAsync("SELECT RAW (SELECT RAW @deep FROM items LIMIT 1) FROM items LIMIT 1", parameters, token);
                Check(reusable.Count == 1 && connection.IsOpen, "depth refusal preserves session and RAW depth boundary");
                foreach (string operand in new[] { "[@deep]", "(SELECT RAW @deep FROM items LIMIT 1)" })
                {
                    var star = await connection.QueryAsync("SELECT * FROM items d UNNEST " + operand + " AS v LIMIT 1", parameters, token);
                    Check(star.Count == 1 && star.Documents[0].GetProperty("v").ValueKind == JsonValueKind.Array, "lone star depth boundary");
                }
            }
            foreach (string predicate in new[] { "", " WHERE EXISTS (SELECT RAW 1 FROM items)" })
            {
                try
                {
                    await connection.QueryAsync("SELECT deep_value(1) AS v FROM items" + predicate, cancellationToken: token);
                    throw new InvalidOperationException("Overdeep developer JSON projection succeeded.");
                }
                catch (DocumentClientException error)
                { Check(error.Diagnostics.Count == 1 && error.Diagnostics[0].Code == "CD05015" && error.Diagnostics[0].Start == 7, "located developer JSON depth"); }
                var raw = await connection.QueryAsync("SELECT RAW deep_value(1) FROM items", cancellationToken: token);
                Check(raw.Count == 1 && connection.IsOpen, "developer JSON RAW boundary and session reuse");
            }
            var count = await connection.QueryAsync("SELECT COUNT(*) AS n FROM items", cancellationToken: token);
            Check(count.Columns[0].Type == Assimalign.Cohesion.Database.Types.DatabaseType.Int64 && count.Single()[0] is long number && number == 1, "COUNT Int64");
            var aggregates = (await connection.QueryAsync(
                "SELECT COUNT(DISTINCT score) AS c, SUM(DISTINCT score) AS s, AVG(ALL score) AS a, MIN(DISTINCT score) AS lo, MAX(ALL score) AS hi, ARRAY_AGG(DISTINCT nested) AS ar, BOOL_AND(score > 0) AS ba, BOOL_OR(score > 0) AS bo, EVERY(EVERY x IN nested SATISFIES x IS VALUED END) AS e, STDDEV(score) AS sd, VARIANCE(score) AS va FROM items HAVING COUNT(DISTINCT score) > 0",
                cancellationToken: token)).Single();
            Check(aggregates[0] is 1L && aggregates[1] is 3m && aggregates[2] is 3m && aggregates[3] is 3m && aggregates[4] is 3m, "aggregate quantifiers");
            Check(aggregates[5] is System.Text.Json.JsonElement aggregateArray && aggregateArray.GetArrayLength() == 1 && aggregateArray[0][1].ValueKind == System.Text.Json.JsonValueKind.Null, "ARRAY_AGG DISTINCT owned JSON");
            Check(aggregates[6] is true && aggregates[7] is true && aggregates[8] is false, "Boolean aggregates and quantified EVERY");
            Check(aggregates[9] is 0m && aggregates[10] is 0m, "sample singleton statistics");
            var groupBindings = await connection.QueryAsync("SELECT key, g, FIRST r.d.name FOR r IN g END AS first_name, ARRAY r.t FOR r IN g END AS elements FROM items d LEFT UNNEST d.nested t GROUP BY d.score AS key GROUP AS g HAVING ANY r IN g SATISFIES r.d.score = 3 END ORDER BY ARRAY_LENGTH(g)", cancellationToken: token);
            Check(groupBindings.Count == 1 && groupBindings[0][0] is 3m && groupBindings[0][2] is "東京", "GROUP AS aliases FIRST HAVING ORDER BY");
            var bindingArray = (System.Text.Json.JsonElement)groupBindings[0][1]!;
            Check(bindingArray.GetArrayLength() == 2 && bindingArray.EnumerateArray().All(value => value.GetProperty("key").GetDecimal() == 3m && value.GetProperty("d").GetProperty("name").GetString() == "東京"), "GROUP AS binding wrappers include aliases");
            Check(((System.Text.Json.JsonElement)groupBindings[0][3]!).GetArrayLength() == 2, "GROUP AS ARRAY comprehension");
            var emptyGroup = await connection.QueryAsync("SELECT g FROM items WHERE FALSE GROUP BY TRUE GROUP AS g", cancellationToken: token);
            Check(emptyGroup.Count == 0 && emptyGroup.Columns[0].Type == Assimalign.Cohesion.Database.Types.DatabaseType.Json, "GROUP AS empty metadata");
            var groupPlan = await connection.QueryAsync("EXPLAIN SELECT g FROM items GROUP BY 1/0 AS key GROUP AS g", cancellationToken: token);
            Check(groupPlan.Documents.Any(value => value.GetProperty("detail").ValueKind == System.Text.Json.JsonValueKind.String && value.GetProperty("detail").GetString() == "group_as=g; group_aliases=key"), "GROUP AS nonexecuting EXPLAIN");
            var tinyStatistic = await connection.QueryAsync("SELECT STDDEV(v) FROM items UNNEST [1e-20,2e-20] v", cancellationToken: token);
            Check(tinyStatistic[0][0] is 7.0710678e-21m, "sample statistic avoids intermediate underflow");
            var wideStatistic = await connection.QueryAsync("SELECT VARIANCE(v) AS v, STDDEV(v) AS s FROM items UNNEST [-2e14,2e14,0] v", cancellationToken: token);
            Check(wideStatistic[0][0] is 4e28m && wideStatistic[0][1] is 2e14m, "sample statistic avoids intermediate overflow");
            var registered = await connection.QueryAsync(
                "SELECT lower_tag('READY') AS tag, has_value(absent) AS missing_value, has_value(NULL) AS null_value, has_value(score) AS valued FROM items",
                cancellationToken: token);
            Check(registered.Count == 1 && registered.Columns[0].Type == Assimalign.Cohesion.Database.Types.DatabaseType.String &&
                registered[0][0] is "ready", "registered typed scalar");
            Check(registered.Columns.Skip(1).All(column => column.Type == Assimalign.Cohesion.Database.Types.DatabaseType.Boolean) &&
                registered[0][1] is false && registered[0][2] is false && registered[0][3] is true, "registered MISSING-aware scalar");
            var registeredTotal = await connection.QueryAsync("SELECT rank_total(score) AS total FROM items", cancellationToken: token);
            Check(registeredTotal.Count == 1 && registeredTotal.Columns[0].Type == Assimalign.Cohesion.Database.Types.DatabaseType.Decimal &&
                registeredTotal[0][0] is 3m, "registered typed aggregate");
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
            var unnested = await connection.QueryAsync("SELECT t, COUNT(*) AS n FROM items d UNNEST d.nested t GROUP BY t ORDER BY t", cancellationToken: token);
            Check(unnested.Count == 2 && unnested[0].IsNull(0) && unnested[1][0] is 1m && unnested[1][1] is 1L, "UNNEST grouped rows");
            var quantified = await connection.QueryAsync("SELECT ANY x IN nested SATISFIES x = 1 END AS a, EVERY x IN nested SATISFIES x = 1 END AS e, 1 IN nested AS i FROM items", cancellationToken: token);
            Check(quantified[0][0] is true && quantified[0][1] is false && quantified[0][2] is true, "ANY EVERY array IN");
            var comprehensions = await connection.QueryAsync("SELECT ARRAY x FOR x IN nested WHEN x IS VALUED END AS a, FIRST x FOR x IN [] END AS f, ARRAY lower_tag(x) FOR x IN ['READY'] END AS s FROM items", cancellationToken: token);
            Check(comprehensions[0][0] is System.Text.Json.JsonElement array && array.GetArrayLength() == 1 && array[0].GetInt32() == 1, "ARRAY comprehension");
            Check(comprehensions[0].IsMissing(1), "FIRST empty MISSING");
            Check(comprehensions[0][2] is System.Text.Json.JsonElement mapped && mapped[0].GetString() == "ready", "comprehension registered mapping");
            var comprehensionReturning = await connection.QueryAsync("INSERT INTO items (KEY, VALUE) VALUES ('comprehension', {nested:[1]}) RETURNING FIRST x FOR x IN nested END AS f", cancellationToken: token);
            Check(comprehensionReturning.AffectedCount == 1 && comprehensionReturning[0][0] is 1m, "FIRST RETURNING");
            Check(await connection.ExecuteAsync("DELETE FROM items WHERE META().id = 'comprehension'", cancellationToken: token) == 1, "comprehension cleanup");
            var synthetic = await connection.QueryAsync("SELECT * FROM items d UNNEST d.nested t", cancellationToken: token);
            Check(synthetic.IsDocumentQuery && synthetic.Documents.Count == 2 && synthetic.Documents[0].GetProperty("d").GetProperty("name").GetString() == "東京" &&
                synthetic.Documents[0].GetProperty("t").GetInt32() == 1, "UNNEST synthetic documents");
            Check((await connection.QueryAsync("SELECT SUM(score) AS total FROM items HAVING SUM(score) > 0", cancellationToken: token))[0][0] is 3m, "aggregate");
            Check(await connection.ExecuteAsync("CREATE COLLECTION ordering", cancellationToken: token) == 0, "ordering collection");
            await connection.ExecuteAsync("INSERT INTO ordering (KEY, VALUE) VALUES ('m', {}), ('n', {'v':NULL}), ('v', {'v':1})", cancellationToken: token);
            (string Suffix, string Expected)[] orderings =
            [
                ("ASC NULLS FIRST", "m,n,v"), ("ASC NULLS LAST", "v,m,n"),
                ("DESC NULLS FIRST", "n,m,v"), ("DESC NULLS LAST", "v,n,m"),
            ];
            foreach (var ordering in orderings)
            {
                var ordered = await connection.QueryAsync($"SELECT META().id AS id, v AS k FROM ordering ORDER BY k {ordering.Suffix}", cancellationToken: token);
                Check(string.Join(',', ordered.Select(row => (string)row.GetValue(0)!)) == ordering.Expected, "NULLS " + ordering.Suffix);
            }
            await connection.ExecuteAsync("CREATE INDEX ordering_ix ON ordering (v INCLUDE MISSING)", cancellationToken: token);
            var orderingPlan = await connection.QueryAsync("EXPLAIN SELECT META().id FROM ordering ORDER BY v DESC", cancellationToken: token);
            Check(orderingPlan.Documents.Any(document => document.GetProperty("operator").GetString() == "IndexOrderCandidate" &&
                document.GetProperty("detail").GetString()!.Contains("consumed=True; sort=elided", StringComparison.Ordinal)), "ordered index consumption");
            var orderedPage = await connection.QueryAsync("SELECT META().id FROM ordering ORDER BY v DESC LIMIT 1 OFFSET 1", cancellationToken: token);
            Check(orderedPage.Count == 1 && orderedPage.Single().GetValue(0) is "n", "ordered index early page");
            Check(await connection.ExecuteAsync("DROP COLLECTION ordering", cancellationToken: token) == 0, "ordering cleanup");
            var derivedSource = await connection.QueryAsync("SELECT d.score FROM (SELECT score FROM items) d ORDER BY d.score DESC LIMIT 1", cancellationToken: token);
            Check(derivedSource.Count == 1, "derived source ordered page");
            var expressionSource = await connection.QueryAsync("SELECT RAW v FROM [1,NULL,{n:2}] AS v", cancellationToken: token);
            Check(expressionSource.Count == 3 && expressionSource.Documents[0].GetInt32() == 1 &&
                expressionSource.Documents[1].ValueKind == JsonValueKind.Null && expressionSource.Documents[2].GetProperty("n").GetInt32() == 2, "expression source values");
            Check((await connection.QueryAsync("SELECT RAW v FROM MISSING AS v", cancellationToken: token)).Count == 0, "missing source empty");
            using var sourceParameter = JsonDocument.Parse("[1,2]");
            var parameterSource = await connection.QueryAsync("SELECT RAW v FROM @p AS v",
                new Dictionary<string, object?> { ["p"] = sourceParameter.RootElement }, token);
            Check(parameterSource.Documents.Select(document => document.GetInt32()).SequenceEqual([1, 2]), "parameter array source");
            Check((await connection.QueryAsync("SELECT * FROM [1] AS v", cancellationToken: token)).Documents.Single().GetRawText() == "{\"v\":1}", "expression source star");
            Check((await connection.QueryAsync("SELECT META(v).id AS id FROM [1] AS v", cancellationToken: token)).Single()[0] is null, "expression source metadata");
            var derivedPlan = await connection.QueryAsync("EXPLAIN SELECT d.score FROM (SELECT score FROM items) d", cancellationToken: token);
            Check(derivedPlan.Documents.Any(document => document.GetProperty("operator").GetString() == "DerivedSource"), "derived source explain");
            Check(await connection.ExecuteAsync("CREATE INDEX by_score ON items (score)", cancellationToken: token) == 0, "index create");
            var explained = await connection.QueryAsync("EXPLAIN SELECT score FROM items WHERE score >= 2", cancellationToken: token);
            Check(explained.IsDocumentQuery && explained.Documents.Any(document =>
                document.GetProperty("operator").GetString() == "IndexSeek" && document.GetProperty("index").GetString() == "by_score" &&
                document.GetProperty("estimated_rows").GetInt64() == 1 &&
                document.GetProperty("actual_rows").ValueKind == System.Text.Json.JsonValueKind.Null), "EXPLAIN documents");
            Check((await connection.QueryAsync("SELECT * FROM COHESION_SCHEMA.INDEXES", cancellationToken: token)).Count == 1, "catalog");
            Check(await connection.ExecuteAsync("DROP INDEX by_score ON items", cancellationToken: token) == 0, "index drop");
            Check(await connection.ExecuteAsync("CREATE INDEX by_nested ON items (DISTINCT ARRAY t FOR t IN nested WHEN t IS NOT NULL END)", cancellationToken: token) == 0, "array index DDL");
            var arrayPlan = await connection.QueryAsync("EXPLAIN SELECT score FROM items WHERE ANY x IN nested SATISFIES x = 1 AND x IS NOT NULL END", cancellationToken: token);
            Check(arrayPlan.Documents.Any(document => document.GetProperty("operator").GetString() == "IndexSeek" &&
                document.GetProperty("detail").GetString()!.Contains("array_residual=True", StringComparison.Ordinal)), "array seek EXPLAIN");
            Check((await connection.QueryAsync("SELECT score FROM items WHERE ANY x IN nested SATISFIES x = 1 AND x IS NOT NULL END", cancellationToken: token)).Count == 1, "array ANY residual");
            Check((await connection.QueryAsync("SELECT t FROM items d UNNEST d.nested t WHERE t = 1 AND t IS NOT NULL", cancellationToken: token)).Count == 1, "array UNNEST seek");
            Check(await connection.ExecuteAsync("DROP INDEX by_nested ON items", cancellationToken: token) == 0, "array index drop");
            Check(await connection.ExecuteAsync("CREATE UNIQUE INDEX by_name_score ON items (name,score)", cancellationToken: token) == 0, "composite unique create");
            Check(await connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS by_name_score ON items (score)", cancellationToken: token) == 0, "index if not exists");
            Check((await connection.QueryAsync("SELECT score FROM items WHERE name = '東京' AND score >= 3", cancellationToken: token)).Count == 1, "composite seek");
            var indexMetadata = (await connection.QueryAsync("SELECT PATHS[1] AS p, IS_UNIQUE AS u FROM COHESION_SCHEMA.INDEXES", cancellationToken: token)).Single();
            Check(indexMetadata["p"] is "score" && indexMetadata["u"] is true, "composite metadata");
            try
            {
                await connection.PutAsync("items", "duplicate", "{\"name\":\"東京\",\"score\":3}"u8.ToArray(), cancellationToken: token);
                throw new InvalidOperationException("Unique index accepted a duplicate.");
            }
            catch (DocumentClientException exception)
            { Check(exception.Code == ProtocolErrorCode.ExecutionFailure && exception.Diagnostics.Any(d => d.Code == "CD03020"), "unique execution failure"); }
            Check(await connection.ExecuteAsync("DROP INDEX IF EXISTS by_name_score ON items", cancellationToken: token) == 0, "conditional index drop");
            Check(await connection.ExecuteAsync("DROP INDEX IF EXISTS absent ON items", cancellationToken: token) == 0, "absent index drop");
            try
            {
                await connection.QueryAsync("MERGE INTO items", cancellationToken: token);
                throw new InvalidOperationException("Unsupported OQL succeeded.");
            }
            catch (DocumentClientException exception)
            {
                Check(exception.Code == ProtocolErrorCode.ParseFailure && exception.Diagnostics.Any(d => d.Code == "CK00001"), "typed diagnostics");
            }
            var paged = await connection.QueryAsync("SELECT name AS n FROM items ORDER BY n LIMIT @n OFFSET 0",
                new Dictionary<string, object?> { ["n"] = 1 }, token);
            Check(paged.Count == 1 && paged.AffectedCount == -1, "parameter paging");
            var zero = await connection.QueryAsync("SELECT COUNT(*) FROM items LIMIT 0", cancellationToken: token);
            Check(zero.Count == 0 && zero.Columns[0].Type == Assimalign.Cohesion.Database.Types.DatabaseType.Int64, "empty page metadata");
            Check((await connection.QueryAsync("SELECT * FROM items OFFSET 1", cancellationToken: token)).Count == 0, "offset only");
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
                Check(exception.Code == ProtocolErrorCode.ExecutionFailure && exception.Diagnostics.Any(d => d.Code == "CD03012"), "INSERT duplicate diagnostic");
            }
            Check(await connection.ExecuteAsync("DELETE FROM archive WHERE META().id = 'e'", cancellationToken: token) == 1, "DELETE META affected count");
            Check(await connection.GetAsync("archive", "e", token) is null, "DELETE readback");
            Check(await connection.ExecuteAsync("CREATE INDEX archive_amount ON archive (amount)", cancellationToken: token) == 0, "DELETE index setup");
            Check(await connection.ExecuteAsync("UPDATE items SET amount = {'nested':[1,MISSING]}, extra.deep = 2 WHERE META().id = 'e'", cancellationToken: token) == 1, "UPDATE affected count");
            var patch = await connection.GetAsync("items", "e", token);
            using var patchJson = System.Text.Json.JsonDocument.Parse(patch.GetValueOrDefault().Content);
            Check(patchJson.RootElement.GetProperty("amount").GetProperty("nested")[1].ValueKind == System.Text.Json.JsonValueKind.Null, "UPDATE array MISSING");
            Check(await connection.ExecuteAsync("UPDATE items UNSET extra, amount WHERE META().id = 'e'", cancellationToken: token) == 1, "UNSET affected count");
            try
            {
                await connection.ExecuteAsync("UPDATE items SET extra[5] = 1 WHERE META().id = 'e'", cancellationToken: token);
                throw new InvalidOperationException("Invalid UPDATE succeeded.");
            }
            catch (DocumentClientException exception)
            {
                Check(exception.Code == ProtocolErrorCode.ExecutionFailure && exception.Diagnostics.Any(d => d.Code == "CD02016"), "UPDATE path diagnostic");
            }
            var deletePlan = await connection.QueryAsync("EXPLAIN DELETE FROM archive WHERE amount >= 1", cancellationToken: token);
            Check(deletePlan.Documents.Any(document => document.GetProperty("operator").GetString() == "Delete" &&
                document.GetProperty("detail").GetString() == "filter=True; access=IndexSeek"), "DELETE EXPLAIN seek");
            Check(await connection.ExecuteAsync("DELETE FROM archive", cancellationToken: token) == 1, "DELETE all affected count");
            Check((await connection.QueryAsync("SELECT * FROM COHESION_SCHEMA.INDEXES WHERE COLLECTION_NAME = 'archive'", cancellationToken: token)).Count == 1, "DELETE retains index");
            Check(await connection.ExecuteAsync("DROP COLLECTION archive", cancellationToken: token) == 0, "reuse after duplicate");
            var upsert = await connection.QueryAsync("UPSERT INTO items (KEY, VALUE) VALUES ('e', {'name':'é東京<😀'}), ('new', NULL)", cancellationToken: token);
            Check(upsert.AffectedCount == 2 && upsert.Count == 0 && upsert.Documents.Count == 0 && upsert.Columns.Count == 0, "UPSERT completion");
            var replaced = await connection.GetAsync("items", "e", token);
            Check(System.Text.Encoding.UTF8.GetString(replaced.GetValueOrDefault().Content.Span) == "{\"name\":\"é東京<😀\"}", "UPSERT whole UTF-8 replacement");
            var upsertPlan = await connection.QueryAsync("EXPLAIN UPSERT INTO items (KEY k) SELECT META().id AS k FROM items", cancellationToken: token);
            Check(upsertPlan.Documents.Any(document => document.GetProperty("operator").GetString() == "UpsertSelect"), "UPSERT SELECT EXPLAIN");
            try
            {
                await connection.ExecuteAsync("UPSERT INTO items (KEY, VALUE) VALUES (MISSING, 1)", cancellationToken: token);
                throw new InvalidOperationException("Invalid UPSERT succeeded.");
            }
            catch (DocumentClientException exception)
            {
                Check(exception.Code == ProtocolErrorCode.ExecutionFailure && exception.Diagnostics.Any(d => d.Code == "CD02008"), "UPSERT invalid key");
            }
            Check(await connection.ExecuteAsync("UPSERT INTO items (KEY k, VALUE v) SELECT META().id AS k, 1 AS v FROM items", cancellationToken: token) == 3, "UPSERT SELECT session reuse");
            var insertedRows = await connection.QueryAsync("INSERT INTO items AS d (KEY, VALUE) VALUES ('returned', {n:1}) RETURNING META(d).id AS id, META(d).version AS version, d.n AS n", cancellationToken: token);
            var insertedImage = await connection.GetAsync("items", "returned", token);
            Check(insertedRows.AffectedCount == 1 && insertedRows.Count == 1 && insertedRows[0]["id"] is "returned" &&
                insertedRows[0]["version"] is decimal version && version == insertedImage.GetValueOrDefault().Version.Value, "INSERT RETURNING stored metadata");
            var upsertRows = await connection.QueryAsync("UPSERT INTO items (KEY, VALUE) VALUES ('returned', {n:2}) RETURNING RAW items.n", cancellationToken: token);
            Check(upsertRows.AffectedCount == 1 && upsertRows.Documents[0].GetInt32() == 2, "UPSERT RETURNING RAW");
            var updateRows = await connection.QueryAsync("UPDATE items SET n = 3 WHERE META().id = 'returned' RETURNING VALUE n", cancellationToken: token);
            Check(updateRows.AffectedCount == 1 && updateRows.Documents[0].GetInt32() == 3, "UPDATE RETURNING post-image");
            try
            {
                await connection.QueryAsync("DELETE FROM items WHERE META().id = 'returned' RETURNING 1 / 0", cancellationToken: token);
                throw new InvalidOperationException("RETURNING projection failure succeeded.");
            }
            catch (DocumentClientException exception)
            { Check(exception.Diagnostics.Any(d => d.Code == "CD02006"), "RETURNING projection rollback diagnostic"); }
            Check(await connection.GetAsync("items", "returned", token) is not null, "RETURNING projection rollback preserved document");
            var deleteRows = await connection.QueryAsync("DELETE FROM items WHERE META().id = 'returned' RETURNING *", cancellationToken: token);
            Check(deleteRows.AffectedCount == 1 && deleteRows.Documents[0].GetProperty("n").GetInt32() == 3, "DELETE RETURNING pre-image");
            Check(await connection.ExecuteAsync("DROP COLLECTION items", cancellationToken: token) == 0, "OQL drop collection");
            Check(await connection.ExecuteAsync("DROP COLLECTION IF EXISTS items", cancellationToken: token) == 0, "absent collection");
            await connection.CreateCollectionAsync("intersection", token);
            await connection.ExecuteAsync("INSERT INTO intersection (KEY, VALUE) VALUES " + string.Join(',', Enumerable.Range(0, 200)
                .Select(i => $"('{i:D3}',{{'a':{(i < 100 ? 1 : 0)},'b':{(i is >= 99 and < 199 ? 1 : 0)}}})")), cancellationToken: token);
            await connection.ExecuteAsync("CREATE INDEX by_a ON intersection(a)", cancellationToken: token);
            await connection.ExecuteAsync("CREATE INDEX by_b ON intersection(b)", cancellationToken: token);
            const string intersectionQuery = "SELECT META().id AS id FROM intersection WHERE a=1 AND b=1";
            var intersected = await connection.QueryAsync(intersectionQuery, cancellationToken: token);
            Check(intersected.Count == 1 && intersected[0]["id"] is "099", "intersection result");
            var intersectionPlan = await connection.QueryAsync("EXPLAIN " + intersectionQuery, cancellationToken: token);
            Check(intersectionPlan.Documents.Any(document => document.GetProperty("operator").GetString() == "IndexIntersection" &&
                document.GetProperty("estimated_rows").GetInt64() == 100 &&
                document.GetProperty("detail").GetString() == "inputs=2; estimate_kind=upper_bound") &&
                intersectionPlan.Documents.Count(document => document.GetProperty("operator").GetString() == "IndexSeek") == 2 &&
                intersectionPlan.Documents.All(document => document.GetProperty("actual_rows").ValueKind == System.Text.Json.JsonValueKind.Null),
                "intersection EXPLAIN estimates");
            var unaffected = await connection.QueryAsync("SELECT COUNT(*) AS n FROM intersection", cancellationToken: token);
            Check(unaffected[0]["n"] is 200L, "intersection EXPLAIN has no effects");
            await connection.ExecuteAsync("DROP COLLECTION intersection", cancellationToken: token);
            Check((await connection.GetCollectionsAsync(token)).Count == 0, "drop collection");
            await connection.CreateCollectionAsync("reset", token);
            await connection.ExecuteAsync("BEGIN", cancellationToken: token);
            await connection.ExecuteAsync("SAVEPOINT pooled", cancellationToken: token);
            await connection.PutAsync("reset", "pending", "{}"u8.ToArray(), cancellationToken: token);
        }
        var sessionId = server.Sessions.Single().Id;
        await using (var rented = await client.ConnectAsync(token))
        {
            Check(server.Sessions.Single().Id == sessionId && server.Sessions.Single().DatabaseSession!.CurrentTransaction is null, "pooled transaction reset");
            Check(await rented.GetAsync("reset", "pending", token) is null, "pooled rollback removed writes");
            await rented.ExecuteAsync("START TRANSACTION ISOLATION LEVEL READ COMMITTED", cancellationToken: token);
            await rented.PutAsync("reset", "committed", "{}"u8.ToArray(), cancellationToken: token);
            await rented.ExecuteAsync("COMMIT", cancellationToken: token);
            Check(await rented.GetAsync("reset", "committed", token) is not null, "wire commit retained writes");
            await rented.DropCollectionAsync("reset", token);
            Check((await rented.GetCollectionsAsync(token)).Count == 0, "pooled connection");
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) { throw new InvalidOperationException(name); }
        _passed++;
        Console.WriteLine($"PASS {name}");
    }
}
