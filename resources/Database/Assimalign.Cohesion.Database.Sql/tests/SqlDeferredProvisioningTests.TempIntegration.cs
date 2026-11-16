using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlDeferredProvisioningTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Interrupted joint jobs resume or discard identically with available and unavailable temp space (#1778, #1789)")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BuildAsync_InterruptedJointJobWithOptionalTempFailure_ShouldKeepDatabaseHealthy(bool unavailable, bool matchingTarget)
    {
        // Arrange: capture an unpublished job after its prepared trees are durable.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string root = Path.Combine(Path.GetTempPath(), "cohesion-joint-temp-" + Guid.NewGuid().ToString("N"));
        var desired = Desired();
        var storage = await SeedAsync(new CrashCaptureSqlStorageStrategy(), null, desired, guard.Token);
        CrashCaptureSqlStorageStrategy? crash = null;
        var seed = Builder(storage);
        seed.AddWorker(engine =>
        {
            engine.BeforeProvisioning = database => database.SchemaProvisioner.JointStage = stage =>
            {
                if (stage == "TreesDurable") { crash = storage.CaptureDurableImages(); }
            };
            return new RecordingWorker(engine);
        });
        await using (var engine = await seed.AddDatabase(desired).BuildAsync(guard.Token))
        { await WaitDoneAsync(await engine.OpenDatabaseAsync("pending", guard.Token), guard.Token); }
        try
        {
            Directory.CreateDirectory(root);
            string temp = Path.Combine(root, ".spill");
            if (unavailable) { File.WriteAllBytes(temp, [1, 2, 3]); }
            var builder = Builder(crash.ShouldNotBeNull());
            builder.Options.TempDirectory = temp;
            if (matchingTarget) { builder.AddDatabase(desired); }

            // Act
            await using var restarted = await builder.BuildAsync(guard.Token);
            var database = await restarted.OpenDatabaseAsync("pending", guard.Token);
            await WaitDoneAsync(database, guard.Token);

            // Assert: optional temp setup and a later refused spill do not fence durable work.
            database.IsOffline.ShouldBeFalse();
            restarted.OfflineDatabases.ShouldBeEmpty();
            database.Catalog.PendingMigration.ShouldBeNull();
            database.Provisioning.ShouldBeNull();
            database.Catalog.TryGetTable("dbo", "items", out var items).ShouldBeTrue();
            (items.FindColumn("Note") is not null).ShouldBe(matchingTarget);
            if (matchingTarget) { database.Catalog.SchemaState!.ContentHash.ShouldBe(desired.Compile().Hash); }
            using var statement = restarted.TempSpace.ShouldNotBeNull().CreateStatement(8192);
            if (unavailable)
            {
                Should.Throw<StorageTempException>(() => statement.CreateRun()).Code.ShouldBe("CK05005");
                File.ReadAllBytes(temp).ShouldBe([1, 2, 3]);
            }
            else { using var run = statement.CreateRun(); }
            await using var session = await database.CreateSessionAsync(guard.Token);
            await using var rows = (Execution.DatabaseResultSet)await session.ExecuteAsync("SELECT COUNT(*) FROM items", cancellationToken: guard.Token);
            await foreach (var row in rows.GetRowsAsync(guard.Token)) { row.GetValue(0).ShouldBe(130L); }
            database.IsOffline.ShouldBeFalse();
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Temp directory segments cannot collide with database names (#1789)")]
    public async Task Create_TempSegmentNames_ShouldRefuseCollidingConfiguration()
    {
        // Arrange
        string root = Path.Combine(Path.GetTempPath(), "cohesion-temp-segment-" + Guid.NewGuid().ToString("N"));
        string segment = "cohesion-" + Guid.NewGuid().ToString("N") + ".ctmp";

        // Act / Assert: a run-shaped valid database name cannot be a temp directory segment.
        Should.Throw<ArgumentOutOfRangeException>(() => SqlDatabaseEngine.Create("temp-segment", new()
        { RootPath = root, TempDirectory = Path.Combine(root, segment) })).ParamName.ShouldBe("TempDirectory");
        await using var engine = SqlDatabaseEngine.Create("reserved-segment", new());
        await Should.ThrowAsync<ArgumentException>(async () => await engine.CreateDatabaseAsync(".cohesion-temp", CancellationToken.None));
        Directory.Exists(root).ShouldBeFalse();
    }
}
