using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Tests;

[Collection("Document index concurrency")]
public sealed class DocumentSubqueryBoundaryTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - Subqueries: correlation and DML remain named deferred forms")]
    [InlineData("SELECT (SELECT RAW d.n FROM items s) FROM items d", "Correlated")]
    [InlineData("SELECT (SELECT n FROM items s UNNEST d.tags AS d) FROM items d", "Correlated")]
    [InlineData("SELECT (SELECT COUNT(*) FROM items s WHERE d.n > 0 GROUP BY s.n AS d) FROM items d", "Correlated")]
    [InlineData("SELECT (SELECT COUNT(*) FROM items s GROUP BY d.n AS d) FROM items d", "Correlated")]
    [InlineData("SELECT * FROM (SELECT n FROM items) s", "derived FROM")]
    [InlineData("INSERT INTO items (KEY, VALUE) VALUES ('x', (SELECT RAW n FROM items))", "DML")]
    [InlineData("UPSERT INTO items (KEY k) SELECT (SELECT RAW n FROM items) AS k FROM items", "DML")]
    [InlineData("UPDATE items SET n = (SELECT RAW n FROM items)", "DML")]
    [InlineData("DELETE FROM items WHERE EXISTS (SELECT RAW n FROM items)", "DML")]
    public async Task ExecuteAsync_DeferredForms_ShouldNameFormWithCapabilityCodeAsync(string query, string form)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("deferred-subqueries", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        // Act / Assert
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(query, cancellationToken: CancellationToken.None));
        error.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CK00001" && diagnostic.Message!.Contains(form, StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Subqueries: own transaction writes are visible at both levels")]
    public async Task ExecuteAsync_OwnWrites_ShouldShareTransactionSnapshotAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("own-subqueries", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        await using var transaction = await session.BeginTransactionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('own', {n:4})", cancellationToken: CancellationToken.None);
        // Act
        var rows = await DocumentSubqueryTests.RowsAsync(session, "SELECT n, (SELECT RAW n FROM items WHERE n = 4) AS own_n FROM items WHERE n = 4");
        // Assert
        rows.ShouldHaveSingleItem().GetInt32(0).ShouldBe(4);
        rows.Single().GetString(1).ShouldBe("[4]");
        await transaction.RollbackAsync(CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Subqueries: concurrent committed inserts remain outside both statement levels")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_ConcurrentInsert_ShouldSharePinnedSnapshotAsync(bool readCommitted)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var release = new ManualResetEventSlim();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int callbacks = 0;
        var builder = DocumentDatabaseEngine.CreateBuilder("snapshot-subqueries");
        builder.Functions.Add(DocumentScalarFunction.Create<decimal, decimal>("pause", value =>
        {
            if (Interlocked.Increment(ref callbacks) == 1) { entered.TrySetResult(); release.Wait(timeout.Token); }
            return value;
        }));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", timeout.Token);
        await using var reader = await database.CreateSessionAsync(timeout.Token);
        await using var writer = await database.CreateSessionAsync(timeout.Token);
        await DocumentSubqueryTests.SeedAsync(writer);
        int rounds = 0;
        var elapsed = Stopwatch.StartNew();
        do
        {
            await using var transaction = readCommitted ? await reader.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token) : null;
            release.Reset();
            callbacks = 0;
            entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = Task.Run(() => DocumentSubqueryTests.RowsAsync(reader,
                "SELECT n, (SELECT RAW pause(n) FROM items) AS all_n FROM items"), timeout.Token);
            try
            {
                await entered.Task.WaitAsync(timeout.Token);
                // Act: this commit occurs during the first nested scan, after planning.
                await writer.ExecuteAsync($"INSERT INTO items (KEY, VALUE) VALUES ('late{rounds}', {{n:99}})", cancellationToken: timeout.Token);
            }
            finally { release.Set(); }
            var rows = await pending.WaitAsync(timeout.Token);
            // Assert: both levels see exactly the inputs present before admission.
            int expected = 3 + rounds;
            rows.Count.ShouldBe(expected);
            foreach (var row in rows) { ((JsonElement)row.GetValue(1)!).GetArrayLength().ShouldBe(expected); }
            callbacks.ShouldBe(expected * expected);
            if (transaction is not null) { await transaction.CommitAsync(timeout.Token); }
            rounds++;
        } while (elapsed.Elapsed < TimeSpan.FromSeconds(1) || rounds < 4);
        rounds.ShouldBeGreaterThanOrEqualTo(4);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Subqueries: cancellation and shared memory refusal release all nested reservations")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_NestedFailure_ShouldReleaseStatementMemoryAsync(bool cancel)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        var builder = DocumentDatabaseEngine.CreateBuilder("bounded-subqueries");
        builder.Options.StatementMemoryBudget = cancel ? 64L * 1024 * 1024 : 12_000;
        builder.Functions.Add(DocumentScalarFunction.Create<decimal, string>("nested_value", value =>
        {
            calls++;
            if (cancel) { cancellation.Cancel(); }
            return new string('x', 8192);
        }));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        var request = DocumentQueryRequest.FromOql(cancel
            ? "SELECT (SELECT RAW n FROM items WHERE nested_value(n) = 'x') FROM items"
            : "SELECT (SELECT RAW nested_value(n) FROM items) FROM items");
        // Act / Assert
        await database.RunAsync(session, async operation =>
        {
            var memory = operation.GetStatementMemory(cancellation.Token);
            if (cancel)
            { await Should.ThrowAsync<OperationCanceledException>(async () => await DocumentPlanExecutor.ExecuteAsync(database, operation, request.Statement, null, cancellation.Token)); }
            else
            {
                var failure = await Should.ThrowAsync<DatabaseException>(async () => await DocumentPlanExecutor.ExecuteAsync(database, operation, request.Statement, null, cancellation.Token));
                failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05018");
            }
            memory.Bytes.ShouldBe(0L);
            return true;
        }, CancellationToken.None);
        calls.ShouldBe(1);
        if (cancel) { session.LastQueryDocumentsRead.ShouldBe(4); }
        session.LastQuerySubqueriesExecuted.ShouldBe(1);
        (await DocumentSubqueryTests.RowsAsync(session, "SELECT n FROM items")).Count.ShouldBe(3);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Subqueries: EXPLAIN owns nested plans and replans after index DDL")]
    public async Task ExecuteAsync_Explain_ShouldShowNestedPlansAndCurrentIndexesAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("explain-subqueries", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        const string text = "EXPLAIN SELECT (SELECT RAW n FROM items WHERE n = 2) AS nested_n FROM items";
        var request = DocumentQueryRequest.FromOql(text);
        async Task<List<DatabaseRow>> ExplainAsync()
        {
            await using var result = (DatabaseResultSet)await session.ExecuteAsync(request, CancellationToken.None);
            var rows = new List<DatabaseRow>();
            await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { rows.Add(row); }
            return rows;
        }
        // Act / Assert
        var before = await ExplainAsync();
        before.Count(row => row.GetString(2) == "SubqueryExpression").ShouldBe(1);
        before.Single(row => row.GetString(2) == "SubqueryExpression").GetString(5)!.ShouldContain("cached_for_statement", Case.Sensitive);
        session.LastQuerySubqueriesExecuted.ShouldBe(0);
        await session.ExecuteAsync("CREATE INDEX ix ON items (n)", cancellationToken: CancellationToken.None);
        (await ExplainAsync()).ShouldContain(row => row.GetString(2) == "IndexSeek" && row.GetString(4) == "ix");
        await session.ExecuteAsync("DROP INDEX ix ON items", cancellationToken: CancellationToken.None);
        (await ExplainAsync()).ShouldNotContain(row => row.GetString(2) == "IndexSeek");
        var volatilePlan = await DocumentSubqueryTests.RowsAsync(session, "EXPLAIN SELECT (SELECT RAW NOW_STR() FROM items) FROM items");
        volatilePlan.Single(row => row.GetString(2) == "SubqueryExpression").GetString(5)!.ShouldContain("per_evaluation", Case.Sensitive);
    }
    [Fact(DisplayName = "Cohesion Test [Documents] - Subqueries: consumed index order retains paging on both levels")]
    public async Task ExecuteAsync_OrderedSubqueries_ShouldConsumeIndexWithoutChangingResultsAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("ordered-subqueries", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        await session.ExecuteAsync("CREATE INDEX ix ON items (n)", cancellationToken: CancellationToken.None);
        // Act
        var rows = await DocumentSubqueryTests.RowsAsync(session,
            "SELECT n+0 AS number, (SELECT RAW n+0 FROM items WHERE n >= 1 ORDER BY n DESC LIMIT 2) AS values_n FROM items WHERE n >= 1 ORDER BY n DESC LIMIT 2");
        // Assert
        rows.Select(row => row.GetInt32(0)).ShouldBe([3, 2]);
        rows.Select(row => row.GetString(1)).ShouldBe(["[3,2]", "[3,2]"]);
        session.LastQuerySubqueriesExecuted.ShouldBe(1);
        session.LastQueryDocumentsRead.ShouldBe(4);
        session.LastQueryIndexEntriesRead.ShouldBeGreaterThanOrEqualTo(4);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Subqueries: ordinary SELECT star returns object elements for scalar roots")]
    public async Task ExecuteAsync_StarSubquery_ShouldWrapNonobjectRootsAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("star-subqueries", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        await session.CreateCollectionAsync("roots", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO roots (KEY, VALUE) VALUES ('a', 42), ('b', {x:1}), ('c', [1]), ('d', NULL)", cancellationToken: CancellationToken.None);
        // Act
        var rows = await DocumentSubqueryTests.RowsAsync(session, "SELECT (SELECT * FROM roots) FROM items LIMIT 1");
        // Assert
        rows.Single().GetString(0).ShouldBe("[{\"document\":42},{\"x\":1},{\"document\":[1]},{\"document\":null}]");
    }

}
