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
    }

    private sealed record HeldTraceRow(int Id, int? Added);
}
