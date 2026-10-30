using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(SqlTimingCollection.Name)]
public sealed class SqlPartialIndexDurabilityTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Partial indexes: crash images fence create, build, publication and drop (#1630)")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ExecuteAsync_DurableStage_ShouldRecoverSymmetricMetadataAndTree(int stage)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var storage = new CrashCaptureSqlStorageStrategy();
        await using var engine = await Builder(storage).BuildAsync(timeout.Token);
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("CREATE TABLE t(id INT PRIMARY KEY,k INT,active BOOLEAN)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("INSERT INTO t VALUES(1,7,TRUE),(2,7,FALSE),(3,NULL,TRUE),(4,NULL,TRUE)", cancellationToken: timeout.Token);
        CrashCaptureSqlStorageStrategy? image = null;
        session.ProvisioningProgress = (processed, _) =>
        {
            if (image is null && (stage == 0 && processed == 0 || stage == 1 && processed > 0))
            { image = storage.CaptureDurableImages(); }
        };

        // Act
        await session.ExecuteAsync("CREATE UNIQUE INDEX ix ON t(k) WHERE ABS(k) > 0 AND active AND k IS NOT NULL", cancellationToken: timeout.Token);
        session.ProvisioningProgress = null;
        if (stage == 3) { await session.ExecuteAsync("DROP INDEX ix ON t", cancellationToken: timeout.Token); }
        image ??= storage.CaptureDurableImages();
        await using var reopened = await Builder(image).BuildAsync(timeout.Token);
        var recovered = await reopened.OpenDatabaseAsync("db", timeout.Token);
        await using var writer = await recovered.CreateSessionAsync(timeout.Token);

        // Assert
        recovered.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
        recovered.Catalog.TryGetIndex(table.ObjectId, "ix", out var index).ShouldBe(stage == 2);
        if (stage == 2)
        {
            index.Predicate.ShouldBe("ABS(k) > 0 AND active AND k IS NOT NULL");
            index.PredicateFunctionCalls.Count.ShouldBe(1);
            await SqlPartialIndexTests.Oracle(recovered, writer);
            await SqlConstraintAssertions.ThrowAsync(async () => await writer.ExecuteAsync("UPDATE t SET active=TRUE WHERE id=2", cancellationToken: timeout.Token));
        }
        else
        {
            recovered.IndexManager.TryGetIndex(table.ObjectId, "ix", out _).ShouldBeFalse();
            await writer.ExecuteAsync("UPDATE t SET active=TRUE WHERE id=2", cancellationToken: timeout.Token);
        }
        recovered.Catalog.RecordSpaceFormatVersion.ShouldBe(31);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Partial indexes: cancellation and predicate failures remove unpublished trees and release locks (#1630)")]
    public async Task ExecuteAsync_FailedOrCanceledBuild_ShouldAllowAnotherSessionToRetry()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("partial-cancel", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var ddl = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPartialIndexTests.Execute(ddl, "CREATE TABLE t(k INT)");
        await SqlPartialIndexTests.Execute(ddl, "INSERT INTO t VALUES " + string.Join(',', Enumerable.Range(0, 160).Select(i => "(" + i + ")")));
        using var canceled = new CancellationTokenSource();
        int batches = 0;
        ddl.ProvisioningProgress = (processed, _) => { if (processed > 0) { batches++; canceled.Cancel(); } };

        // Act / Assert
        await Should.ThrowAsync<OperationCanceledException>(async () => await ddl.ExecuteAsync("CREATE INDEX ix ON t(k) WHERE k>0", cancellationToken: canceled.Token));
        batches.ShouldBeGreaterThan(0);
        database.IndexManager.ExportRegistrations().ShouldBeEmpty();
        ddl.ProvisioningProgress = null;
        await Should.ThrowAsync<DatabaseException>(async () => await ddl.ExecuteAsync("CREATE INDEX ix ON t(k) WHERE 10/(k-1)>0", cancellationToken: CancellationToken.None));
        database.IndexManager.ExportRegistrations().ShouldBeEmpty();
        await using var retry = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPartialIndexTests.Execute(retry, "CREATE INDEX ix ON t(k) WHERE k>0");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Partial indexes: concurrent builds, inserts and predicate-entering updates exercise table and key waits (#1630)")]
    public async Task ExecuteAsync_WritersRacingBuildAndEachOther_ShouldPreserveSubsetAndUniqueness()
    {
        // Arrange: duration is a minimum diagnostic interval; counters establish coverage.
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var engine = SqlDatabaseEngine.Create("partial-stress", new());
        var database = await engine.CreateDatabaseAsync("db", ceiling.Token);
        await using var ddl = await database.CreateSessionAsync(ceiling.Token);
        await using var writer = await database.CreateSessionAsync(ceiling.Token);
        await using var outside = await database.CreateSessionAsync(ceiling.Token);
        int buildWaits = 0, keyWaits = 0, conflicts = 0, outsideWrites = 0;
        long waitsSeen = 0;
        using var waits = new LockWaitListener(database.Name);
        waits.Observed = written => { if (written.EventName == "LockWaitStart") { Interlocked.Increment(ref waitsSeen); } };
        var elapsed = Stopwatch.StartNew();

        // Act
        do
        {
            ceiling.Token.ThrowIfCancellationRequested();
            await ddl.ExecuteAsync("CREATE TABLE t(id INT PRIMARY KEY,k INT,active BOOLEAN)", cancellationToken: ceiling.Token);
            await ddl.ExecuteAsync("INSERT INTO t VALUES(1,7,TRUE),(2,7,FALSE),(3,8,FALSE)", cancellationToken: ceiling.Token);
            Task<DatabaseResult>? entering = null, excluded = null;
            ddl.ProvisioningProgress = (processed, _) =>
            {
                if (processed == 0 || entering is not null) { return; }
                entering = writer.ExecuteAsync(buildWaits % 2 == 0 ? "UPDATE t SET active=TRUE WHERE id=2" : "INSERT INTO t VALUES(4,7,TRUE)", cancellationToken: ceiling.Token).AsTask();
                excluded = outside.ExecuteAsync("INSERT INTO t VALUES(5,7,FALSE)", cancellationToken: ceiling.Token).AsTask();
                entering.IsCompleted.ShouldBeFalse();
                excluded.IsCompleted.ShouldBeFalse();
                buildWaits++;
            };
            try { await ddl.ExecuteAsync("CREATE UNIQUE INDEX ix ON t(k) WHERE active AND k IS NOT NULL", cancellationToken: ceiling.Token); }
            finally { ddl.ProvisioningProgress = null; }
            await SqlConstraintAssertions.ThrowAsync(async () => await entering.ShouldNotBeNull());
            conflicts++;
            await excluded.ShouldNotBeNull();
            outsideWrites++;
            await SqlPartialIndexTests.Oracle(database, ddl);
            await using (var held = await writer.BeginTransactionAsync(IsolationLevel.ReadCommitted, ceiling.Token))
            {
                await writer.ExecuteAsync(buildWaits % 2 == 0 ? "INSERT INTO t VALUES(6,8,TRUE)" : "UPDATE t SET active=TRUE WHERE id=3", cancellationToken: ceiling.Token);
                var pending = outside.ExecuteAsync(buildWaits % 2 == 0 ? "UPDATE t SET active=TRUE WHERE id=3" : "UPDATE t SET active=TRUE,k=8 WHERE id=2", cancellationToken: ceiling.Token).AsTask();
                pending.IsCompleted.ShouldBeFalse();
                keyWaits++;
                await held.CommitAsync(ceiling.Token);
                await SqlConstraintAssertions.ThrowAsync(async () => await pending);
                conflicts++;
            }
            await SqlPartialIndexTests.Oracle(database, ddl);
            await ddl.ExecuteAsync("DROP TABLE t", cancellationToken: ceiling.Token);
        } while (elapsed.Elapsed < TimeSpan.FromSeconds(1) || buildWaits < 10 || keyWaits < 10 || Interlocked.Read(ref waitsSeen) < buildWaits * 3);

        // Assert
        buildWaits.ShouldBeGreaterThanOrEqualTo(10);
        keyWaits.ShouldBe(buildWaits);
        outsideWrites.ShouldBe(buildWaits);
        conflicts.ShouldBe(buildWaits * 2);
        Interlocked.Read(ref waitsSeen).ShouldBeGreaterThanOrEqualTo(buildWaits * 3);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Partial indexes: format 30 is refused by the existing gate (#1630)")]
    public async Task OpenDatabaseAsync_Format30_ShouldRefuseBeforeDataRecovery()
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        await using var engine = await Builder(storage).BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await database.Catalog.SetRecordSpaceFormatVersionAsync(30, CancellationToken.None);
        var image = storage.CaptureDurableImages();
        await using var reopened = await Builder(image).BuildAsync(CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await reopened.OpenDatabaseAsync("db", CancellationToken.None));

        // Assert
        failure.Diagnostics.ShouldContain(d => d.Code == "CS06041");
        failure.Message.ShouldContain("supports only format 31", Case.Sensitive);
    }

    private static SqlDatabaseEngineBuilder Builder(CrashCaptureSqlStorageStrategy storage)
    {
        var builder = SqlDatabaseEngine.CreateBuilder("partial-crash");
        builder.Options.StorageStrategy = storage;
        return builder;
    }
}
