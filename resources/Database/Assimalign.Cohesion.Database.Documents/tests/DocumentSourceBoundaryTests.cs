using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Documents.Tests;

[Collection("Document index concurrency")]
public sealed class DocumentSourceBoundaryTests
{
    [Fact(DisplayName = "Cohesion Test [Documents] - FROM: streamed system rows charge the current borrowed input")]
    public async Task ExecuteAsync_SystemSource_ShouldChargeInputAndReleaseOnRefusalAsync()
    {
        // Arrange
        var builder = DocumentDatabaseEngine.CreateBuilder("system-source-budget");
        builder.Options.StatementMemoryBudget = 1_200;
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        var request = DocumentQueryRequest.FromOql("SELECT RAW 1 FROM (SELECT RAW 1 FROM COHESION_SCHEMA.OBJECT_OWNERSHIP) d");
        // Act / Assert
        await database.RunAsync(session, async operation =>
        {
            var memory = operation.GetStatementMemory(CancellationToken.None);
            var error = await Should.ThrowAsync<DatabaseException>(async () => await DocumentPlanExecutor.ExecuteAsync(database, operation, request.Statement, null, CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05018");
            memory.Bytes.ShouldBe(0L);
            return true;
        }, CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - FROM: blocking inputs and active UNNEST arrays share the retention budget")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_BlockingSource_ShouldRefuseBudgetAndReleaseAllRowsAsync(bool unnest)
    {
        // Arrange
        var builder = DocumentDatabaseEngine.CreateBuilder("blocking-source-budget");
        builder.Options.StatementMemoryBudget = 12_000;
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var request = DocumentQueryRequest.FromOql(unnest
            ? "SELECT RAW v FROM [1] AS d UNNEST ARRAY x FOR x IN [" + string.Join(",", Enumerable.Range(0, 1000)) + "] END AS v LIMIT 1"
            : "SELECT v, COUNT(*) FROM [" + string.Join(",", Enumerable.Range(0, 50)) + "] AS v GROUP BY v");
        // Act / Assert
        await database.RunAsync(session, async operation =>
        {
            var memory = operation.GetStatementMemory(CancellationToken.None);
            var error = await Should.ThrowAsync<DatabaseException>(async () => await DocumentPlanExecutor.ExecuteAsync(database, operation, request.Statement, null, CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05018");
            memory.Bytes.ShouldBe(0L);
            return true;
        }, CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - FROM: limited source success transfers only final result ownership")]
    public async Task ExecuteAsync_LimitedSource_ShouldReleaseCursorAndResultReservationsAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("source-disposal", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        var request = DocumentQueryRequest.FromOql("SELECT * FROM (SELECT n FROM items) d LIMIT 1");
        // Act / Assert
        await database.RunAsync(session, async operation =>
        {
            var memory = operation.GetStatementMemory(CancellationToken.None);
            var result = (DatabaseResultSet)await DocumentPlanExecutor.ExecuteAsync(database, operation, request.Statement, null, CancellationToken.None);
            memory.Bytes.ShouldBeGreaterThan(0L);
            session.LastQueryDocumentsRead.ShouldBe(1);
            await result.DisposeAsync();
            memory.Bytes.ShouldBe(0L);
            return true;
        }, CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - FROM: failure and cancellation unwind inner cursors and reservations")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_SourceFailure_ShouldReleaseAllRetainedMemoryAsync(bool cancel, bool blocking)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        var builder = DocumentDatabaseEngine.CreateBuilder("bounded-sources");
        builder.Options.StatementMemoryBudget = cancel ? 64L * 1024 * 1024 : 12_000;
        builder.Functions.Add(DocumentScalarFunction.Create<decimal, string>("source_value", value =>
        {
            calls++;
            if (cancel) { cancellation.Cancel(); }
            return new string('x', 8192);
        }));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        var request = DocumentQueryRequest.FromOql("SELECT RAW d FROM (SELECT RAW source_value(n) FROM items) d" + (blocking ? " ORDER BY d" : " LIMIT 1"));
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
        session.LastQueryDocumentsRead.ShouldBe(1);
        (await DocumentSubqueryTests.RowsAsync(session, "SELECT n FROM items")).Count.ShouldBe(3);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - FROM: own writes and nested source plans use the same transaction")]
    public async Task ExecuteAsync_OwnWritesAndExplain_ShouldShareSnapshotAndDescribeNestedPlansAsync()
    {
        // Arrange
        int calls = 0;
        var builder = DocumentDatabaseEngine.CreateBuilder("explain-sources");
        builder.Functions.Add(DocumentScalarFunction.Create<decimal, decimal>("tick", value => { calls++; return value; }));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        await using var transaction = await session.BeginTransactionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('own', {n:4})", cancellationToken: CancellationToken.None);
        // Act
        var rows = await DocumentSubqueryTests.RowsAsync(session, "SELECT RAW n FROM (SELECT n FROM items WHERE n = 4) d");
        var plan = await DocumentSubqueryTests.RowsAsync(session, "EXPLAIN SELECT d.n FROM (SELECT tick(n) AS n FROM items) d ORDER BY d.n DESC LIMIT 1");
        // Assert
        rows.ShouldHaveSingleItem().GetValue(0).ShouldBe(4m);
        plan.Count(row => row.GetString(2) == "DerivedSource").ShouldBe(1);
        plan.ShouldContain(row => row.GetString(2) == "CollectionScan" && row.GetString(3) == "items");
        plan.Single(row => row.GetString(2) == "DerivedSource").GetString(5)!.ShouldContain("sort=outer", Case.Sensitive);
        calls.ShouldBe(0);
        var expression = await DocumentSubqueryTests.RowsAsync(session, "EXPLAIN SELECT v FROM [1,2] AS v");
        expression.ShouldContain(row => row.GetString(2) == "ExpressionSource");
        var expressionQuery = await DocumentSubqueryTests.RowsAsync(session, "EXPLAIN SELECT RAW v FROM COALESCE((SELECT RAW n FROM items), []) AS v");
        int expressionId = expressionQuery.Single(row => row.GetString(2) == "ExpressionSource").GetInt32(0);
        expressionQuery.Single(row => row.GetString(2) == "SubqueryExpression").GetInt32(1).ShouldBe(expressionId);
        await transaction.RollbackAsync(CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - FROM: a concurrent commit cannot enter a statement's derived source")]
    public async Task ExecuteAsync_ConcurrentInsert_ShouldKeepStatementSnapshotAsync()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var release = new ManualResetEventSlim();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var builder = DocumentDatabaseEngine.CreateBuilder("snapshot-sources");
        builder.Functions.Add(DocumentScalarFunction.Create<decimal, decimal>("pause", value =>
        {
            if (Interlocked.Increment(ref calls) == 1) { entered.TrySetResult(); release.Wait(timeout.Token); }
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
            release.Reset();
            calls = 0;
            entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = Task.Run(() => DocumentSubqueryTests.RowsAsync(reader,
                "SELECT d.n FROM (SELECT pause(n) AS n FROM items) d"), timeout.Token);
            try
            {
                await entered.Task.WaitAsync(timeout.Token);
                // Act: commit after the inner cursor has started.
                await writer.ExecuteAsync($"INSERT INTO items (KEY, VALUE) VALUES ('late{rounds}', {{n:99}})", cancellationToken: timeout.Token);
            }
            finally { release.Set(); }
            var rows = await pending.WaitAsync(timeout.Token);
            // Assert
            rows.Count.ShouldBe(3 + rounds);
            calls.ShouldBe(3 + rounds);
            rounds++;
        } while (elapsed.Elapsed < TimeSpan.FromSeconds(1) || rounds < 4);
        rounds.ShouldBeGreaterThanOrEqualTo(4);
    }
}
