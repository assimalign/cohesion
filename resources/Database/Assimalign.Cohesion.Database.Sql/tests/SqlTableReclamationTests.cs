using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Sql.Client;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(SqlLockWaitCollection.Name)]
public sealed class SqlTableReclamationTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Sql] - DROP unpublishes immediately, then reclaims bounded batches and reuses rows and indexes")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DropTable_PopulatedTable_ShouldReclaimAndReuseAllPages(bool fileBacked)
    {
        // Arrange
        string path = Path.Combine(Environment.CurrentDirectory, "_out", "1443", Guid.NewGuid().ToString("N"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var release = new ManualResetEventSlim();
        try
        {
            var options = new SqlDatabaseEngineOptions();
            if (fileBacked) { options.RootPath = path; }
            await using var engine = SqlDatabaseEngine.Create("reclaim", options);
            var database = await engine.CreateDatabaseAsync("db", timeout.Token);
            await using var session = await database.CreateSessionAsync(timeout.Token);
            await SeedAsync(session, "t", 300, timeout.Token);
            await session.ExecuteAsync("CREATE TABLE other (id INT)", cancellationToken: timeout.Token);
            ulong id = TableId(database, "t");
            int rowsPages = database.DataStorage.GetOwnerPages(id).Count;
            rowsPages.ShouldBeGreaterThan(SqlTableReclamation.BatchSize);
            long pageCount = database.DataStorage.PageManager.PageCount;
            long freeBefore = database.DataStorage.FreeSpaceMap.FreePageCount;
            int batches = 0;
            database.TableReclamation.BeforeBatch = (_, token) => release.Wait(token);
            database.TableReclamation.AfterBatch = (_, count) =>
            {
                count.ShouldBeInRange(0, SqlTableReclamation.BatchSize);
                Interlocked.Increment(ref batches);
            };

            // Act: the worker is paused; the drop and another table's writes still finish.
            await session.ExecuteAsync("DROP TABLE t", cancellationToken: timeout.Token);
            database.Catalog.TryGetTable("dbo", "t", out _).ShouldBeFalse();
            database.TableReclamation.ReclaimedPages.ShouldBe(0);
            database.DataStorage.GetOwnerPages(id).Count.ShouldBe(rowsPages);
            database.CatalogStorage.GetOwnerPages(id).Count.ShouldBe(1);
            await session.ExecuteAsync("INSERT INTO other VALUES (7)", cancellationToken: timeout.Token);
            await session.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, payload VARCHAR(2000))", cancellationToken: timeout.Token);
            TableId(database, "t").ShouldNotBe(id);
            release.Set();
            await WaitDoneAsync(database, timeout.Token);

            // Assert: index pages as well as rows return to the FSM, with the replacement intact.
            database.TableReclamation.ReclaimedPages.ShouldBeGreaterThan(rowsPages);
            database.DataStorage.GetOwnerPages(id).ShouldBeEmpty();
            database.CatalogStorage.GetOwnerPages(id).ShouldBeEmpty();
            database.DataStorage.FreeSpaceMap.FreePageCount.ShouldBeGreaterThan(freeBefore);
            batches.ShouldBeGreaterThan(1);
            await FillAsync(session, "t", 300, timeout.Token);
            database.DataStorage.PageManager.PageCount.ShouldBeLessThanOrEqualTo(pageCount + 2);
            (await CountAsync(session, "t", timeout.Token)).ShouldBe(300);
            (await CountAsync(session, "other", timeout.Token)).ShouldBe(1);
            database.Provisioning.ShouldBeNull();
        }
        finally
        {
            release.Set();
            if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Durable DROP crash images resume before and during reclamation")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Reopen_PartialReclamation_ShouldConvergeAndPreserveReplacement(bool midBatch, bool wire)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var strategy = new CrashCaptureSqlStorageStrategy();
        CrashCaptureSqlStorageStrategy? image = null;
        using var captured = new ManualResetEventSlim();
        using var pause = new ManualResetEventSlim();
        ulong oldId;
        long pages;
        await using (var engine = await Builder(strategy).BuildAsync(timeout.Token))
        {
            var database = await engine.CreateDatabaseAsync("db", timeout.Token);
            await using var session = await database.CreateSessionAsync(timeout.Token);
            await SeedAsync(session, "t", 300, timeout.Token);
            oldId = TableId(database, "t");
            pages = database.DataStorage.PageManager.PageCount;
            int batches = 0;
            database.TableReclamation.BeforeBatch = (_, token) =>
            {
                if (!midBatch || Volatile.Read(ref batches) > 0)
                {
                    image = strategy.CaptureDurableImages();
                    captured.Set();
                    pause.Wait(token);
                }
            };
            database.TableReclamation.AfterBatch = (_, _) => Interlocked.Increment(ref batches);

            // Act: retain a real durable image, then cancel the blocked worker by closing.
            if (wire)
            {
                await ExecuteWireAsync(engine, "DROP TABLE t", timeout.Token);
            }
            else { await session.ExecuteAsync("DROP TABLE t", cancellationToken: timeout.Token); }
            captured.Wait(timeout.Token);
            if (midBatch) { database.TableReclamation.ReclaimedPages.ShouldBe(SqlTableReclamation.BatchSize); }
            else { database.TableReclamation.ReclaimedPages.ShouldBe(0); }
        }

        // Assert: recovery replays committed frees exactly once and preserves the retired id.
        using var resume = new ManualResetEventSlim();
        var recovering = Builder(image.ShouldNotBeNull());
        recovering.AddWorker(engine =>
        {
            engine.BeforeProvisioning = database => database.TableReclamation.BeforeBatch = (_, token) => resume.Wait(token);
            return new RecordingWorker(engine);
        });
        await using var reopened = await recovering.BuildAsync(timeout.Token);
        var recovered = await reopened.OpenDatabaseAsync("db", timeout.Token);
        await using var replacement = await recovered.CreateSessionAsync(timeout.Token);
        recovered.Catalog.TryGetTable("dbo", "t", out _).ShouldBeFalse();
        await replacement.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, payload VARCHAR(2000))", cancellationToken: timeout.Token);
        TableId(recovered, "t").ShouldBeGreaterThan(oldId);
        recovered.TableReclamation.ReclaimedPages.ShouldBe(0);
        resume.Set();
        await WaitDoneAsync(recovered, timeout.Token);
        recovered.DataStorage.GetOwnerPages(oldId).ShouldBeEmpty();
        recovered.CatalogStorage.GetOwnerPages(oldId).ShouldBeEmpty();
        long free = recovered.DataStorage.FreeSpaceMap.FreePageCount;
        free.ShouldBe(recovered.DataStorage.PageManager.PageCount - 3); // two header slots and replacement's empty primary-key root
        recovered.TableReclamation.RunBatch(timeout.Token);
        recovered.DataStorage.FreeSpaceMap.FreePageCount.ShouldBe(free);
        await FillAsync(replacement, "t", 300, timeout.Token);
        recovered.DataStorage.PageManager.PageCount.ShouldBeLessThanOrEqualTo(pages + 1);
        (await CountAsync(replacement, "t", timeout.Token)).ShouldBe(300);
        if (wire) { await ExecuteWireAsync(reopened, "SELECT * FROM t", timeout.Token); }
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - DROP coexists with deferred provisioning and waits for the same table")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DropTable_PendingProvisioning_ShouldRespectTableReservation(bool sameTable)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        await using var engine = SqlDatabaseEngine.Create("provision-and-reclaim", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        SqlSchema Definition(bool index) => SqlSchema.Create("db", schema => schema.Table<Item>("items", table =>
        {
            table.Column(row => row.Id);
            table.Column(row => row.Payload);
            if (index) { table.Index(row => row.Id); }
        }));
        await database.ApplySchemaAsync(Definition(false).Compile(), timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await FillAsync(session, "items", 100, timeout.Token);
        await SeedAsync(session, "retired", 100, timeout.Token);
        await session.ExecuteAsync("CREATE TABLE other (id INT)", cancellationToken: timeout.Token);
        database.SchemaProvisioner.BeforeDeferredStep = (_, token) => { entered.Set(); release.Wait(token); };
        await database.SchemaProvisioner.ApplyAsync(Definition(true).Compile(), timeout.Token, deferDataScalingSteps: true);
        entered.Wait(timeout.Token);
        await using var dropper = sameTable ? database.CreateSchemaSession("db", timeout.Token) : await database.CreateSessionAsync(timeout.Token);

        // Act: the reserved table waits in the lock manager; an unrelated drop unpublishes immediately.
        Task<QueryResult> dropping = dropper.ExecuteAsync(sameTable ? "DROP TABLE items" : "DROP TABLE retired", cancellationToken: timeout.Token).AsTask();
        int writes = 0;
        var elapsed = Stopwatch.StartNew();
        try
        {
            if (!sameTable) { await dropping; }
            do
            {
                await session.ExecuteAsync("INSERT INTO other VALUES (1)", cancellationToken: timeout.Token);
                if (sameTable) { dropping.IsCompleted.ShouldBeFalse(); }
                database.TableReclamation.RunBatch(timeout.Token);
                writes++;
                timeout.Token.ThrowIfCancellationRequested();
            } while (elapsed.Elapsed < TimeSpan.FromMilliseconds(100) || writes < 10);
            database.TableReclamation.ReclaimedPages.ShouldBe(0);
        }
        finally { release.Set(); }
        await dropping;
        await WaitDoneAsync(database, timeout.Token);

        // Assert
        writes.ShouldBeGreaterThanOrEqualTo(10);
        database.Catalog.TryGetTable("dbo", sameTable ? "items" : "retired", out _).ShouldBeFalse();
        database.TableReclamation.ReclaimedPages.ShouldBeGreaterThan(0);
        (await CountAsync(session, "other", timeout.Token)).ShouldBe(writes);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Build drops typed and scripted tables without reclaiming any pages")]
    [InlineData(false, 0)]
    [InlineData(false, 300)]
    [InlineData(true, 0)]
    [InlineData(true, 300)]
    public async Task BuildAsync_DeclaredDrop_ShouldDoNoPageReclamation(bool scripted, int rows)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var strategy = new CrashCaptureSqlStorageStrategy();
        var original = SqlSchema.Create("db", schema => schema.Table<Item>("t", table =>
        {
            table.Column(row => row.Id);
            table.Column(row => row.Payload);
        }));
        await using (var engine = await Builder(strategy).BuildAsync(timeout.Token))
        {
            var database = await engine.CreateDatabaseAsync("db", timeout.Token);
            if (scripted)
            {
                await using var schemaSession = database.CreateSchemaSession("db", timeout.Token);
                await schemaSession.ExecuteAsync("CREATE TABLE t (Id INT, Payload VARCHAR(2000))", cancellationToken: timeout.Token);
            }
            else { await database.ApplySchemaAsync(original.Compile(), timeout.Token); }
            await using var session = await database.CreateSessionAsync(timeout.Token);
            await FillAsync(session, "t", rows, timeout.Token);
            strategy = strategy.CaptureDurableImages();
        }
        using var pause = new ManualResetEventSlim();
        var builder = Builder(strategy);
        int insideBuild = 0;
        builder.AddWorker(engine =>
        {
            engine.BeforeProvisioning = database =>
            {
                database.TableReclamation.BeforeBatch = (_, token) => pause.Wait(token);
                database.SchemaProvisioner.StepApplied = _ =>
                {
                    database.TableReclamation.RunBatch(timeout.Token);
                    database.TableReclamation.ReclaimedPages.ShouldBe(0);
                    Interlocked.Increment(ref insideBuild);
                };
            };
            return new RecordingWorker(engine);
        });
        builder.AddDatabase("db", database =>
        {
            if (scripted) { database.Script("drop", "DROP TABLE t"); }
            else { database.Schema(SqlSchema.Create("db", schema => schema.AllowDestructiveChanges())); }
        });

        // Act
        await using var built = await builder.BuildAsync(timeout.Token);
        var result = await built.OpenDatabaseAsync("db", timeout.Token);

        // Assert
        result.Catalog.TryGetTable("dbo", "t", out _).ShouldBeFalse();
        result.TableReclamation.ReclaimedPages.ShouldBe(0);
        if (!scripted) { insideBuild.ShouldBeGreaterThan(0); }
        pause.Set();
        await WaitDoneAsync(result, timeout.Token);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - A pre-drop snapshot pins reclamation without blocking other tables")]
    public async Task DropTable_OldSnapshot_ShouldWaitForItsHorizon()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("horizon", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await SeedAsync(session, "t", 100, timeout.Token);
        await session.ExecuteAsync("CREATE TABLE other (id INT)", cancellationToken: timeout.Token);
        await using var old = await database.CreateSessionAsync(timeout.Token);
        var transaction = await old.BeginTransactionAsync(IsolationLevel.Snapshot, timeout.Token);

        // Act: exercise the worker repeatedly while the old snapshot is alive.
        await session.ExecuteAsync("DROP TABLE t", cancellationToken: timeout.Token);
        int passes = 0;
        var elapsed = Stopwatch.StartNew();
        do
        {
            database.TableReclamation.RunBatch(timeout.Token);
            await session.ExecuteAsync("INSERT INTO other VALUES (1)", cancellationToken: timeout.Token);
            passes++;
            timeout.Token.ThrowIfCancellationRequested();
        } while (elapsed.Elapsed < TimeSpan.FromMilliseconds(100) || passes < 10);

        // Assert
        passes.ShouldBeGreaterThanOrEqualTo(10);
        database.TableReclamation.ReclaimedPages.ShouldBe(0);
        await transaction.CommitAsync(timeout.Token);
        await WaitDoneAsync(database, timeout.Token);
        database.TableReclamation.ReclaimedPages.ShouldBeGreaterThan(0);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - A mid-scan reader never observes freed or reused pages after DROP")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DropTable_ReaderMidScan_ShouldRetainItsPagesUntilStatementEnds(bool rollbackReader, bool indexed)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int visits = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("scan-reclamation");
        builder.Functions.Add(SqlScalarFunction.Create("hold_row", (long id) =>
        {
            if (Interlocked.Increment(ref visits) == 1)
            {
                entered.Set();
                release.Wait(timeout.Token);
            }
            return id;
        }));
        await using var engine = await builder.BuildAsync(timeout.Token);
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var writer = await database.CreateSessionAsync(timeout.Token);
        await SeedAsync(writer, "t", 100, timeout.Token);
        await using var reader = await database.CreateSessionAsync(timeout.Token);
        var snapshot = await reader.BeginTransactionAsync(IsolationLevel.Snapshot, timeout.Token);
        string sql = indexed ? "SELECT id FROM t WHERE id >= 0 AND id < 100 AND hold_row(id) >= 0" : "SELECT id FROM t WHERE hold_row(id) >= 0";
        var scan = Task.Run(async () => await reader.ExecuteAsync(sql, cancellationToken: timeout.Token));
        try
        {
            entered.Wait(timeout.Token);

            // Act: even a rollback ending the transaction must not end the statement's lease.
            await writer.ExecuteAsync("DROP TABLE t", cancellationToken: timeout.Token);
            if (rollbackReader) { await snapshot.RollbackAsync(timeout.Token); }
            await SeedAsync(writer, "t", 100, timeout.Token);
            int passes = 0;
            var elapsed = Stopwatch.StartNew();
            do
            {
                database.TableReclamation.RunBatch(timeout.Token);
                passes++;
                timeout.Token.ThrowIfCancellationRequested();
            } while (elapsed.Elapsed < TimeSpan.FromMilliseconds(100) || passes < 10);

            // Assert
            passes.ShouldBeGreaterThanOrEqualTo(10);
            database.TableReclamation.ReclaimedPages.ShouldBe(0);
        }
        finally { release.Set(); }
        var result = (await scan).ShouldBeAssignableTo<QueryResultSet>();
        int read = 0;
        await foreach (var row in result.GetRowsAsync(timeout.Token)) { row.GetValue(0).ShouldNotBeNull(); read++; }
        read.ShouldBe(100);
        visits.ShouldBe(100);
        if (indexed) { reader.LastStatementMetrics.ShouldNotBeNull().AccessPath.ShouldStartWith("seek:"); }
        if (!rollbackReader) { await snapshot.CommitAsync(timeout.Token); }
        await WaitDoneAsync(database, timeout.Token);
        database.TableReclamation.ReclaimedPages.ShouldBeGreaterThan(0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Several dropped identities reclaim independently of replacement names")]
    public async Task DropTable_SeveralTables_ShouldReclaimEveryIdentity()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("many-drops", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        foreach (string name in new[] { "a", "b", "c" }) { await SeedAsync(session, name, 20, timeout.Token); }
        var ids = database.Catalog.Tables.Select(table => table.ObjectId).ToArray();

        // Act
        foreach (string name in new[] { "a", "b", "c" }) { await session.ExecuteAsync($"DROP TABLE {name}", cancellationToken: timeout.Token); }
        await WaitDoneAsync(database, timeout.Token);

        // Assert
        foreach (ulong id in ids) { database.DataStorage.GetOwnerPages(id).ShouldBeEmpty(); }
        database.Catalog.Tables.ShouldBeEmpty();
        database.TableReclamation.ReclaimedPages.ShouldBeGreaterThan(3);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Reclamation batches race other tables' reads and writes without taking their apply gate")]
    public async Task DropTable_ConcurrentOtherTable_ShouldReclaimWhileWritesProgress()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("concurrent-reclamation", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await SeedAsync(session, "t", 3000, timeout.Token);
        await session.ExecuteAsync("CREATE TABLE other (id INT PRIMARY KEY)", cancellationToken: timeout.Token);
        int batches = 0;
        database.TableReclamation.AfterBatch = (_, count) => { if (count > 0) { Interlocked.Increment(ref batches); } };

        // Act: run until both sides demonstrably progress, with a deadline rather than a fixed race window.
        await session.ExecuteAsync("DROP TABLE t", cancellationToken: timeout.Token);
        int writes = 0;
        var elapsed = Stopwatch.StartNew();
        do
        {
            await session.ExecuteAsync($"INSERT INTO other VALUES ({writes++})", cancellationToken: timeout.Token);
            (await CountAsync(session, "other", timeout.Token)).ShouldBe(writes);
            timeout.Token.ThrowIfCancellationRequested();
        } while (elapsed.Elapsed < TimeSpan.FromMilliseconds(150) || writes < 10 || Volatile.Read(ref batches) < 3);
        await WaitDoneAsync(database, timeout.Token);

        // Assert
        writes.ShouldBeGreaterThanOrEqualTo(10);
        batches.ShouldBeGreaterThanOrEqualTo(3);
        database.DataStorage.GetOwnerPages(1).ShouldBeEmpty();
        (await CountAsync(session, "other", timeout.Token)).ShouldBe(writes);
    }

    private static SqlDatabaseEngineBuilder Builder(CrashCaptureSqlStorageStrategy strategy)
    {
        var builder = SqlDatabaseEngine.CreateBuilder("reclamation");
        builder.Options.StorageStrategy = strategy;
        return builder;
    }

    private static async Task ExecuteWireAsync(SqlDatabaseEngine engine, string sql, CancellationToken cancellationToken)
    {
        await using var listener = new InMemoryConnectionListener();
        await using var server = SqlDatabaseServer.Create(engine, new SqlDatabaseServerOptions { Listener = listener });
        await server.StartAsync(cancellationToken);
        await using var client = SqlClient.Create(new SqlClientOptions
        {
            Settings = new DatabaseConnectionSettings { Database = "db", Principal = "reclamation-test", EndPoint = listener.EndPoint },
            ConnectionFactory = listener.CreateFactory(),
        });
        await using var connection = await client.ConnectAsync(cancellationToken);
        await connection.ExecuteAsync(sql, cancellationToken: cancellationToken);
    }

    private static ulong TableId(SqlDatabase database, string name)
    {
        database.Catalog.TryGetTable("dbo", name, out var table).ShouldBeTrue();
        return table.ObjectId;
    }

    private static async Task SeedAsync(SqlDatabaseSession session, string name, int rows, CancellationToken cancellationToken)
    {
        await session.ExecuteAsync($"CREATE TABLE {name} (id INT PRIMARY KEY, payload VARCHAR(2000))", cancellationToken: cancellationToken);
        await FillAsync(session, name, rows, cancellationToken);
    }

    private static async Task FillAsync(SqlDatabaseSession session, string name, int rows, CancellationToken cancellationToken)
    {
        string payload = new('x', 1800);
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.Snapshot, cancellationToken);
        for (int row = 0; row < rows; row++)
        {
            await session.ExecuteAsync($"INSERT INTO {name} VALUES ({row}, '{payload}')", cancellationToken: cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<long> CountAsync(SqlDatabaseSession session, string table, CancellationToken cancellationToken)
    {
        var result = (await session.ExecuteAsync($"SELECT COUNT(*) FROM {table}", cancellationToken: cancellationToken)).ShouldBeAssignableTo<QueryResultSet>();
        await foreach (var row in result.GetRowsAsync(cancellationToken)) { return (long)row.GetValue(0)!; }
        throw new InvalidOperationException();
    }

    private static async Task WaitDoneAsync(SqlDatabase database, CancellationToken cancellationToken)
    {
        while (database.TableReclamation.HasWork) { await Task.Delay(5, cancellationToken); }
        database.OfflineError.ShouldBeNull();
    }

    private sealed record Item(int Id, string Payload);
}
