using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection("SQL namespace DDL interlock")]
public sealed class SqlSchemaDdlInterlockReviewTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Schema DDL between deferred steps never takes the database offline")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_SchemaDdlDuringDeferredMigration_ShouldStayOnline(bool drop)
    {
        // Arrange: pause between two real deferred index builds on a populated typed table.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var releaseWorker = new ManualResetEventSlim();
        using var enteredDdl = new ManualResetEventSlim();
        using var releaseDdl = new ManualResetEventSlim();
        var enteredWorker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var storage = new CrashCaptureSqlStorageStrategy();
        var builder = SqlDatabaseEngine.CreateBuilder("schema-deferred");
        builder.Options.StorageStrategy = storage;
        await using var engine = await builder.BuildAsync(timeout.Token);
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        var items = new CompiledSchemaTable("items", "Item", [new("id", DatabaseType.Int32, false)], null, [], []);
        var source = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "db", false, [], [items], []);
        await database.ApplySchemaAsync(source, timeout.Token);
        await using var holder = await database.CreateSessionAsync(timeout.Token);
        await using var ddl = await database.CreateSessionAsync(timeout.Token);
        await holder.ExecuteAsync("INSERT INTO items VALUES (1)", cancellationToken: timeout.Token);
        await holder.ExecuteAsync("CREATE SCHEMA sales", cancellationToken: timeout.Token);
        await holder.ExecuteAsync("CREATE TABLE sales.t(id INT)", cancellationToken: timeout.Token);
        var target = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "db", false, [],
            [new CompiledSchemaTable(items.Name, items.RowType, items.Columns, null,
                [new("ix_one", ["id"]), new("ix_two", ["id"])], [])], []);
        int steps = 0;
        int resumed = 0;
        database.SchemaProvisioner.BeforeDeferredStep = (_, token) =>
        {
            if (Interlocked.Increment(ref steps) == 2)
            {
                enteredWorker.TrySetResult();
                releaseWorker.Wait(token);
                Interlocked.Increment(ref resumed);
            }
        };
        try
        {
            await database.SchemaProvisioner.ApplyAsync(target, timeout.Token, deferDataScalingSteps: true);
            await enteredWorker.Task.WaitAsync(timeout.Token);
            await using var transaction = await holder.BeginTransactionAsync(timeout.Token);
            Task<DatabaseResult> pending;
            if (drop)
            {
                await holder.ExecuteAsync("INSERT INTO sales.t VALUES (7)", cancellationToken: timeout.Token);
                using var waits = new EventSourceRecorder(EventSource.GetSources().Single(value =>
                    value.Name == "Assimalign.Cohesion.Database.Transactions"), EventLevel.Verbose);
                pending = ddl.ExecuteAsync("DROP SCHEMA sales CASCADE", cancellationToken: timeout.Token).AsTask();
                await waits.WaitForAsync(value => value.EventName == "LockWaitStart" &&
                    value.Payload is { Count: > 3 } payload && Equals(payload[0], "db") && Equals(payload[3], "Exclusive"), timeout.Token);
            }
            else
            {
                int armed = 1;
                storage.StorageWriteStarting = catalog =>
                {
                    if (catalog && Interlocked.Exchange(ref armed, 0) == 1)
                    {
                        enteredDdl.Set();
                        releaseDdl.Wait(timeout.Token);
                    }
                };
                pending = DedicatedTestTask.Run(async () => await ddl.ExecuteAsync("CREATE SCHEMA other", cancellationToken: timeout.Token), timeout.Token);
                enteredDdl.Wait(timeout.Token);
            }

            // Act: DROP waits on A's INSERT; CREATE holds a real definition lease.
            releaseWorker.Set();
            int observations = 0;
            var elapsed = Stopwatch.StartNew();
            do
            {
                database.IsOffline.ShouldBeFalse();
                pending.IsCompleted.ShouldBeFalse();
                if (!drop) { database.SchemaProvisioner.Deferred.ShouldNotBeNull(); }
                observations++;
                await Task.Delay(1, timeout.Token);
                elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
            } while (elapsed.Elapsed < TimeSpan.FromMilliseconds(150) || observations < 10 || Volatile.Read(ref resumed) == 0 ||
                drop && database.SchemaProvisioner.Deferred is not null);
            observations.ShouldBeGreaterThanOrEqualTo(10);
            resumed.ShouldBe(1);
            releaseDdl.Set();
            await transaction.CommitAsync(timeout.Token);
            (await pending.WaitAsync(timeout.Token)).Status.ShouldBe(DatabaseResultStatus.Success);
            while (database.SchemaProvisioner.Deferred is not null) { await Task.Delay(1, timeout.Token); }

            // Assert: both deferred steps completed, and the requested schema DDL also committed.
            steps.ShouldBe(2);
            database.IsOffline.ShouldBeFalse();
            engine.OfflineDatabases.ShouldBeEmpty();
            database.Catalog.PendingMigration.ShouldBeNull();
            database.Catalog.SchemaState.ShouldNotBeNull().ContentHash.ShouldBe(target.Hash);
            database.Catalog.Namespaces.Any(value => value.Name == (drop ? "sales" : "other")).ShouldBe(!drop);
        }
        finally
        {
            storage.StorageWriteStarting = null;
            releaseWorker.Set();
            releaseDdl.Set();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - A namespace changed during DROP's lock wait receives a coded retry refusal")]
    public async Task ExecuteAsync_NamespaceChangedWhileWaiting_ShouldReleaseLocksWithoutPublication()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("schema-revalidation", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var holder = await database.CreateSessionAsync(timeout.Token);
        await using var dropper = await database.CreateSessionAsync(timeout.Token);
        await using var creator = await database.CreateSessionAsync(timeout.Token);
        await holder.ExecuteAsync("CREATE SCHEMA sales", cancellationToken: timeout.Token);
        await holder.ExecuteAsync("CREATE TABLE sales.t(id INT)", cancellationToken: timeout.Token);
        await using var transaction = await holder.BeginTransactionAsync(timeout.Token);
        await holder.ExecuteAsync("INSERT INTO sales.t VALUES (1)", cancellationToken: timeout.Token);
        using var waits = new EventSourceRecorder(EventSource.GetSources().Single(value =>
            value.Name == "Assimalign.Cohesion.Database.Transactions"), EventLevel.Verbose);

        // Act: creation is allowed while DROP has no metadata lease and waits on the writer.
        var pending = dropper.ExecuteAsync("DROP SCHEMA sales CASCADE", cancellationToken: timeout.Token).AsTask();
        await waits.WaitForAsync(value => value.EventName == "LockWaitStart" &&
            value.Payload is { Count: > 3 } payload && Equals(payload[0], "db") && Equals(payload[3], "Exclusive"), timeout.Token);
        await creator.ExecuteAsync("CREATE TABLE sales.added(id INT)", cancellationToken: timeout.Token);
        ulong generation = database.Catalog.DdlGeneration;
        await transaction.CommitAsync(timeout.Token);
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await pending.WaitAsync(timeout.Token));

        // Assert: the retry operates on the complete new set, with no stranded locks.
        failure.Diagnostics.ShouldContain(value => value.Code == "CS05013");
        failure.Message.ShouldContain("Retry the statement", Case.Sensitive);
        database.Catalog.DdlGeneration.ShouldBe(generation);
        database.Catalog.Tables.Count.ShouldBe(2);
        (await dropper.ExecuteAsync("DROP SCHEMA sales CASCADE", cancellationToken: timeout.Token)).Status.ShouldBe(DatabaseResultStatus.Success);
        database.Catalog.Tables.ShouldBeEmpty();
        database.IsOffline.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Deferred metadata admission waits cancellably for a definition lease")]
    public async Task EnterMetadataAsync_DefinitionLease_ShouldCancelAndAdmitAfterRelease()
    {
        // Arrange
        var interlock = new SqlDdlInterlock();
        using var definition = interlock.EnterMetadata("db", definition: true);
        using var cancellation = new CancellationTokenSource();

        // Act
        var waiting = interlock.EnterMetadataAsync(definition: false, cancellation.Token).AsTask();
        waiting.IsCompleted.ShouldBeFalse();
        cancellation.Cancel();

        // Assert
        await Should.ThrowAsync<OperationCanceledException>(async () => await waiting);
        definition.Dispose();
        using var admitted = await interlock.EnterMetadataAsync(definition: false, CancellationToken.None);
    }
}
