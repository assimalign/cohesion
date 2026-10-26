using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Client;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(SqlLockWaitCollection.Name)]
public sealed partial class SqlTruncateReviewTests
{
    private readonly ITestOutputHelper _output;

    public SqlTruncateReviewTests(ITestOutputHelper output) { _output = output; }

    [Fact(DisplayName = "Cohesion Test [Sql] - TRUNCATE: removing an FK while CASCADE waits cannot erase its former child (#1716)")]
    public async Task ExecuteAsync_RemovedDependentDuringLockWait_ShouldRefuseStaleClosure()
    {
        // Arrange: DROP queues on c before TRUNCATE reaches c, after closing over p and c.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("truncate-shrink", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var a = await database.CreateSessionAsync(timeout.Token);
        await using var b = await database.CreateSessionAsync(timeout.Token);
        await using var t = await database.CreateSessionAsync(timeout.Token);
        await ExecAsync(a, "CREATE TABLE p (id INT PRIMARY KEY)", timeout.Token);
        await ExecAsync(a, "CREATE TABLE c (id INT, pid INT, CONSTRAINT fk FOREIGN KEY(pid) REFERENCES p(id))", timeout.Token);
        await ExecAsync(a, "INSERT INTO p VALUES (1)", timeout.Token);
        await ExecAsync(a, "INSERT INTO c VALUES (1,1)", timeout.Token);
        await using var transaction = await a.BeginTransactionAsync(timeout.Token);
        await ExecAsync(a, "INSERT INTO c VALUES (2,1)", timeout.Token);
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int waits = 0;
        using var listener = new LockWaitListener(database.Name);
        listener.Observed = written =>
        {
            if (written.EventName != "LockWaitStart") { return; }
            if (Interlocked.Increment(ref waits) == 1) { first.TrySetResult(); }
            else { second.TrySetResult(); }
        };
        Task? dropping = null;
        Task? truncating = null;
        try
        {
            dropping = ExecAsync(b, "ALTER TABLE c DROP CONSTRAINT fk", timeout.Token);
            await first.Task.WaitAsync(timeout.Token);
            truncating = ExecAsync(t, "TRUNCATE p CASCADE", timeout.Token);
            await second.Task.WaitAsync(timeout.Token);
            Volatile.Read(ref waits).ShouldBe(2);

            // Act
            await transaction.CommitAsync(timeout.Token);
            await dropping;
            var failure = await Should.ThrowAsync<DatabaseException>(async () => await truncating);

            // Assert
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS04064");
            (await RowsAsync(a, "SELECT id FROM c ORDER BY id", timeout.Token)).Select(row => row[0]).ShouldBe(new object?[] { 1, 2 });
            (await RowsAsync(a, "SELECT id FROM p", timeout.Token)).ShouldHaveSingleItem()[0].ShouldBe(1);
            await ExecAsync(t, "TRUNCATE p CASCADE", timeout.Token);
            (await RowsAsync(a, "SELECT id FROM c ORDER BY id", timeout.Token)).Count.ShouldBe(2);
        }
        finally
        {
            if (transaction.State == TransactionState.Active) { await transaction.RollbackAsync(CancellationToken.None); }
            if (dropping is not null) { try { await dropping; } catch (Exception) { /* Preserve the assertion failure. */ } }
            if (truncating is not null) { try { await truncating; } catch (Exception) { /* Observe the expected conflict on every exit. */ } }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - TRUNCATE: the closure is rechecked at publication (#1716)")]
    public async Task ExecuteAsync_AddedDependentAfterPreparation_ShouldRefusePublication()
    {
        // Arrange: the public catalog can publish independently of SQL's table locks.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("truncate-publication-growth", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await ExecAsync(session, "CREATE TABLE p (id INT PRIMARY KEY)", timeout.Token);
        await ExecAsync(session, "CREATE TABLE c (id INT, CONSTRAINT fk FOREIGN KEY(id) REFERENCES p(id))", timeout.Token);
        await ExecAsync(session, "INSERT INTO p VALUES (1)", timeout.Token);
        await ExecAsync(session, "INSERT INTO c VALUES (1)", timeout.Token);
        int prepared = 0;
        SqlPlanExecutor.TruncatePrepared = () =>
        {
            database.Catalog.CreateTableAsync("dbo", "added", database.Catalog.Tables.Single(table => table.Name == "c").Columns,
                primaryKeyColumns: null, DatabaseObjectOwner.Adhoc, owningSchema: null, timeout.Token,
                constraints: [new SqlCatalogConstraint("added_fk", SqlCatalogConstraintKind.Reference, ["id"], "dbo", "p", ["id"])])
                .GetAwaiter().GetResult();
            prepared++;
        };
        try
        {
            // Act / Assert
            var failure = await Should.ThrowAsync<DatabaseException>(() => ExecAsync(session, "TRUNCATE p CASCADE", timeout.Token));
            var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
            diagnostic.Code.ShouldBe("CS04064");
            diagnostic.Message.ShouldBe(ErrorMessages.TruncateClosureChanged);
            prepared.ShouldBe(1);
        }
        finally { SqlPlanExecutor.TruncatePrepared = null; }
        (await RowsAsync(session, "SELECT * FROM c", timeout.Token)).ShouldHaveSingleItem()[0].ShouldBe(1);
        (await RowsAsync(session, "SELECT * FROM p", timeout.Token)).ShouldHaveSingleItem()[0].ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - FK DDL: an obsolete parent lock cannot validate uncommitted replacement rows (#1716)")]
    public async Task ExecuteAsync_ParentReplacedDuringForeignKeyLockWait_ShouldRefuseBeforeBackfill()
    {
        // Arrange: TRUNCATE owns p before ALTER resolves and waits on its original identity.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var prepared = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var engine = SqlDatabaseEngine.Create("truncate-fk-parent", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var truncator = await database.CreateSessionAsync(timeout.Token);
        await using var alter = await database.CreateSessionAsync(timeout.Token);
        await using var writer = await database.CreateSessionAsync(timeout.Token);
        await ExecAsync(truncator, "CREATE TABLE p (id INT PRIMARY KEY)", timeout.Token);
        await ExecAsync(truncator, "CREATE TABLE d (id INT)", timeout.Token);
        await ExecAsync(truncator, "INSERT INTO d VALUES (10)", timeout.Token);
        await using var uncommitted = await writer.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
        SqlPlanExecutor.TruncatePrepared = () => { prepared.Set(); release.Wait(timeout.Token); };
        var truncating = DedicatedTestTask.Run(() => ExecAsync(truncator, "TRUNCATE p", timeout.Token), timeout.Token);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new LockWaitListener(database.Name);
        listener.Observed = written => { if (written.EventName == "LockWaitStart") { waiting.TrySetResult(); } };
        Task? adding = null;
        int locked = 0;
        try
        {
            prepared.Wait(timeout.Token);
            SqlPlanExecutor.ReferencedTablesLocked = () =>
            {
                // ALTER has the retired owner's X lock; the replacement accepts this writer.
                ExecAsync(writer, "INSERT INTO p VALUES (10)", timeout.Token).GetAwaiter().GetResult();
                locked++;
            };
            adding = ExecAsync(alter, "ALTER TABLE d ADD CONSTRAINT fk FOREIGN KEY(id) REFERENCES p(id)", timeout.Token);
            await waiting.Task.WaitAsync(timeout.Token);

            // Act
            release.Set();
            await truncating;
            var failure = await Should.ThrowAsync<DatabaseException>(async () => await adding);

            // Assert
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS04064");
            locked.ShouldBe(1);
            await uncommitted.RollbackAsync(timeout.Token);
            database.Catalog.Tables.Single(table => table.Name == "d").Constraints.ShouldBeEmpty();
            (await RowsAsync(alter, "SELECT * FROM p", timeout.Token)).ShouldBeEmpty();
            (await RowsAsync(alter, "SELECT * FROM d", timeout.Token)).ShouldHaveSingleItem()[0].ShouldBe(10);
        }
        finally
        {
            SqlPlanExecutor.TruncatePrepared = null;
            SqlPlanExecutor.ReferencedTablesLocked = null;
            release.Set();
            try { await truncating; } catch (Exception) { /* Preserve the primary failure. */ }
            if (adding is not null) { try { await adding; } catch (Exception) { /* Observe the conflict. */ } }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - TRUNCATE: retained joins and joining views read the original owners on every path (#1716)")]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    public async Task ExecuteAsync_RetainedJoin_ShouldReadSnapshot(int transport, int shape)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("truncate-join", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var writer = await database.CreateSessionAsync(timeout.Token);
        await ExecAsync(writer, "CREATE TABLE p (id INT PRIMARY KEY)", timeout.Token);
        await ExecAsync(writer, "CREATE TABLE c (id INT PRIMARY KEY, pid INT REFERENCES p(id))", timeout.Token);
        await ExecAsync(writer, "CREATE TABLE u (id INT PRIMARY KEY)", timeout.Token);
        await ExecAsync(writer, "INSERT INTO p VALUES (1)", timeout.Token);
        await ExecAsync(writer, "INSERT INTO c VALUES (2,1)", timeout.Token);
        await ExecAsync(writer, "INSERT INTO u VALUES (1)", timeout.Token);
        await ExecAsync(writer, "CREATE VIEW v AS SELECT c.id AS cid, p.id AS pid FROM c JOIN p ON c.pid=p.id", timeout.Token);
        string query = shape switch
        {
            0 => "SELECT c.id, p.id FROM c JOIN p ON c.pid=p.id",
            1 => "SELECT cid, pid FROM v",
            _ => "SELECT c.id, u.id FROM c JOIN u ON c.pid=u.id",
        };
        if (transport == 0)
        {
            await using var reader = await database.CreateSessionAsync(timeout.Token);
            await using var snapshot = await reader.BeginTransactionAsync(timeout.Token);
            (await RowsAsync(reader, "SELECT * FROM p", timeout.Token)).ShouldHaveSingleItem()[0].ShouldBe(1);

            // Act / Assert
            await ExecAsync(writer, "TRUNCATE p CASCADE", timeout.Token);
            (await RowsAsync(reader, query, timeout.Token)).ShouldHaveSingleItem().ShouldBe(new object?[] { 2, 1 });
            (await RowsAsync(writer, query, timeout.Token)).ShouldBeEmpty();
            return;
        }
        await using IDuplexConnectionListener connectionListener = transport == 2
            ? TcpConnectionListener.Create(options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0))
            : new InMemoryConnectionListener();
        await using var server = SqlDatabaseServer.Create(engine, new() { Listener = connectionListener });
        await server.StartAsync(timeout.Token);
        await using var client = SqlClient.Create(new SqlClientOptions
        {
            Settings = new DatabaseConnectionSettings { Database = "db", EndPoint = connectionListener.EndPoint },
            ConnectionFactory = transport == 2 ? new TcpConnectionFactory() : ((InMemoryConnectionListener)connectionListener).CreateFactory(),
        });
        await using var connection = await client.ConnectAsync(timeout.Token);
        await connection.ExecuteAsync("BEGIN", cancellationToken: timeout.Token);
        (await connection.QueryAsync("SELECT * FROM p", cancellationToken: timeout.Token)).ShouldHaveSingleItem()[0].ShouldBe(1);

        // Act / Assert
        await ExecAsync(writer, "TRUNCATE p CASCADE", timeout.Token);
        var row = (await connection.QueryAsync(query, cancellationToken: timeout.Token)).ShouldHaveSingleItem();
        new object?[] { row[0], row[1] }.ShouldBe(new object?[] { 2, 1 });
        await connection.ExecuteAsync("ROLLBACK", cancellationToken: timeout.Token);
        (await connection.QueryAsync(query, cancellationToken: timeout.Token)).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - TRUNCATE: a retained indexed point lookup examines one of 20000 rows (#1716)")]
    public async Task ExecuteAsync_RetainedIndex_ShouldSeekOneRecord()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var engine = SqlDatabaseEngine.Create("truncate-index", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var writer = await database.CreateSessionAsync(timeout.Token);
        await using var reader = await database.CreateSessionAsync(timeout.Token);
        await ExecAsync(writer, "CREATE TABLE t (id INT PRIMARY KEY)", timeout.Token);
        for (int batch = 0; batch < 20; batch++)
        {
            await ExecAsync(writer, "INSERT INTO t VALUES " + string.Join(",", Enumerable.Range(batch * 1000, 1000).Select(id => $"({id})")), timeout.Token);
        }
        await using var snapshot = await reader.BeginTransactionAsync(timeout.Token);
        (await RowsAsync(reader, "SELECT id FROM t WHERE id=12345", timeout.Token)).ShouldHaveSingleItem()[0].ShouldBe(12345);
        long before = reader.LastStatementMetrics!.RecordsExamined;

        // Act
        await ExecAsync(writer, "TRUNCATE t", timeout.Token);
        (await RowsAsync(reader, "SELECT id FROM t WHERE id=12345", timeout.Token)).ShouldHaveSingleItem()[0].ShouldBe(12345);
        long seek = reader.LastStatementMetrics!.RecordsExamined;
        string path = reader.LastStatementMetrics.AccessPath;
        (await RowsAsync(reader, "SELECT id FROM t WHERE id+0=12345", timeout.Token)).ShouldHaveSingleItem()[0].ShouldBe(12345);
        long scan = reader.LastStatementMetrics!.RecordsExamined;
        _output.WriteLine($"20000 rows: before={before}; retained seek={seek} ({path}); retained scan={scan}");

        // Assert
        before.ShouldBe(1);
        seek.ShouldBe(1);
        path.ShouldStartWith("seek:");
        scan.ShouldBe(20_000);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - TRUNCATE: fresh binding allocates zero bytes while retirements remain pinned (#1716)")]
    [InlineData(1)]
    [InlineData(100)]
    public async Task CaptureTruncateReadSnapshot_FreshSnapshot_ShouldAllocateNothing(int count)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var engine = SqlDatabaseEngine.Create("truncate-capture-allocation", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var writer = await database.CreateSessionAsync(timeout.Token);
        await using var reader = await database.CreateSessionAsync(timeout.Token);
        await ExecAsync(writer, "CREATE TABLE t (id INT)", timeout.Token);
        await using var pin = await reader.BeginTransactionAsync(timeout.Token);
        for (int i = 0; i < count; i++) { await ExecAsync(writer, "TRUNCATE t", timeout.Token); }
        await using var freshTransaction = await writer.BeginTransactionAsync(timeout.Token);
        var fresh = freshTransaction.Context.Snapshot;
        for (int i = 0; i < 100; i++) { database.Catalog.CaptureTruncateReadSnapshot(fresh).ShouldBeNull(); }

        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) { _ = database.Catalog.CaptureTruncateReadSnapshot(fresh); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        _output.WriteLine($"pending retirements={count}; fresh capture bytes/1000={allocated}; bytes/call={allocated / 1000}");

        // Assert
        database.Catalog.GetPendingTableReclamations().Count.ShouldBe(count);
        allocated.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - TRUNCATE: a cached view image cannot hide a later retained index (#1716)")]
    public async Task ExecuteAsync_IndexAddedAfterViewImage_ShouldRetainNewSeekMetadata()
    {
        // Arrange: CREATE INDEX preserves the table and view description instances.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("truncate-index-image", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var writer = await database.CreateSessionAsync(timeout.Token);
        await using var reader = await database.CreateSessionAsync(timeout.Token);
        await ExecAsync(writer, "CREATE TABLE t (id INT)", timeout.Token);
        await ExecAsync(writer, "INSERT INTO t VALUES " + string.Join(",", Enumerable.Range(0, 64).Select(id => $"({id})")), timeout.Token);
        await ExecAsync(writer, "CREATE VIEW v AS SELECT id FROM t", timeout.Token);
        (await RowsAsync(reader, "SELECT * FROM v", timeout.Token)).Count.ShouldBe(64);
        await ExecAsync(writer, "CREATE INDEX ix ON t(id)", timeout.Token);
        await using var snapshot = await reader.BeginTransactionAsync(timeout.Token);

        // Act
        await ExecAsync(writer, "TRUNCATE t", timeout.Token);
        (await RowsAsync(reader, "SELECT id FROM t WHERE id=42", timeout.Token)).ShouldHaveSingleItem()[0].ShouldBe(42);

        // Assert
        reader.LastStatementMetrics!.RecordsExamined.ShouldBe(1);
        reader.LastStatementMetrics.AccessPath.ShouldBe("seek:ix");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - TRUNCATE: prepared owners stay internal to reclamation (#1716)")]
    public async Task GetPendingTableReclamations_PreparedOwner_ShouldRemainInternal()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("truncate-public-intents", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await ExecAsync(session, "CREATE TABLE t (id INT)", timeout.Token);
        await using var pin = await session.BeginTransactionAsync(timeout.Token);
        await using var truncator = await database.CreateSessionAsync(timeout.Token);
        ulong retired = database.Catalog.Tables.Single().ObjectId;
        int reservations = 0;
        SqlPlanExecutor.TruncateReserved = () =>
        {
            // Act / Assert
            var prepared = database.Catalog.CaptureTableReclamations().ShouldHaveSingleItem();
            database.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
            database.Catalog.CompleteTableReclamationAsync(prepared.ObjectId, timeout.Token).GetAwaiter().GetResult();
            database.Catalog.IsCurrentTableReclamation(prepared.ObjectId, prepared.Intent).ShouldBeTrue();
            reservations++;
        };
        try { await ExecAsync(truncator, "TRUNCATE t", timeout.Token); }
        finally { SqlPlanExecutor.TruncateReserved = null; }
        reservations.ShouldBe(1);
        database.Catalog.GetPendingTableReclamations().ShouldBe(new[] { retired });
    }

    private static async Task ExecAsync(SqlDatabaseSession session, string sql, CancellationToken cancellationToken)
    {
        var result = await session.ExecuteAsync(sql, cancellationToken: cancellationToken);
        result.Status.ShouldBe(DatabaseResultStatus.Success);
    }

    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string sql, CancellationToken cancellationToken)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql, cancellationToken: cancellationToken);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(cancellationToken))
        { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows;
    }
}
