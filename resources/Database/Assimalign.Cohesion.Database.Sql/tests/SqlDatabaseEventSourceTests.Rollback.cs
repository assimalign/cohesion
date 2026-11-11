using System;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlDatabaseEventSourceTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Held rollback: provisioning stops Held without joint work or failure events")]
    public async Task BuildAsync_HeldRollback_ShouldTraceHeldWithoutStartingJointWork()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = new CrashCaptureSqlStorageStrategy();
        SqlSchema Declaration(bool extra) => SqlSchema.Create("held", schema => schema.Table<HeldTraceRow>("items", table =>
        {
            table.Key(row => row.Id);
            if (extra) { table.Column(row => row.Added); }
        }));
        var first = Declaration(false);
        var seed = SqlDatabaseEngine.CreateBuilder(UniqueEngineName());
        seed.Options.StorageStrategy = storage;
        await using (var engine = await seed.BuildAsync(timeout.Token))
        {
            var database = await engine.CreateDatabaseAsync("held", timeout.Token);
            await database.ApplySchemaAsync(first.Compile(), timeout.Token);
            await database.ApplySchemaAsync(Declaration(true).Compile(), timeout.Token);
            storage = storage.CaptureDurableImages();
        }
        using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Informational);
        var rollback = SqlDatabaseEngine.CreateBuilder(UniqueEngineName());
        rollback.Options.StorageStrategy = storage;

        // Act
        await using var held = await rollback.AddDatabase(first).BuildAsync(timeout.Token);

        // Assert
        var stop = NewEvent(recorder, 14, "ProvisioningStop", _provisioningStopPayload);
        stop.Payload![5].ShouldBe("Held");
        recorder.Events.Count(entry => entry.EventId == 13).ShouldBe(1);
        recorder.Events.ShouldNotContain(entry => entry.EventId == 0 || entry.EventId == 15 || entry.EventId == 18 || entry.EventId == 19 || entry.EventId == 20 || entry.EventId == 21);
        (await held.OpenDatabaseAsync("held", timeout.Token)).Provisioning!.FailureCode.ShouldBe("CS03405");

        // Act: an unrelated physical owner build and DROP share this recording with Held.
        var physicalDatabase = await held.OpenDatabaseAsync("held", timeout.Token);
        await using var session = await physicalDatabase.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("CREATE TABLE physical(v INT)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("INSERT INTO physical VALUES(1)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("CREATE INDEX ix ON physical(v)", cancellationToken: timeout.Token);
        ulong owner = physicalDatabase.IndexManager.PageOwner(physicalDatabase.IndexManager.ExportRegistrations().Single(index => index.Definition.Name == "ix"));
        long pages = physicalDatabase.DataStorage.GetOwnerIndexPageCount(owner);
        await session.ExecuteAsync("DROP INDEX ix ON physical", cancellationToken: timeout.Token);
        while (physicalDatabase.TableReclamation.HasWork) { await Task.Delay(1, timeout.Token); }

        // Assert: the owner lifecycle uses Info events; it cannot close the Held activity.
        var began = recorder.Events.Where(e => e.EventId == 28 && Equals(e.Payload![0], rollback.Name.ToString()) && Equals(e.Payload![2], (long)owner)).ShouldHaveSingleItem();
        var ended = recorder.Events.Where(e => e.EventId == 29 && Equals(e.Payload![0], rollback.Name.ToString()) && Equals(e.Payload![2], (long)owner)).ShouldHaveSingleItem();
        began.EventName.ShouldBe("IndexBuildBegan");
        ended.EventName.ShouldBe("IndexBuildEnded");
        began.Opcode.ShouldBe(EventOpcode.Info);
        ended.Opcode.ShouldBe(EventOpcode.Info);
        ended.Payload!.Take(3).ShouldBe(began.Payload);
        ended.Payload![3].ShouldBe(true);
        recorder.Events.Where(e => e.EventId == 30 && Equals(e.Payload![0], rollback.Name.ToString()) && Equals(e.Payload![2], (long)owner)).Sum(e => (long)(int)e.Payload![3]!).ShouldBe(pages);
        recorder.Events.Count(e => e.EventId == 13).ShouldBe(1);
        recorder.Events.Count(e => e.EventId == 14).ShouldBe(1);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    private sealed record HeldTraceRow(int Id, int? Added);
}
