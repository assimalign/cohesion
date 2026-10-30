using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentDmlSubqueryTests
{
    [Fact(DisplayName = "Cohesion Test [Documents] - DML subqueries: INSERT SELECT keeps the storage constructor depth diagnostic")]
    public async Task ExecuteAsync_WriteConstructorDepth_ShouldMatchSynchronousFailureAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("write-depth-parity", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        await session.CreateCollectionAsync("target", CancellationToken.None);
        string? expected = null;
        // Act / Assert
        foreach (string extra in new[] { "", " AND EXISTS (SELECT RAW 1 FROM items)" })
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
                "INSERT INTO target (KEY k, VALUE v) SELECT 'x' AS k, {a:@deep} AS v FROM items WHERE n = 1" + extra,
                DocumentSubqueryRegressionTests.DeepParameters(), CancellationToken.None));
            expected ??= error.Diagnostics.Single().Code;
            error.Diagnostics.Single().Code.ShouldBe(expected);
        }
        (await DocumentSubqueryTests.RowsAsync(session, "SELECT * FROM target")).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - DML subqueries: pending VALUES owns its charges until staging ends")]
    public async Task ExecuteAsync_ValuesOwnership_ShouldReleaseExactlyOnceAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("values-ownership", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        var request = DocumentQueryRequest.FromOql("INSERT INTO items (KEY, VALUE) VALUES ('d', {a:(SELECT RAW n FROM items)})");
        // Act / Assert
        await database.RunAsync(session, async operation =>
        {
            var memory = operation.GetStatementMemory(CancellationToken.None);
            await DocumentPlanExecutor.ExecuteAsync(database, operation, request.Statement, null, CancellationToken.None);
            memory.Bytes.ShouldBe(0);
            return true;
        }, CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - DML subqueries: lazy RETURNING reads nothing and exposes the committed versions")]
    public async Task ExecuteAsync_LazyReturning_ShouldUseActualReservedVersionsAsync()
    {
        // Arrange
        int calls = 0;
        var builder = DocumentDatabaseEngine.CreateBuilder("lazy-returning-correlation");
        builder.Functions.Add(DocumentScalarFunction.Create<decimal, decimal>("tick", n => { calls++; return n; }));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        // Act
        var rows = await DocumentSubqueryTests.RowsAsync(session,
            "UPDATE items SET n = n + 10 RETURNING META().id AS id, META().version AS version, CASE WHEN TRUE THEN 1 ELSE (SELECT RAW tick(n) FROM items) END AS v");
        // Assert
        session.LastQuerySubqueriesExecuted.ShouldBe(0);
        session.LastQueryDocumentsRead.ShouldBe(3);
        calls.ShouldBe(0);
        var stored = await DocumentSubqueryTests.RowsAsync(session, "SELECT META().id AS id, META().version AS version FROM items");
        rows.Select(row => (row.GetString(0), row.GetValue(1))).ShouldBe(stored.Select(row => (row.GetString(0), row.GetValue(1))));
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Subqueries: mutation parity includes predicates values sources and RETURNING")]
    [InlineData("UPDATE items d SET n = n RETURNING [d.n] AS a", "UPDATE items d SET n = n RETURNING (SELECT RAW d.n FROM [1] AS s) AS a")]
    [InlineData("UPDATE items d SET n = n + 10 WHERE n > 1 RETURNING n", "UPDATE items d SET n = (FIRST v FOR v IN (SELECT RAW d.n + 10 FROM [1] AS s) END) WHERE EXISTS (SELECT RAW s.n FROM items s WHERE s.n = d.n AND s.n > 1) RETURNING n")]
    [InlineData("DELETE FROM items WHERE n = 2 RETURNING n", "DELETE FROM items d WHERE d.n IN (SELECT RAW s.n FROM items s WHERE s.n = 2) RETURNING n")]
    [InlineData("INSERT INTO target (KEY, VALUE) VALUES ('x', {a:[1]}) RETURNING a", "INSERT INTO target (KEY, VALUE) VALUES ('x', {a:(SELECT RAW n FROM items WHERE n = 1)}) RETURNING a")]
    [InlineData("UPSERT INTO target (KEY, VALUE) VALUES ('x', {a:[1]}) RETURNING a", "UPSERT INTO target (KEY, VALUE) VALUES ('x', {a:(SELECT RAW n FROM items WHERE n = 1)}) RETURNING a")]
    [InlineData("INSERT INTO target (KEY k, VALUE v) SELECT 'x' AS k, {a:[d.n]} AS v FROM items d WHERE n = 1", "INSERT INTO target (KEY k, VALUE v) SELECT 'x' AS k, {a:(SELECT RAW d.n FROM [1] AS s)} AS v FROM items d WHERE n = 1")]
    [InlineData("UPSERT INTO target (KEY k, VALUE v) SELECT 'x' AS k, {a:[d.n]} AS v FROM items d WHERE n = 1", "UPSERT INTO target (KEY k, VALUE v) SELECT 'x' AS k, {a:[d.n]} AS v FROM (SELECT n FROM items WHERE n = 1) AS d")]
    public async Task ExecuteAsync_MutationPositions_ShouldMatchSynchronousSemanticsAsync(string synchronous, string asynchronous)
    {
        // Arrange / Act
        var expected = await EvaluateAsync(synchronous);
        var actual = await EvaluateAsync(asynchronous);
        // Assert
        actual.ShouldBe(expected);

        static async Task<string[]> EvaluateAsync(string statement)
        {
            await using var engine = DocumentDatabaseEngine.Create("dml-parity", new());
            var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await DocumentSubqueryTests.SeedAsync(session);
            await session.CreateCollectionAsync("target", CancellationToken.None);
            var output = new List<string>();
            var result = await session.ExecuteAsync(statement, cancellationToken: CancellationToken.None);
            if (result is DatabaseResultSet set)
            {
                await using (set)
                {
                    await foreach (var row in set.GetRowsAsync(CancellationToken.None))
                    { output.Add(string.Join("|", Enumerable.Range(0, row.FieldCount).Select(i => Cell(row.GetValue(i))))); }
                }
            }
            foreach (string collection in new[] { "items", "target" })
            {
                foreach (var row in await DocumentSubqueryTests.RowsAsync(session, "SELECT * FROM " + collection))
                { output.Add(collection + ":" + Cell(row.GetValue(0))); }
            }
            return output.ToArray();
        }

        static string Cell(object? value) => value is JsonElement json ? json.GetRawText() : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "NULL";
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - DML subqueries: staged ordered reads include earlier transaction writes")]
    public async Task ExecuteAsync_IndexedReturning_ShouldIncludeEarlierOwnWritesAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("indexed-returning-correlation", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        await session.ExecuteAsync("CREATE INDEX ix ON items (n)", cancellationToken: CancellationToken.None);
        await using var transaction = await session.BeginTransactionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('d', {n:4})", cancellationToken: CancellationToken.None);
        // Act
        var rows = await DocumentSubqueryTests.RowsAsync(session,
            "UPDATE items SET n = n + 10 RETURNING n, (SELECT RAW s.n FROM items s ORDER BY s.n DESC LIMIT 1) AS old, META().version AS version, (SELECT RAW META(items).version FROM [1] AS s) AS nested_version");
        // Assert
        rows.Count.ShouldBe(4);
        foreach (var row in rows)
        {
            ((JsonElement)row.GetValue(1)!).GetRawText().ShouldBe("[4]");
            ((JsonElement)row.GetValue(3)!)[0].GetDecimal().ShouldBe((decimal)row.GetValue(2)!);
        }
        await transaction.RollbackAsync(CancellationToken.None);
        (await DocumentSubqueryTests.RowsAsync(session, "SELECT n FROM items")).Select(row => row.GetValue(0)).ShouldBe(new object?[] { 1m, 2m, 3m });
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Subqueries: one shared work limit refuses depth-two writes and releases memory")]
    public async Task ExecuteAsync_SharedWorkLimit_ShouldRefuseAndReleaseAsync()
    {
        // Arrange
        var builder = DocumentDatabaseEngine.CreateBuilder("work-correlation");
        builder.Options.StatementWorkBudget = 16;
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        var request = DocumentQueryRequest.FromOql("UPDATE items d SET a = (SELECT RAW (SELECT RAW d.n FROM [1,2] AS b) FROM [1,2] AS a)");
        // Act / Assert
        await database.RunAsync(session, async operation =>
        {
            var memory = operation.GetStatementMemory(CancellationToken.None);
            var error = await Should.ThrowAsync<DatabaseException>(async () => await DocumentPlanExecutor.ExecuteAsync(database, operation, request.Statement, null, CancellationToken.None));
            error.Diagnostics.Single().Code.ShouldBe("CD05020");
            error.Diagnostics.Single().Start.ShouldNotBeNull();
            session.LastQuerySubqueriesExecuted.ShouldBe(3);
            memory.Bytes.ShouldBe(0);
            return true;
        }, CancellationToken.None);
        (await DocumentSubqueryTests.RowsAsync(session, "SELECT n FROM items")).Select(row => row.GetValue(0)).ShouldBe(new object?[] { 1m, 2m, 3m });
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - DML subqueries: self reads always observe statement-start content")]
    [InlineData("UPDATE items d SET n = d.n + 10, old = (SELECT RAW s.n FROM items s ORDER BY s.n) WHERE EXISTS (SELECT RAW v FROM [d.n] AS v)", false)]
    [InlineData("DELETE FROM items d WHERE d.n IN (SELECT RAW s.n FROM items s)", true)]
    public async Task ExecuteAsync_SelfRead_ShouldStageBeforeWritesAsync(string statement, bool delete)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("dml-correlation", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        // Act
        await session.ExecuteAsync(statement, cancellationToken: CancellationToken.None);
        var rows = await DocumentSubqueryTests.RowsAsync(session, "SELECT * FROM items");
        // Assert
        rows.Count.ShouldBe(delete ? 0 : 3);
        if (!delete)
        {
            rows.Select(row => ((JsonElement)row.GetValue(0)!).GetProperty("n").GetInt32()).ShouldBe(new[] { 11, 12, 13 });
            rows.All(row => ((JsonElement)row.GetValue(0)!).GetProperty("old").GetRawText() == "[1,2,3]").ShouldBeTrue();
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - DML subqueries: RETURNING keeps actual images and statement-start nested reads")]
    [InlineData("UPDATE items d SET n = n + 10 RETURNING n, (SELECT RAW tick(s.n) FROM items s ORDER BY s.n) AS old", "[1,2,3]", 3)]
    [InlineData("DELETE FROM items d WHERE TRUE RETURNING n, (SELECT RAW tick(s.n) FROM items s ORDER BY s.n) AS old", "[1,2,3]", 3)]
    [InlineData("INSERT INTO items (KEY, VALUE) VALUES ('d', {n:4}), ('e', {n:5}) RETURNING n, (SELECT RAW tick(s.n) FROM items s ORDER BY s.n) AS old", "[1,2,3]", 2)]
    [InlineData("UPSERT INTO items (KEY, VALUE) VALUES ('a', {n:4}), ('b', {n:5}) RETURNING n, (SELECT RAW tick(s.n) FROM items s ORDER BY s.n) AS old", "[1,2,3]", 2)]
    public async Task ExecuteAsync_Returning_ShouldReadStatementStartInputAsync(string statement, string expected, int count)
    {
        // Arrange
        int calls = 0;
        var builder = DocumentDatabaseEngine.CreateBuilder("returning-correlation");
        builder.Functions.Add(DocumentScalarFunction.Create<decimal, decimal>("tick", n => { calls++; return n; }));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        // Act
        var rows = await DocumentSubqueryTests.RowsAsync(session, statement);
        // Assert
        rows.Count.ShouldBe(count);
        foreach (var row in rows) { ((JsonElement)row.GetValue(1)!).GetRawText().ShouldBe(expected); }
        calls.ShouldBe(count * 3);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - DML subqueries: VALUES derived and expression SELECT sources share nested execution")]
    [InlineData("INSERT INTO target (KEY, VALUE) VALUES ('x', {a:(SELECT RAW n FROM items)})", "{\"a\":[1,2,3]}")]
    [InlineData("UPSERT INTO target (KEY, VALUE) VALUES ('x', {a:(SELECT RAW n FROM items)})", "{\"a\":[1,2,3]}")]
    [InlineData("INSERT INTO target (KEY k, VALUE v) SELECT 'x' AS k, {a:(SELECT RAW n FROM items)} AS v FROM [1] AS d", "{\"a\":[1,2,3]}")]
    [InlineData("UPSERT INTO target (KEY k, VALUE v) SELECT 'x' AS k, {a:(SELECT RAW n FROM items)} AS v FROM (SELECT RAW n FROM items LIMIT 1) AS d", "{\"a\":[1,2,3]}")]
    public async Task ExecuteAsync_WriteSources_ShouldPreserveArrayValuesAsync(string statement, string expected)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("write-correlation", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        await session.CreateCollectionAsync("target", CancellationToken.None);
        // Act
        await session.ExecuteAsync(statement, cancellationToken: CancellationToken.None);
        // Assert
        ((JsonElement)(await DocumentSubqueryTests.RowsAsync(session, "SELECT * FROM target")).Single().GetValue(0)!).GetRawText().ShouldBe(expected);
        var plan = await DocumentSubqueryTests.RowsAsync(session, "EXPLAIN " + statement);
        plan.ShouldContain(row => row.GetString(2) == "SubqueryExpression");
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - DML subqueries: depth-two cancellation and refusal unwind the shared owner")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_DepthTwoFailure_ShouldReleaseEverythingAsync(bool cancel)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        var builder = DocumentDatabaseEngine.CreateBuilder("depth-correlation");
        builder.Options.StatementMemoryBudget = cancel ? 64 * 1024 * 1024 : 12000;
        builder.Functions.Add(DocumentScalarFunction.Create<decimal, string>("payload", n =>
        { calls++; if (cancel) { cancellation.Cancel(); } return new string('x', 8192); }));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        var request = DocumentQueryRequest.FromOql("UPDATE items d SET v = (SELECT RAW (SELECT RAW payload(d.n) FROM [1] AS b) FROM [1] AS a)");
        // Act / Assert
        await database.RunAsync(session, async operation =>
        {
            var memory = operation.GetStatementMemory(cancellation.Token);
            if (cancel) { await Should.ThrowAsync<OperationCanceledException>(async () => await DocumentPlanExecutor.ExecuteAsync(database, operation, request.Statement, null, cancellation.Token)); }
            else
            {
                var error = await Should.ThrowAsync<DatabaseException>(async () => await DocumentPlanExecutor.ExecuteAsync(database, operation, request.Statement, null, cancellation.Token));
                error.Diagnostics.Single().Code.ShouldBe("CD05018");
            }
            memory.Bytes.ShouldBe(0);
            return true;
        }, CancellationToken.None);
        calls.ShouldBe(1);
        (await DocumentSubqueryTests.RowsAsync(session, "SELECT n FROM items")).Select(row => row.GetValue(0)).ShouldBe(new object?[] { 1m, 2m, 3m });
    }
}
