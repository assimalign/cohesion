using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Sql.Client;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(SqlLockWaitCollection.Name)]
public sealed class SqlTruncateTests
{
    private readonly ITestOutputHelper _output;

    public SqlTruncateTests(ITestOutputHelper output) { _output = output; }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - TRUNCATE: maintenance cannot fence retirement before the visibility marker ends")]
    public async Task ExecuteAsync_ReaderAdmittedDuringPublication_ShouldOutliveTheRetirementFence()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var published = new ManualResetEventSlim();
        using var releasePublication = new ManualResetEventSlim();
        using var snapshotTaken = new ManualResetEventSlim();
        using var releaseRead = new ManualResetEventSlim();
        await using var engine = SqlDatabaseEngine.Create("truncate-publication-fence", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var writer = await database.CreateSessionAsync(timeout.Token);
        await using var reader = await database.CreateSessionAsync(timeout.Token);
        await ExecAsync(writer, "CREATE TABLE t (id INT PRIMARY KEY)", timeout.Token);
        await ExecAsync(writer, "INSERT INTO t VALUES (1)", timeout.Token);
        ulong old = Id(database, "t");
        var pages = database.DataStorage.GetOwnerPages(old).ToArray();
        SqlPlanExecutor.TruncatePublished = () => { published.Set(); releasePublication.Wait(timeout.Token); };
        var truncating = DedicatedTestTask.Run(() => ExecAsync(writer, "TRUNCATE t", timeout.Token), timeout.Token);
        Task<List<object?[]>>? reading = null;
        try
        {
            published.Wait(timeout.Token);
            database.TableReclamation.Request();
            database.TableReclamation.RunBatch(timeout.Token);
            SqlQueryExecutor.BeforePlan = request =>
            {
                if (request.Statement.SqlExpression.CommandType != SqlQueryCommandType.Select) { return; }
                snapshotTaken.Set();
                releaseRead.Wait(timeout.Token);
            };
            reading = DedicatedTestTask.Run(() => RowsAsync(reader, "SELECT id FROM t", timeout.Token), timeout.Token);
            snapshotTaken.Wait(timeout.Token);

            // Act: this reader began after the maintenance pass but while the marker was active.
            releasePublication.Set();
            await truncating;
            database.TableReclamation.RunBatch(timeout.Token);

            // Assert
            database.DataStorage.GetOwnerPages(old).ShouldBe(pages);
            database.TableReclamation.ReclaimedPages.ShouldBe(0);
            releaseRead.Set();
            (await reading).ShouldHaveSingleItem()[0].ShouldBe(1);
        }
        finally
        {
            SqlPlanExecutor.TruncatePublished = null;
            SqlQueryExecutor.BeforePlan = null;
            releasePublication.Set();
            releaseRead.Set();
            try { await truncating; } catch (Exception) { /* Observe every started operation without hiding the assertion. */ }
            if (reading is not null) { try { await reading; } catch (Exception) { /* Preserve the primary failure. */ } }
        }
        await ReclaimAsync(database, timeout.Token);
        database.DataStorage.GetOwnerPages(old).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - TRUNCATE: a cached prepared intent cannot supply the retirement fence for its later live owner")]
    public async Task ExecuteAsync_PreparedOwnerRetiredAgain_ShouldAcquireANewIntentFence()
    {
        // Arrange: pause the engine pump while the explicit passes retain one prepared intent.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var workerEntered = new ManualResetEventSlim();
        using var resumeWorker = new ManualResetEventSlim();
        await using var engine = SqlDatabaseEngine.Create("truncate-intent-fence", new() { MaintenanceInterval = TimeSpan.FromMilliseconds(2) });
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        var worker = engine.Workers.OfType<SqlVersionPurgeWorker>().Single();
        worker.BeforeFullPass = _ => { workerEntered.Set(); resumeWorker.Wait(timeout.Token); };
        engine.UndoDeferredSignal.Set();
        try
        {
            workerEntered.Wait(timeout.Token);
            await using var writer = await database.CreateSessionAsync(timeout.Token);
            await using var reader = await database.CreateSessionAsync(timeout.Token);
            await ExecAsync(writer, "CREATE TABLE t (id INT PRIMARY KEY)", timeout.Token);
            int preparedPasses = 0;
            SqlPlanExecutor.TruncatePrepared = () =>
            {
                database.TableReclamation.Request();
                database.TableReclamation.RunBatch(timeout.Token);
                database.TableReclamation.HasWork.ShouldBeTrue();
                preparedPasses++;
            };
            await ExecAsync(writer, "TRUNCATE t", timeout.Token);
            SqlPlanExecutor.TruncatePrepared = null;
            preparedPasses.ShouldBe(1);
            ulong retained = Id(database, "t");
            await ExecAsync(writer, "INSERT INTO t VALUES (2)", timeout.Token);
            await using var snapshot = await reader.BeginTransactionAsync(timeout.Token);

            // Act: that same owner now receives a different intent when the next truncate retires it.
            await ExecAsync(writer, "TRUNCATE t", timeout.Token);
            database.TableReclamation.RunBatch(timeout.Token);

            // Assert
            database.DataStorage.GetOwnerPages(retained).ShouldNotBeEmpty();
            (await RowsAsync(reader, "SELECT id FROM t", timeout.Token)).ShouldHaveSingleItem()[0].ShouldBe(2);
            await snapshot.RollbackAsync(timeout.Token);
            await ReclaimAsync(database, timeout.Token);
            database.DataStorage.GetOwnerPages(retained).ShouldBeEmpty();
        }
        finally
        {
            SqlPlanExecutor.TruncatePrepared = null;
            worker.BeforeFullPass = null;
            resumeWorker.Set();
        }
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - TRUNCATE: stale snapshot DML cannot write the replacement behind its retained read binding")]
    [InlineData("INSERT INTO t VALUES (3)")]
    [InlineData("UPDATE t SET id=3")]
    [InlineData("DELETE FROM t")]
    public async Task ExecuteAsync_StaleSnapshotWriter_ShouldRefuseBeforeAnyMutation(string sql)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("truncate-stale-write", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var writer = await database.CreateSessionAsync(timeout.Token);
        await using var reader = await database.CreateSessionAsync(timeout.Token);
        await ExecAsync(writer, "CREATE TABLE t (id INT PRIMARY KEY)", timeout.Token);
        await ExecAsync(writer, "INSERT INTO t VALUES (1)", timeout.Token);
        await using var snapshot = await reader.BeginTransactionAsync(timeout.Token);
        await ExecAsync(writer, "TRUNCATE t", timeout.Token);
        await ExecAsync(writer, "INSERT INTO t VALUES (2)", timeout.Token);

        // Act
        var failure = await Should.ThrowAsync<DatabaseTransactionAbortedException>(() => ExecAsync(reader, sql, timeout.Token));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS04006");
        (await RowsAsync(reader, "SELECT * FROM t", timeout.Token)).ShouldHaveSingleItem()[0].ShouldBe(1);
        (await RowsAsync(writer, "SELECT * FROM t", timeout.Token)).ShouldHaveSingleItem()[0].ShouldBe(2);
        await snapshot.RollbackAsync(timeout.Token);
        await ExecAsync(reader, sql, timeout.Token);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - TRUNCATE: ReadCommitted DML uses its refreshed statement snapshot")]
    public async Task ExecuteAsync_ReadCommittedWriter_ShouldUseReplacement()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("truncate-fresh-write", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var writer = await database.CreateSessionAsync(timeout.Token);
        await using var reader = await database.CreateSessionAsync(timeout.Token);
        await ExecAsync(writer, "CREATE TABLE t (id INT PRIMARY KEY)", timeout.Token);
        await using var transaction = await reader.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
        await ExecAsync(writer, "TRUNCATE t", timeout.Token);

        // Act
        await ExecAsync(reader, "INSERT INTO t VALUES (3)", timeout.Token);

        // Assert
        (await RowsAsync(reader, "SELECT * FROM t", timeout.Token)).ShouldHaveSingleItem()[0].ShouldBe(3);
        await transaction.CommitAsync(timeout.Token);
        (await RowsAsync(writer, "SELECT * FROM t", timeout.Token)).ShouldHaveSingleItem()[0].ShouldBe(3);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - TRUNCATE: a foreign key published ahead of the table lock invalidates CASCADE's closed set retryably")]
    public async Task ExecuteAsync_ConcurrentForeignKeyPublication_ShouldRefuseStaleClosureRetryably()
    {
        // Arrange: queue CREATE's parent lock before TRUNCATE captures a catalog without c.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("truncate-fk-race", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var creator = await database.CreateSessionAsync(timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await ExecAsync(session, "CREATE TABLE p (id INT PRIMARY KEY)", timeout.Token);
        await ExecAsync(session, "INSERT INTO p VALUES (1)", timeout.Token);
        var holder = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, timeout.Token);
        await database.Coordinator.LockManager.AcquireAsync(holder.Sequence,
            LockResource.Object(Id(database, "p")), LockMode.Exclusive, timeout.Token);
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
        Task? creating = null;
        Task? truncating = null;
        try
        {
            creating = ExecAsync(creator, "CREATE TABLE c (pid INT REFERENCES p(id))", timeout.Token);
            await first.Task.WaitAsync(timeout.Token);
            truncating = ExecAsync(session, "TRUNCATE p CASCADE", timeout.Token);
            await second.Task.WaitAsync(timeout.Token);

            // Act
            await database.Coordinator.RollbackAsync(holder, CancellationToken.None);
            await creating;
            var failure = await Should.ThrowAsync<DatabaseException>(async () => await truncating);

            // Assert
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS04064");
            (await RowsAsync(session, "SELECT * FROM p", timeout.Token)).ShouldHaveSingleItem()[0].ShouldBe(1);
            await ExecAsync(session, "TRUNCATE p CASCADE", timeout.Token);
            (await RowsAsync(session, "SELECT * FROM p", timeout.Token)).ShouldBeEmpty();
        }
        finally
        {
            if (holder.State == TransactionState.Active) { await database.Coordinator.RollbackAsync(holder, CancellationToken.None); }
            if (creating is not null) { try { await creating; } catch (Exception) { /* Observe the started operation on every assertion path. */ } }
            if (truncating is not null) { try { await truncating; } catch (Exception) { /* The expected refusal is observed above. */ } }
        }
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - TRUNCATE: canceled table-lock waits release statement admission and preserve all tables")]
    public async Task ExecuteAsync_CanceledTableLockWait_ShouldReleaseAdmission()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        await using var engine = SqlDatabaseEngine.Create("truncate-lock", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var holder = await database.CreateSessionAsync(timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await ExecAsync(holder, "CREATE TABLE t (id INT PRIMARY KEY)", timeout.Token);
        await using var transaction = await holder.BeginTransactionAsync(timeout.Token);
        await ExecAsync(holder, "INSERT INTO t VALUES (1)", timeout.Token);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new LockWaitListener(database.Name);
        listener.Observed = written => { if (written.EventName == "LockWaitStart") { waiting.TrySetResult(); } };

        // Act
        var truncate = ExecAsync(session, "TRUNCATE t", cancellation.Token);
        await waiting.Task.WaitAsync(timeout.Token);
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await truncate);

        // Assert
        database.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
        await transaction.RollbackAsync(timeout.Token);
        await ExecAsync(session, "TRUNCATE t", timeout.Token);
        await ExecAsync(session, "INSERT INTO t VALUES (2)", timeout.Token);
        await ReclaimAsync(database, timeout.Token);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - TRUNCATE: CASCADE closes cyclic foreign keys across namespaces")]
    public async Task ExecuteAsync_CyclicForeignKeysAcrossNamespaces_ShouldCloseOncePerTable()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("truncate-cycle", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await ExecAsync(session, "CREATE SCHEMA remote", timeout.Token);
        await ExecAsync(session, "CREATE TABLE p (id INT PRIMARY KEY, next INT)", timeout.Token);
        await ExecAsync(session, "CREATE TABLE remote.c (id INT PRIMARY KEY, pid INT REFERENCES dbo.p(id))", timeout.Token);
        await ExecAsync(session, "ALTER TABLE p ADD CONSTRAINT cycle FOREIGN KEY(next) REFERENCES remote.c(id)", timeout.Token);
        await ExecAsync(session, "INSERT INTO p VALUES (1,NULL)", timeout.Token);
        await ExecAsync(session, "INSERT INTO remote.c VALUES (2,1)", timeout.Token);
        await ExecAsync(session, "UPDATE p SET next=2", timeout.Token);

        // Act
        await ExecAsync(session, "TRUNCATE p CASCADE", timeout.Token);

        // Assert
        (await RowsAsync(session, "SELECT * FROM p", timeout.Token)).ShouldBeEmpty();
        (await RowsAsync(session, "SELECT * FROM remote.c", timeout.Token)).ShouldBeEmpty();
        await Should.ThrowAsync<DatabaseException>(() => ExecAsync(session, "INSERT INTO remote.c VALUES (2,1)", timeout.Token));
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - TRUNCATE: schema ownership permits data replacement and keeps the declared definition")]
    public async Task ExecuteAsync_SchemaOwnedTable_ShouldRetainOwnership()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("truncate-owned", new());
        var database = await engine.CreateDatabaseAsync("owned", timeout.Token);
        var schema = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "owned", false, [],
            [new CompiledSchemaTable("t", "Tests.Truncate", [new CompiledSchemaColumn("id", DatabaseType.Int32, IsNullable: true)], null, [], [])], []);
        await database.ApplySchemaAsync(schema, cancellationToken: timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await ExecAsync(session, "INSERT INTO t VALUES (1)", timeout.Token);

        // Act
        await ExecAsync(session, "TRUNCATE t", timeout.Token);

        // Assert
        var table = database.Catalog.Tables.ShouldHaveSingleItem();
        table.Owner.ShouldBe(DatabaseObjectOwner.Schema);
        table.OwningSchema.ShouldBe("owned");
        table.Columns.ShouldHaveSingleItem().Name.ShouldBe("id");
        (await database.VerifySchemaAsync(schema, timeout.Token)).WasAlreadyApplied.ShouldBeTrue();
        (await RowsAsync(session, "SELECT * FROM t", timeout.Token)).ShouldBeEmpty();
        await Should.ThrowAsync<DatabaseObjectLockedException>(() => ExecAsync(session, "DROP TABLE t", timeout.Token));
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - TRUNCATE: declared-script receipt is atomic with publication and prevents replay")]
    public async Task BuildAsync_TruncateScript_ShouldRetainReceiptAcrossReopen()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var strategy = new CrashCaptureSqlStorageStrategy();
        const string sql = "CREATE TABLE t (id INT PRIMARY KEY); INSERT INTO t VALUES (1); TRUNCATE t; INSERT INTO t VALUES (2);";
        var builder = SqlDatabaseEngine.CreateBuilder("truncate-script");
        builder.Options.StorageStrategy = strategy;
        builder.AddDatabase("db", database => database.Script("001", sql));
        await using (var engine = await builder.BuildAsync(timeout.Token))
        {
            var database = await engine.OpenDatabaseAsync("db", timeout.Token);
            await using var session = await database.CreateSessionAsync(timeout.Token);
            (await RowsAsync(session, "SELECT * FROM t", timeout.Token)).ShouldHaveSingleItem()[0].ShouldBe(2);
            database.Catalog.Scripts.ShouldHaveSingleItem().CompletedStatements.ShouldBe(4);
            await ExecAsync(session, "TRUNCATE t", timeout.Token);
            await ExecAsync(session, "INSERT INTO t VALUES (2),(3)", timeout.Token);
        }

        // Act
        var reopen = SqlDatabaseEngine.CreateBuilder("truncate-script");
        reopen.Options.StorageStrategy = strategy.CaptureDurableImages();
        reopen.AddDatabase("db", database => { database.Provisioning = SqlProvisioningMode.Verify; database.Script("001", sql); });
        await using var recovered = await reopen.BuildAsync(timeout.Token);
        var restored = await recovered.OpenDatabaseAsync("db", timeout.Token);
        await using var reader = await restored.CreateSessionAsync(timeout.Token);

        // Assert
        (await RowsAsync(reader, "SELECT id FROM t ORDER BY id", timeout.Token)).Select(row => row[0]).ShouldBe(new object?[] { 2, 3 });
        restored.Catalog.Scripts.ShouldHaveSingleItem().CompletedStatements.ShouldBe(4);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - TRUNCATE: a statement snapshot taken before publication retains its owner even when binding starts after it")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_DelayedBinding_ShouldRetainAutoCommitAndReadCommittedStorage(bool readCommitted)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var engine = SqlDatabaseEngine.Create("truncate-bind", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var writer = await database.CreateSessionAsync(timeout.Token);
        await using var reader = await database.CreateSessionAsync(timeout.Token);
        await ExecAsync(writer, "CREATE TABLE t (id INT PRIMARY KEY)", timeout.Token);
        await ExecAsync(writer, "INSERT INTO t VALUES (1)", timeout.Token);
        await using var transaction = readCommitted ? await reader.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token) : null;
        SqlQueryExecutor.BeforePlan = request =>
        {
            if (request.Statement.SqlExpression.CommandType != SqlQueryCommandType.Select) { return; }
            entered.Set();
            release.Wait(timeout.Token);
        };
        var query = DedicatedTestTask.Run(() => RowsAsync(reader, "SELECT id FROM t", timeout.Token), timeout.Token);
        try
        {
            entered.Wait(timeout.Token);

            // Act
            await ExecAsync(writer, "TRUNCATE t", timeout.Token);
            database.TableReclamation.RunBatch(timeout.Token);
            database.TableReclamation.ReclaimedPages.ShouldBe(0);
        }
        finally { SqlQueryExecutor.BeforePlan = null; release.Set(); }

        // Assert
        (await query).ShouldHaveSingleItem()[0].ShouldBe(1);
        (await RowsAsync(reader, "SELECT * FROM t", timeout.Token)).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - TRUNCATE: canceled snapshot/publication admission releases every acquired resource")]
    public async Task ExecuteAsync_CanceledSnapshotGateWait_ShouldLeaveBeginAndTruncateUsable()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        await using var engine = SqlDatabaseEngine.Create("truncate-gate", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await ExecAsync(session, "CREATE TABLE t (id INT PRIMARY KEY)", timeout.Token);
        await database.Catalog.TruncateSnapshotGate.WaitAsync(timeout.Token);
        try
        {
            // Act / Assert: BEGIN has reached the awaited admission, then cancellation ends it.
            var begin = session.BeginTransactionAsync(cancellation.Token).AsTask();
            begin.IsCompleted.ShouldBeFalse();
            cancellation.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(async () => await begin);
        }
        finally { database.Catalog.TruncateSnapshotGate.Release(); }
        await using (var transaction = await session.BeginTransactionAsync(timeout.Token))
        { await transaction.RollbackAsync(timeout.Token); }

        using var publicationCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        await database.Catalog.TruncateSnapshotGate.WaitAsync(timeout.Token);
        SqlPlanExecutor.TruncatePrepared = () => publicationCancellation.Cancel();
        try
        {
            await Should.ThrowAsync<OperationCanceledException>(() => ExecAsync(session, "TRUNCATE t", publicationCancellation.Token));
        }
        finally
        {
            SqlPlanExecutor.TruncatePrepared = null;
            database.Catalog.TruncateSnapshotGate.Release();
        }
        await ExecAsync(session, "TRUNCATE t", timeout.Token);
        await ExecAsync(session, "INSERT INTO t VALUES (1)", timeout.Token);
        await ReclaimAsync(database, timeout.Token);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - TRUNCATE: late cancellation or observer failure cannot remove replacement trees or retain retired handles")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_PostpublicationFailure_ShouldFinishMandatoryCleanup(bool cancel)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        await using var engine = SqlDatabaseEngine.Create("truncate-published", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await ExecAsync(session, "CREATE TABLE t (id INT PRIMARY KEY)", timeout.Token);
        await ExecAsync(session, "INSERT INTO t VALUES (1)", timeout.Token);
        ulong old = Id(database, "t");
        SqlPlanExecutor.AfterSelfCommit = () =>
        {
            if (cancel) { cancellation.Cancel(); }
            else { throw new InvalidOperationException("injected post-publication failure"); }
        };
        try
        {
            // Act
            if (cancel) { await ExecAsync(session, "TRUNCATE t", cancellation.Token); }
            else { await Should.ThrowAsync<Exception>(() => ExecAsync(session, "TRUNCATE t", cancellation.Token)); }
        }
        finally { SqlPlanExecutor.AfterSelfCommit = null; }

        // Assert
        Id(database, "t").ShouldNotBe(old);
        database.IndexManager.ExportRegistrations().ShouldAllBe(registration => registration.ObjectId != old);
        await ExecAsync(session, "INSERT INTO t VALUES (2)", timeout.Token);
        (await RowsAsync(session, "SELECT * FROM t WHERE id=2", timeout.Token)).ShouldHaveSingleItem()[0].ShouldBe(2);
        await ReclaimAsync(database, timeout.Token);
        database.DataStorage.GetOwnerPages(old).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - TRUNCATE: a wire snapshot blocks an explicit reclamation pass until its transaction ends")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_WireSnapshot_ShouldFenceReclamation(bool tcp)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("truncate-wire", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using IDuplexConnectionListener listener = tcp
            ? TcpConnectionListener.Create(options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0))
            : new InMemoryConnectionListener();
        await using var server = SqlDatabaseServer.Create(engine, new() { Listener = listener });
        await server.StartAsync(timeout.Token);
        await using var client = SqlClient.Create(new SqlClientOptions
        {
            Settings = new DatabaseConnectionSettings { Database = "db", EndPoint = listener.EndPoint },
            ConnectionFactory = tcp ? new TcpConnectionFactory() : ((InMemoryConnectionListener)listener).CreateFactory(),
        });
        await using var writer = await client.ConnectAsync(timeout.Token);
        await writer.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY)", cancellationToken: timeout.Token);
        await writer.ExecuteAsync("INSERT INTO t VALUES (1),(2)", cancellationToken: timeout.Token);
        ulong old = Id(database, "t");
        var pages = database.DataStorage.GetOwnerPages(old).ToArray();
        await using var reader = await client.ConnectAsync(timeout.Token);
        await reader.ExecuteAsync("BEGIN", cancellationToken: timeout.Token);

        // Act: read the pinned snapshot only AFTER the truncate and a completed maintenance pass.
        await writer.ExecuteAsync("TRUNCATE t", cancellationToken: timeout.Token);
        database.TableReclamation.RunBatch(timeout.Token);

        // Assert
        database.TableReclamation.ReclaimedPages.ShouldBe(0);
        database.DataStorage.GetOwnerPages(old).ShouldBe(pages);
        (await reader.QueryAsync("SELECT id FROM t ORDER BY id", cancellationToken: timeout.Token))
            .Select(row => row[0]).ShouldBe(new object?[] { 1, 2 });
        (await writer.QueryAsync("SELECT * FROM t", cancellationToken: timeout.Token)).ShouldBeEmpty();
        await reader.ExecuteAsync("COMMIT", cancellationToken: timeout.Token);
        await ReclaimAsync(database, timeout.Token);
        database.DataStorage.GetOwnerPages(old).ShouldBeEmpty();
        database.TableReclamation.ReclaimedPages.ShouldBeGreaterThan(0);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - TRUNCATE: every crash image publishes the complete set or keeps the original set")]
    [InlineData(StorageCommitDurability.Synchronous)]
    [InlineData(StorageCommitDurability.Grouped)]
    [InlineData(StorageCommitDurability.None)]
    public async Task Reopen_InterruptedTruncate_ShouldRecoverAtomicTablesIdentityViewsAndReclamation(StorageCommitDurability durability)
    {
        // Arrange: None models process loss, durable modes model power loss.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var strategy = new CrashCaptureSqlStorageStrategy(durability);
        CrashCaptureSqlStorageStrategy.CrashPointRecorder recorder;
        await using (var engine = SqlDatabaseEngine.Create("truncate-crash", new() { StorageStrategy = strategy, Durability = durability }))
        {
            var database = await engine.CreateDatabaseAsync("db", timeout.Token);
            await using var session = await database.CreateSessionAsync(timeout.Token);
            await ExecAsync(session, "CREATE TABLE p (id BIGINT GENERATED BY DEFAULT AS IDENTITY (START 10 INCREMENT 3) PRIMARY KEY)", timeout.Token);
            await ExecAsync(session, "CREATE TABLE c (id INT PRIMARY KEY, pid BIGINT REFERENCES p(id))", timeout.Token);
            await ExecAsync(session, "CREATE VIEW v AS SELECT * FROM p", timeout.Token);
            await ExecAsync(session, "INSERT INTO p DEFAULT VALUES", timeout.Token);
            await ExecAsync(session, "INSERT INTO c VALUES (1,10)", timeout.Token);
            await using var pin = await session.BeginTransactionAsync(timeout.Token);
            await using var truncate = await database.CreateSessionAsync(timeout.Token);

            // Act: retain each write/flush/length boundary through preparation and publication.
            using (recorder = strategy.RecordCrashPoints())
            {
                await ExecAsync(truncate, "TRUNCATE p RESTART IDENTITY CASCADE", timeout.Token);
            }
        }

        // Assert: no image can publish only part of the list, retain broken view IDs, or
        // leave prepared owners after maintenance. Opening uses the real WAL recovery.
        recorder.Count.ShouldBeGreaterThan(1);
        for (int point = 0; point < recorder.Count; point++)
        {
            await using var recovered = SqlDatabaseEngine.Create("truncate-recover", new() { StorageStrategy = recorder.Open(point), Durability = durability });
            var database = await recovered.OpenDatabaseAsync("db", timeout.Token);
            await using var session = await database.CreateSessionAsync(timeout.Token);
            var parent = await RowsAsync(session, "SELECT * FROM p", timeout.Token);
            var child = await RowsAsync(session, "SELECT * FROM c", timeout.Token);
            child.Count.ShouldBe(parent.Count, $"crash image {point}");
            parent.Count.ShouldBeInRange(0, 1);
            (await RowsAsync(session, "SELECT * FROM v", timeout.Token)).Count.ShouldBe(parent.Count);
            await ExecAsync(session, "INSERT INTO p DEFAULT VALUES", timeout.Token);
            var ids = (await RowsAsync(session, "SELECT id FROM p ORDER BY id", timeout.Token)).Select(row => (long)row[0]!).ToArray();
            if (parent.Count == 0) { ids.ShouldBe(new[] { 10L }); }
            else { ids[0].ShouldBe(10L); ids[1].ShouldBeGreaterThan(10L); }
            await ReclaimAsync(database, timeout.Token);
            database.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
        }
        _output.WriteLine($"{durability}: recovered {recorder.Count} TRUNCATE crash images");
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - TRUNCATE: identity continuity and restart retain every index and definition")]
    [InlineData(10L, 3L)]
    [InlineData(-10L, -3L)]
    public async Task ExecuteAsync_IdentityPolicies_ShouldPreserveDefinitionsAndResetOnlyWhenRequested(long start, long increment)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("truncate", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await ExecAsync(session, $"CREATE TABLE t (id BIGINT GENERATED BY DEFAULT AS IDENTITY (START {start} INCREMENT {increment}) PRIMARY KEY, n INT DEFAULT 7 CHECK (n > 0))", timeout.Token);
        await ExecAsync(session, "CREATE UNIQUE INDEX ix ON t (n)", timeout.Token);
        await ExecAsync(session, "INSERT INTO t DEFAULT VALUES", timeout.Token);
        ulong old = Id(database, "t");

        // Act / Assert: omitted policy is CONTINUE, including the allocator's unused block.
        (await session.ExecuteAsync("TRUNCATE TABLE ONLY t, t", cancellationToken: timeout.Token)).AffectedCount.ShouldBe(0);
        Id(database, "t").ShouldNotBe(old);
        await ExecAsync(session, "INSERT INTO t DEFAULT VALUES", timeout.Token);
        (await RowsAsync(session, "SELECT id, n FROM t", timeout.Token)).ShouldHaveSingleItem()
            .ShouldBe(new object?[] { start + increment, 7 });
        await Should.ThrowAsync<DatabaseException>(() => ExecAsync(session, "INSERT INTO t (n) VALUES (7)", timeout.Token));
        await ExecAsync(session, "TRUNCATE t RESTART IDENTITY RESTRICT", timeout.Token);
        await ExecAsync(session, "INSERT INTO t DEFAULT VALUES", timeout.Token);
        (await RowsAsync(session, "SELECT id, n FROM t WHERE n=7", timeout.Token)).ShouldHaveSingleItem()
            .ShouldBe(new object?[] { start, 7 });
        database.Catalog.GetIndexes(Id(database, "t")).Count.ShouldBe(2);
        await Should.ThrowAsync<DatabaseException>(() => ExecAsync(session, "INSERT INTO t (n) VALUES (-1)", timeout.Token));
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - TRUNCATE: RESTRICT requires the complete recursive FK set")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_ForeignKeyClosure_ShouldTruncateAtomically(bool cascade)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("truncate", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await ExecAsync(session, "CREATE TABLE p (id INT PRIMARY KEY, self INT REFERENCES p(id))", timeout.Token);
        await ExecAsync(session, "CREATE TABLE c (id INT PRIMARY KEY, pid INT REFERENCES p(id))", timeout.Token);
        await ExecAsync(session, "CREATE TABLE g (id INT PRIMARY KEY, cid INT REFERENCES c(id))", timeout.Token);
        await ExecAsync(session, "INSERT INTO p VALUES (1,1)", timeout.Token);
        await ExecAsync(session, "INSERT INTO c VALUES (2,1)", timeout.Token);
        await ExecAsync(session, "INSERT INTO g VALUES (3,2)", timeout.Token);
        var old = database.Catalog.Tables.ToDictionary(table => table.Name, table => table.ObjectId);

        // Act / Assert
        var failure = await Should.ThrowAsync<DatabaseException>(() => ExecAsync(session, "TRUNCATE p, c RESTRICT", timeout.Token));
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03067");
        database.Catalog.Tables.ShouldAllBe(table => table.ObjectId == old[table.Name]);
        await Should.ThrowAsync<DatabaseException>(() => ExecAsync(session, "TRUNCATE p, missing CASCADE", timeout.Token));
        await ExecAsync(session, cascade ? "TRUNCATE p CASCADE" : "TRUNCATE g, c, p RESTRICT", timeout.Token);
        foreach (string table in new[] { "p", "c", "g" })
        {
            (await RowsAsync(session, $"SELECT * FROM {table}", timeout.Token)).ShouldBeEmpty();
            Id(database, table).ShouldNotBe(old[table]);
        }
        await Should.ThrowAsync<DatabaseException>(() => ExecAsync(session, "INSERT INTO c VALUES (2,1)", timeout.Token));
        await ExecAsync(session, "INSERT INTO p VALUES (1,1)", timeout.Token);
        await ExecAsync(session, "INSERT INTO c VALUES (2,1)", timeout.Token);
        await ExecAsync(session, "INSERT INTO g VALUES (3,2)", timeout.Token);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - TRUNCATE: a pinned snapshot reads its rows after commit and prevents any reclamation")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_PinnedSnapshot_ShouldRetainRowsViewsAndStorageUntilItEnds(bool disposeReader)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("truncate", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var writer = await database.CreateSessionAsync(timeout.Token);
        await using var reader = await database.CreateSessionAsync(timeout.Token);
        await ExecAsync(writer, "CREATE TABLE t (id INT PRIMARY KEY, n INT)", timeout.Token);
        await ExecAsync(writer, "INSERT INTO t VALUES (1,10),(2,20)", timeout.Token);
        await ExecAsync(writer, "CREATE VIEW v AS SELECT id, n FROM t", timeout.Token);
        await ExecAsync(writer, "CREATE VIEW vv AS SELECT * FROM v", timeout.Token);
        await using var snapshot = await reader.BeginTransactionAsync(timeout.Token);
        var before = await RowsAsync(reader, "SELECT * FROM vv ORDER BY id", timeout.Token);
        ulong old = Id(database, "t");
        var pages = database.DataStorage.GetOwnerPages(old).ToArray();

        // Act
        await ExecAsync(writer, "TRUNCATE t", timeout.Token);
        await ExecAsync(writer, "INSERT INTO t VALUES (3,30)", timeout.Token);
        database.TableReclamation.RunBatch(timeout.Token);

        // Assert: both the row scan and the old indexed plan read after the publication.
        (await RowsAsync(reader, "SELECT * FROM vv ORDER BY id", timeout.Token)).Select(row => string.Join(",", row))
            .ShouldBe(before.Select(row => string.Join(",", row)));
        (await RowsAsync(reader, "SELECT n FROM t WHERE id=1", timeout.Token)).ShouldHaveSingleItem()[0].ShouldBe(10);
        database.TableReclamation.ReclaimedPages.ShouldBe(0);
        database.DataStorage.GetOwnerPages(old).ShouldBe(pages);
        (await RowsAsync(writer, "SELECT * FROM vv", timeout.Token)).ShouldHaveSingleItem().ShouldBe(new object?[] { 3, 30 });
        if (disposeReader) { await reader.DisposeAsync(); }
        else { await snapshot.RollbackAsync(timeout.Token); }
        await ReclaimAsync(database, timeout.Token);
        database.DataStorage.GetOwnerPages(old).ShouldBeEmpty();
        database.TableReclamation.ReclaimedPages.ShouldBeGreaterThan(0);
        (await RowsAsync(disposeReader ? writer : reader, "SELECT * FROM vv", timeout.Token)).ShouldHaveSingleItem().ShouldBe(new object?[] { 3, 30 });
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - TRUNCATE: pre-publication failures release locks and reclaim prepared storage")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ExecuteAsync_PrepublicationFailure_ShouldKeepOriginalAndReleaseEverything(int point)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        await using var engine = SqlDatabaseEngine.Create("truncate", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await ExecAsync(session, "CREATE TABLE t (id INT PRIMARY KEY)", timeout.Token);
        await ExecAsync(session, "INSERT INTO t VALUES (1)", timeout.Token);
        ulong old = Id(database, "t");
        Action fail = () => { if (point == 2) { cancellation.Cancel(); } else { throw new InvalidOperationException("injected TRUNCATE failure"); } };
        if (point == 0) { SqlPlanExecutor.TruncateReserved = fail; }
        else { SqlPlanExecutor.TruncatePrepared = fail; }
        try
        {
            // Act
            await Should.ThrowAsync<Exception>(() => ExecAsync(session, "TRUNCATE t", cancellation.Token));
        }
        finally { SqlPlanExecutor.TruncateReserved = null; SqlPlanExecutor.TruncatePrepared = null; }

        // Assert
        Id(database, "t").ShouldBe(old);
        (await RowsAsync(session, "SELECT * FROM t", timeout.Token)).ShouldHaveSingleItem()[0].ShouldBe(1);
        await ExecAsync(session, "INSERT INTO t VALUES (2)", timeout.Token);
        await ReclaimAsync(database, timeout.Token);
        database.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
        database.IndexManager.ExportRegistrations().ShouldAllBe(registration => registration.ObjectId == old);
        await ExecAsync(session, "TRUNCATE t", timeout.Token);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - TRUNCATE: explicit transaction refusal leaves its prior writes usable")]
    public async Task ExecuteAsync_ExplicitTransaction_ShouldRefuseWithoutChangingItsState()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("truncate", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await ExecAsync(session, "CREATE TABLE t (id INT)", timeout.Token);
        await using var transaction = await session.BeginTransactionAsync(timeout.Token);
        await ExecAsync(session, "INSERT INTO t VALUES (1)", timeout.Token);

        // Act / Assert
        var result = await session.ExecuteAsync("TRUNCATE t", cancellationToken: timeout.Token);
        result.Status.ShouldBe(DatabaseResultStatus.Error);
        result.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS04066");
        (await RowsAsync(session, "SELECT * FROM t", timeout.Token)).ShouldHaveSingleItem()[0].ShouldBe(1);
        await transaction.RollbackAsync(timeout.Token);
        await ExecAsync(session, "TRUNCATE t", timeout.Token);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - TRUNCATE: concurrent replacement history detects lost and resurrected writes")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ExecuteAsync_ConcurrentReplacementAndMaintenance_ShouldKeepPublishedOwnersAlive(int corruption)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var engine = SqlDatabaseEngine.Create("truncate-stress", new() { MaintenanceInterval = TimeSpan.FromMilliseconds(2) });
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await ExecAsync(session, "CREATE TABLE t (id INT PRIMARY KEY)", timeout.Token);
        int preparedPasses = 0;
        int truncations = 0;
        int inserts = 0;
        int written = 0;
        int snapshots = 0;
        // Commit/BEGIN history is independent of observed rows. Take the history gate
        // after INSERT's table intent or TRUNCATE's X locks, never before a table wait.
        // This orders publication, commit and snapshot capture without serializing reads
        // or preparation, or creating a gate -> table-lock cycle.
        using var historyGate = new SemaphoreSlim(1, 1);
        var writes = new List<(long Commit, int Id)>();
        var truncates = new List<long>();
        var reads = new List<(long Snapshot, int[] Rows)>();
        long commitSequence = 0;
        bool truncateOwnsHistory = false;
        SqlPlanExecutor.TruncateReserved = () =>
        {
            historyGate.Wait(timeout.Token);
            truncateOwnsHistory = true;
        };
        SqlPlanExecutor.TruncatePublished = () =>
        {
            truncates.Add(++commitSequence);
            truncateOwnsHistory = false;
            historyGate.Release();
        };
        SqlPlanExecutor.TruncatePrepared = () =>
        {
            database.TableReclamation.Request();
            database.TableReclamation.RunBatch(timeout.Token);
            Interlocked.Increment(ref preparedPasses);
        };
        var elapsed = Stopwatch.StartNew();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var writer = DedicatedTestTask.Run(async () =>
        {
            await using var concurrent = await database.CreateSessionAsync(timeout.Token);
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await using var transaction = await concurrent.BeginTransactionAsync(IsolationLevel.ReadCommitted, stop.Token);
                    int id = Interlocked.Increment(ref inserts);
                    await ExecAsync(concurrent, $"INSERT INTO t VALUES ({id})", stop.Token);
                    await historyGate.WaitAsync(stop.Token);
                    try
                    {
                        await transaction.CommitAsync(stop.Token);
                        writes.Add((++commitSequence, id));
                        Interlocked.Increment(ref written);
                    }
                    finally { historyGate.Release(); }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (DatabaseException error) when (error.Diagnostics.Any(diagnostic => diagnostic.Code is "CS04064" or "CS04006")) { }
            }
        }, timeout.Token);
        var reader = DedicatedTestTask.Run(async () =>
        {
            await using var concurrent = await database.CreateSessionAsync(timeout.Token);
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    SqlDatabaseTransaction snapshot;
                    long captured;
                    await historyGate.WaitAsync(stop.Token);
                    try
                    {
                        snapshot = await concurrent.BeginTransactionAsync(stop.Token);
                        captured = commitSequence;
                    }
                    finally { historyGate.Release(); }
                    await using var pinned = snapshot;
                    var before = (await RowsAsync(concurrent, "SELECT id FROM t ORDER BY id", stop.Token)).Select(row => (int)row[0]!).ToArray();
                    reads.Add((captured, before));
                    await Task.Yield();
                    var after = (await RowsAsync(concurrent, "SELECT id FROM t ORDER BY id", stop.Token)).Select(row => (int)row[0]!).ToArray();
                    reads.Add((captured, after));
                    after.ShouldBe(before);
                    Interlocked.Increment(ref snapshots);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            }
        }, timeout.Token);
        try
        {
            // Act: a minimum duration plus observed counters determines completion.
            do
            {
                await ExecAsync(session, "TRUNCATE t", timeout.Token);
                truncations++;
            } while (elapsed.Elapsed < TimeSpan.FromSeconds(1) || truncations < 100 || Volatile.Read(ref written) < 100 ||
                Volatile.Read(ref snapshots) < 100 || Volatile.Read(ref preparedPasses) < 100);
        }
        finally
        {
            SqlPlanExecutor.TruncatePrepared = null;
            SqlPlanExecutor.TruncateReserved = null;
            SqlPlanExecutor.TruncatePublished = null;
            if (truncateOwnsHistory) { historyGate.Release(); }
            stop.Cancel();
            await Task.WhenAll(writer, reader);
        }

        // Assert
        preparedPasses.ShouldBeGreaterThanOrEqualTo(100);
        inserts.ShouldBeGreaterThanOrEqualTo(100);
        written.ShouldBeGreaterThanOrEqualTo(100);
        snapshots.ShouldBeGreaterThanOrEqualTo(100);
        truncations.ShouldBeGreaterThanOrEqualTo(100);
        writes.Count.ShouldBe(written);
        truncates.Count.ShouldBe(truncations);
        reads.Count.ShouldBeGreaterThanOrEqualTo(2 * snapshots);
        foreach (var read in reads)
        {
            read.Rows.ShouldBe(ExpectedRows(read.Snapshot), $"snapshot at commit {read.Snapshot}");
        }
        // Ensure the final suffix contains a committed write even if the last racing
        // operation was TRUNCATE. Fault modes prove that the oracle catches a missing
        // write and an equal-count substitution of a pre-TRUNCATE write.
        int finalId = Interlocked.Increment(ref inserts);
        await ExecAsync(session, $"INSERT INTO t VALUES ({finalId})", timeout.Token);
        writes.Add((++commitSequence, finalId));
        Interlocked.Increment(ref written);
        if (corruption != 0)
        {
            await ExecAsync(session, $"DELETE FROM t WHERE id={finalId}", timeout.Token);
            if (corruption == 2)
            {
                int resurrected = writes.First(write => write.Commit < truncates[^1]).Id;
                await ExecAsync(session, $"INSERT INTO t VALUES ({resurrected})", timeout.Token);
            }
        }
        var finalRows = (await RowsAsync(session, "SELECT id FROM t ORDER BY id", timeout.Token)).Select(row => (int)row[0]!).ToArray();
        var expectedFinal = ExpectedRows(commitSequence);
        void VerifyFinalRows() => finalRows.ShouldBe(expectedFinal, "exact writes committed after the last TRUNCATE");
        if (corruption == 0) { VerifyFinalRows(); }
        else { Should.Throw<ShouldAssertException>(VerifyFinalRows); }
        if (corruption == 2) { finalRows.Length.ShouldBe(expectedFinal.Length); }
        await ReclaimAsync(database, timeout.Token);
        await ExecAsync(session, "TRUNCATE t", timeout.Token);
        await ExecAsync(session, "INSERT INTO t VALUES (-1)", timeout.Token);
        (await RowsAsync(session, "SELECT * FROM t WHERE id=-1", timeout.Token)).ShouldHaveSingleItem()[0].ShouldBe(-1);
        _output.WriteLine($"truncations={truncations}; writer attempts={inserts}; committed writes={written}; stable snapshots={snapshots}; prepared-owner maintenance passes={preparedPasses}");

        int[] ExpectedRows(long snapshot)
        {
            long lastTruncate = truncates.LastOrDefault(sequence => sequence <= snapshot);
            return writes.Where(write => write.Commit > lastTruncate && write.Commit <= snapshot)
                .Select(write => write.Id).Order().ToArray();
        }
    }

    private static ulong Id(SqlDatabase database, string name) => database.Catalog.Tables.Single(table => table.Name == name).ObjectId;

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
        {
            rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray());
        }
        return rows;
    }

    private static async Task ReclaimAsync(SqlDatabase database, CancellationToken cancellationToken)
    {
        do
        {
            database.TableReclamation.RunBatch(cancellationToken);
            if (database.TableReclamation.HasWork) { await Task.Delay(1, cancellationToken); }
        } while (database.TableReclamation.HasWork);
    }
}
