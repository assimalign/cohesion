using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection("SQL isolation configuration")]
public sealed class SqlIsolationReviewTests
{
    private readonly ITestOutputHelper _output;

    public SqlIsolationReviewTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Sql] - Isolation: interlock refusals consume the first statement window")]
    [InlineData("CREATE TABLE attempted(id INT)", true)]
    [InlineData("CREATE VIEW attempted AS SELECT id FROM t", false)]
    [InlineData("DROP SCHEMA attempted", false)]
    [InlineData("TRUNCATE t", true)]
    [InlineData("CREATE TABLE INFORMATION_SCHEMA.attempted(id INT)", true)]
    [InlineData("UPDATE INFORMATION_SCHEMA.TABLES SET TABLE_NAME='attempted'", true)]
    public async Task ExecuteAsync_InterlockRefusal_ShouldCloseConfiguration(string sql, bool definition)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("isolation-interlock", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(id INT)", cancellationToken: CancellationToken.None);
        var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);

        // Act: the lease is the same admission refusal held by another session's DDL.
        using (database.DdlInterlock.EnterMetadata("db", definition))
        {
            await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None));
        }
        var result = await session.ExecuteAsync("SET TRANSACTION ISOLATION LEVEL REPEATABLE READ", cancellationToken: CancellationToken.None);

        // Assert
        result.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS04072");
        transaction.IsolationLevel.ShouldBe(IsolationLevel.ReadCommitted);
        transaction.RunningStatements.ShouldBe(0);
        database.Coordinator.GetOpenContexts().ShouldHaveSingleItem().ShouldBeSameAs(transaction.Context);
        await transaction.RollbackAsync(CancellationToken.None);
        database.Coordinator.GetOpenContexts().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Isolation: parse errors precede DDL interlocks and leave configuration open")]
    public async Task ExecuteAsync_TypedParseFailureUnderLease_ShouldKeepConfigurationOpen()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("isolation-parse-admission", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        var request = new SqlQueryRequest(new SqlQueryParser().Parse("CREATE TABLE attempted(id INT, )").ShouldBeOfType<SqlQueryStatement>());
        request.Statement.Diagnostics.ShouldContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        // Act / Assert
        using (database.DdlInterlock.EnterMetadata("db", true))
        {
            await Should.ThrowAsync<DatabaseParseException>(async () => await session.ExecuteAsync(request, CancellationToken.None));
        }
        (await session.ExecuteAsync("SET TRANSACTION ISOLATION LEVEL REPEATABLE READ", cancellationToken: CancellationToken.None)).Status.ShouldBe(DatabaseResultStatus.Success);
        transaction.IsolationLevel.ShouldBe(IsolationLevel.Snapshot);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Isolation: the configuration pin begins outside the truncate gate")]
    public async Task BeginTransactionAsync_TruncateGateHeld_ShouldRegisterOnlyThePin()
    {
        // Arrange
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("isolation-pin-gate", new());
        var database = await engine.CreateDatabaseAsync("db", ceiling.Token);
        await using var session = await database.CreateSessionAsync(ceiling.Token);
        await database.Catalog.TruncateSnapshotGate.WaitAsync(ceiling.Token);
        Task<SqlDatabaseTransaction>? beginning = null;

        // Act / Assert: no scheduler or duration decides whether the pin registered.
        try
        {
            beginning = session.BeginTransactionAsync(IsolationLevel.ReadCommitted, ceiling.Token).AsTask();
            beginning.IsCompleted.ShouldBeFalse();
            database.Coordinator.GetOpenContexts().ShouldHaveSingleItem();
        }
        finally
        {
            database.Catalog.TruncateSnapshotGate.Release();
            if (beginning is not null) { await (await beginning.WaitAsync(ceiling.Token)).RollbackAsync(ceiling.Token); }
        }
        database.Coordinator.GetOpenContexts().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Isolation: a pin before TRUNCATE uses the context and catalog captured after publication")]
    public async Task BeginTransactionAsync_PinPredatesTruncate_ShouldUsePublishedBindings()
    {
        // Arrange
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("isolation-pin-publication", new());
        var database = await engine.CreateDatabaseAsync("db", ceiling.Token);
        await using var reader = await database.CreateSessionAsync(ceiling.Token);
        await using var writer = await database.CreateSessionAsync(ceiling.Token);
        await writer.ExecuteAsync("CREATE TABLE t(id INT)", cancellationToken: ceiling.Token);
        await writer.ExecuteAsync("INSERT INTO t VALUES(1)", cancellationToken: ceiling.Token);
        int prepared = 0, published = 0;
        SqlPlanExecutor.TruncatePrepared = () => prepared++;
        SqlPlanExecutor.TruncatePublished = () => published++;
        await database.Catalog.TruncateSnapshotGate.WaitAsync(ceiling.Token);
        Task<DatabaseResult>? truncating = null;
        Task<SqlDatabaseTransaction>? beginning = null;
        try
        {
            // Act: queue publication first, then BEGIN. Only the pin may register while
            // the gate is held. Publication completes before BEGIN captures its context.
            truncating = writer.ExecuteAsync("TRUNCATE t", cancellationToken: ceiling.Token).AsTask();
            prepared.ShouldBe(1);
            // Discovery briefly registers its own snapshot pin for the prepared
            // owner. Finish that work before counting the two application contexts.
            database.TableReclamation.Request();
            database.TableReclamation.RunBatch(ceiling.Token);
            beginning = reader.BeginTransactionAsync(IsolationLevel.ReadCommitted, ceiling.Token).AsTask();
            database.Coordinator.GetOpenContexts().Count.ShouldBe(2);
        }
        finally
        {
            database.Catalog.TruncateSnapshotGate.Release();
            if (truncating is not null) { (await truncating.WaitAsync(ceiling.Token)).Status.ShouldBe(DatabaseResultStatus.Success); }
            if (beginning is not null) { await beginning.WaitAsync(ceiling.Token); }
            SqlPlanExecutor.TruncatePrepared = null;
            SqlPlanExecutor.TruncatePublished = null;
        }

        // Assert: the old pin cannot force an old storage binding into the newer context.
        published.ShouldBe(1);
        (await reader.ExecuteAsync("SET TRANSACTION ISOLATION LEVEL REPEATABLE READ", cancellationToken: ceiling.Token)).Status.ShouldBe(DatabaseResultStatus.Success);
        await using var rows = (await reader.ExecuteAsync("SELECT id FROM t", cancellationToken: ceiling.Token)).ShouldBeAssignableTo<DatabaseResultSet>();
        int count = 0;
        await foreach (var _ in rows.GetRowsAsync(ceiling.Token)) { count++; }
        count.ShouldBe(0);
        await reader.CurrentTransaction.ShouldNotBeNull().RollbackAsync(ceiling.Token);
        database.Coordinator.GetOpenContexts().ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Isolation: read committed releases its begin catalog at the first attempted statement")]
    [InlineData("SELECT 1")]
    [InlineData("SELECT 1/0")]
    [InlineData("CREATE TABLE attempted(id INT)")]
    public async Task ExecuteAsync_ReadCommittedFirstStatement_ShouldReleaseBeginCatalog(string sql)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("isolation-catalog-release", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        var field = typeof(SqlDatabaseTransaction).GetField("_catalogSnapshot", BindingFlags.Instance | BindingFlags.NonPublic).ShouldNotBeNull();
        field.GetValue(transaction).ShouldNotBeNull();

        // Act
        try { var result = await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None); if (result is DatabaseResultSet rows) { await rows.DisposeAsync(); } }
        catch (DatabaseException) { /* An attempted data statement still closes configuration. */ }

        // Assert
        field.GetValue(transaction).ShouldBeNull();
        database.Coordinator.GetOpenContexts().ShouldHaveSingleItem();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Isolation: outside SET succeeds with one warning and leaves the default unchanged")]
    [InlineData("READ COMMITTED")][InlineData("SERIALIZABLE")]
    public async Task ExecuteAsync_SetOutsideBlock_ShouldWarnWithoutChangingDefaults(string level)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("isolation-outside-warning", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var result = await session.ExecuteAsync("SET TRANSACTION ISOLATION LEVEL " + level, cancellationToken: CancellationToken.None);

        // Assert
        result.Status.ShouldBe(DatabaseResultStatus.Success);
        var diagnostic = result.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CS04071");
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Warning);
        session.CurrentTransaction.ShouldBeNull();
        database.Coordinator.GetOpenContexts().ShouldBeEmpty();
        await using var transaction = await session.BeginTransactionAsync(CancellationToken.None);
        transaction.IsolationLevel.ShouldBe(IsolationLevel.Snapshot);
    }

#if !DEBUG
    [Theory(DisplayName = "Cohesion Test [Sql] - Isolation: warmed explicit BEGIN COMMIT pairs share unchanged catalog captures")]
    [InlineData(IsolationLevel.Snapshot)][InlineData(IsolationLevel.ReadCommitted)]
    public async Task BeginTransactionAsync_WarmedPairs_ShouldReduceAllocations(IsolationLevel level)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("isolation-begin-cost", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        Measure(1000);

        // Act
        long minimum = long.MaxValue;
        for (int round = 0; round < 3; round++) { minimum = Math.Min(minimum, Measure(5000)); }

        // Assert
        _output.WriteLine($"{level}: {minimum / 5000.0:F3} B/BEGIN-COMMIT pair");
        minimum.ShouldBeLessThanOrEqualTo(1048L * 5000);

        long Measure(int count)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < count; i++)
            {
                var pending = session.BeginTransactionAsync(level, CancellationToken.None);
                if (!pending.IsCompletedSuccessfully) { throw new InvalidOperationException("BEGIN probe suspended."); }
                var commit = pending.Result.CommitAsync(CancellationToken.None);
                if (!commit.IsCompletedSuccessfully) { throw new InvalidOperationException("COMMIT probe suspended."); }
                commit.GetAwaiter().GetResult();
            }
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }
#endif
}
