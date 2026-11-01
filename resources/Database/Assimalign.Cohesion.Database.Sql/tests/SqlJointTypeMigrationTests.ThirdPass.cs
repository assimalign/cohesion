using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Joint estimate: actual narrow rows admit text conversion and literal defaults")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyAsync_NarrowActualBytes_ShouldAdmitWithBoundedEstimate(bool addedDefault)
    {
        // Arrange: compare replacement R separately from the documented WAL/margin/reserve.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var storage = new CrashCaptureSqlStorageStrategy();
        await using var engine = await Builder(storage).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        var source = NarrowDeclaration(false, false);
        var target = NarrowDeclaration(true, addedDefault);
        await database.ApplySchemaAsync(source, guard.Token);
        SeedRaw(database, [1, 1], 16384);
        database.Coordinator.Checkpoint(guard.Token);
        long sourcePages = database.DataStorage.GetOwnerPages(Table(database).ObjectId).Count;
        long before = database.DataStorage.PageManager.PageCount;
        // 96 MiB admits actual data plus the documented bounded WAL and reserves.
        // The old declared-TEXT model asks for hundreds of MiB for this tiny heap.
        storage.MigrationAvailableBytes = 96L * 1024 * 1024;

        // Act
        await database.ApplySchemaAsync(target, guard.Token);

        // Assert
        long copied = database.DataStorage.GetOwnerPages(Table(database).ObjectId).Count * Page.Size;
        long physical = (database.DataStorage.PageManager.PageCount - before) * Page.Size;
        database.SchemaProvisioner.JointSpaceNeed.ShouldBeLessThanOrEqualTo(storage.MigrationAvailableBytes);
        database.SchemaProvisioner.JointSpaceReplacementBytes.ShouldBeLessThanOrEqualTo(3 * copied);
        database.SchemaProvisioner.JointSpaceReplacementBytes.ShouldBeGreaterThanOrEqualTo(copied);
        database.Catalog.SchemaState!.ContentHash.ShouldBe(target.Hash);
        _output.WriteLine($"narrow added={addedDefault}: old pages={sourcePages}; R={database.SchemaProvisioner.JointSpaceReplacementBytes}; copied={copied}; file growth={physical}; N={database.SchemaProvisioner.JointSpaceNeed}");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint estimate: NULL remains one byte across scalar-to-CHAR conversion")]
    public async Task ApplyAsync_NullCharacterConversion_ShouldAdmitWithoutPaddingNulls()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = new CrashCaptureSqlStorageStrategy();
        await using var engine = await Builder(storage).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        var source = Compiled(new CompiledSchemaTable("items", "row", [new("Code", DatabaseType.Float64, true)], null, [], []));
        var target = Compiled(new CompiledSchemaTable("items", "row", [new("Code", DatabaseType.Character, true, MaxLength: 4096)], null, [], []));
        await database.ApplySchemaAsync(source, guard.Token);
        SeedRaw(database, [null], 16384);
        storage.MigrationAvailableBytes = 96L * 1024 * 1024;

        // Act
        await database.ApplySchemaAsync(target, guard.Token);

        // Assert
        long copied = database.DataStorage.GetOwnerPages(Table(database).ObjectId).Count * Page.Size;
        database.SchemaProvisioner.JointSpaceReplacementBytes.ShouldBe(copied);
        database.Catalog.SchemaState!.ContentHash.ShouldBe(target.Hash);
        await using var reader = await database.CreateSessionAsync(guard.Token);
        (await RowsAsync(reader, "SELECT COUNT(*) FROM items WHERE Code IS NULL", guard.Token)).Single()[0].ShouldBe(16384L);
        _output.WriteLine($"NULL to CHAR: R={database.SchemaProvisioner.JointSpaceReplacementBytes}; copied={copied}; N={database.SchemaProvisioner.JointSpaceNeed}");
    }

    private static SqlCompiledSchema NarrowDeclaration(bool changed, bool added)
        => Compiled(new CompiledSchemaTable("items", "row", added
            ? [new("Id", DatabaseType.Int64, false), new("Value", DatabaseType.Int32, false), new("Status", DatabaseType.String, false, MaxLength: 32, DefaultLiteral: "'active'")]
            : [new("Id", DatabaseType.Int32, false), new("Value", changed ? DatabaseType.String : DatabaseType.Int32, false)], null, [], []));

    private static void SeedRaw(SqlDatabase database, object?[] values, int count)
    {
        var table = Table(database);
        byte[] record = SqlRowCodec.Encode(table, values, TransactionSequence.None);
        for (int first = 0; first < count; first += 64)
        {
            using var bracket = database.DataStorage.BeginTransaction();
            for (int row = first; row < Math.Min(count, first + 64); row++)
            { database.DataStorage.InsertRow(bracket, table.ObjectId, record); }
            bracket.Commit();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint estimate: TEXT cannot hide CHAR padding growth")]
    public async Task ApplyAsync_CharWithText_ShouldChargeActualPaddingGrowth()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = new CrashCaptureSqlStorageStrategy();
        SqlCompiledSchema Declare(int width) => Compiled(new CompiledSchemaTable("items", "row",
            [new("Id", DatabaseType.Int32, false), new("Notes", DatabaseType.String, true), new("Code", DatabaseType.Character, false, MaxLength: width)], null, [], []));
        await using var engine = await Builder(storage).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await database.ApplySchemaAsync(Declare(10), guard.Token);
        SeedRaw(database, [1, "x", new SqlCharacter("x".PadRight(10))], 2048);
        long oldBytes = database.DataStorage.GetOwnerPages(Table(database).ObjectId).Count * Page.Size;

        // Act
        await database.ApplySchemaAsync(Declare(200), guard.Token);

        // Assert
        long copied = database.DataStorage.GetOwnerPages(Table(database).ObjectId).Count * Page.Size;
        database.SchemaProvisioner.JointSpaceReplacementBytes.ShouldBeGreaterThan(3 * oldBytes);
        database.SchemaProvisioner.JointSpaceReplacementBytes.ShouldBeGreaterThanOrEqualTo(copied);
        _output.WriteLine($"CHAR plus TEXT: old={oldBytes}; R={database.SchemaProvisioner.JointSpaceReplacementBytes}; copied={copied}");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint watchdog: shrinking space stops before copy/index brackets and retries online")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyAsync_ShrinkingVolume_ShouldDiscardOnlineAndRetry(bool roots)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = await SeedAsync(guard.Token, 129);
        await using var engine = await Builder(storage).BuildAsync(guard.Token);
        var database = await engine.OpenDatabaseAsync("typed", guard.Token);
        int copied = 0;
        database.SchemaProvisioner.JointStage = stage =>
        { if (roots && stage == "OwnersDurable") { storage.MigrationAvailableBytes = database.SchemaProvisioner.JointSpaceDiscardBytes; } };
        SqlPlanExecutor.RewriteStage = (stage, _) =>
        { if (!roots && stage == "Copied" && ++copied == 1) { storage.MigrationAvailableBytes = database.SchemaProvisioner.JointSpaceDiscardBytes; } };
        try
        {
            // Act
            var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await database.ApplySchemaAsync(Target().Compile(), guard.Token));

            // Assert
            failure.Diagnostics.ShouldContain(d => d.Code == "CS03402");
            failure.Message.ShouldContain("bytes needed");
            failure.Message.ShouldContain("bytes available");
            database.Catalog.PendingMigration.ShouldBeNull();
            database.Catalog.SchemaState!.ContentHash.ShouldBe(Source().Compile().Hash);
            database.IsOffline.ShouldBeFalse();
            if (!roots) { copied.ShouldBe(1); }
            storage.MigrationAvailableBytes = long.MaxValue;
            database.SchemaProvisioner.JointStage = null;
            SqlPlanExecutor.RewriteStage = null;
            await database.ApplySchemaAsync(Target().Compile(), guard.Token);
            database.Catalog.SchemaState!.ContentHash.ShouldBe(Target().Compile().Hash);
        }
        finally { SqlPlanExecutor.RewriteStage = null; }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint watchdog: an opaque application cast cannot fill the volume")]
    public async Task ApplyAsync_ExpandingApplicationCast_ShouldStopBeforeVolumeFillsAndRetry()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var storage = new CrashCaptureSqlStorageStrategy();
        var builder = Builder(storage);
        var domain = SqlType.CreateDomain("expanded", SqlType.Text);
        builder.Types.Add(domain);
        builder.Types.AddCast(SqlCast.Create<int, string>(SqlType.Integer, domain, _ => new string('x', 7000), SqlCastContext.Assignment));
        await using var engine = await builder.BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        var source = Compiled(new CompiledSchemaTable("items", "row", [new("Code", DatabaseType.Int32, false)], null, [], []));
        var target = Compiled(new CompiledSchemaTable("items", "row", [new("Code", DatabaseType.String, false, CustomType: "expanded")], null, [], []));
        await database.ApplySchemaAsync(source, guard.Token);
        SeedRaw(database, [1], 4096);
        database.Coordinator.Checkpoint(guard.Token);
        database.SchemaProvisioner.JointStage = stage =>
        {
            if (stage == "OwnersDurable")
            { storage.VolumeCapacityBytes = storage.AllocatedBytes + database.SchemaProvisioner.JointSpaceNeed; }
        };

        // Act
        var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await database.ApplySchemaAsync(target, guard.Token));

        // Assert
        failure.Diagnostics.ShouldContain(d => d.Code == "CS03402");
        failure.Message.ShouldContain("bytes needed");
        storage.CapacityFailures.ShouldBe(0);
        storage.PeakAllocatedBytes.ShouldBeLessThan(storage.VolumeCapacityBytes);
        database.Catalog.SchemaState!.ContentHash.ShouldBe(source.Hash);
        database.Catalog.PendingMigration.ShouldBeNull();
        database.IsOffline.ShouldBeFalse();
        await using var reader = await database.CreateSessionAsync(guard.Token);
        (await RowsAsync(reader, "SELECT COUNT(*) FROM items", guard.Token)).Single()[0].ShouldBe(4096L);
        _output.WriteLine($"opaque cast: N={database.SchemaProvisioner.JointSpaceNeed}; capacity={storage.VolumeCapacityBytes}; peak={storage.PeakAllocatedBytes}; actual={storage.AllocatedBytes}; code={failure.Diagnostics[0].Code}");
        storage.VolumeCapacityBytes = long.MaxValue;
        database.SchemaProvisioner.JointStage = null;
        await database.ApplySchemaAsync(target, guard.Token);
        database.Catalog.SchemaState!.ContentHash.ShouldBe(target.Hash);
        database.Provisioning.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint identity: implicit CREATE/ADD identity candidates skip pending table names")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyAsync_ImplicitIdentityName_ShouldSkipJointReservation(bool add)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = await Builder(await SeedAsync(guard.Token)).BuildAsync(guard.Token);
        var database = await engine.OpenDatabaseAsync("typed", guard.Token);
        var target = Compiled(Target().Compile().Tables.Append(new CompiledSchemaTable("scratch_c_seq", "row", [new("id", DatabaseType.Int32, true)], null, [], [])).ToArray());
        await using var session = await database.CreateSessionAsync(guard.Token);
        if (add) { await session.ExecuteAsync("CREATE TABLE scratch(id INT)", cancellationToken: guard.Token); }
        database.SchemaProvisioner.BeforeDeferredStep = (_, token) => { entered.TrySetResult(); release.Wait(token); };
        await database.SchemaProvisioner.ApplyAsync(target, guard.Token, true);
        await entered.Task.WaitAsync(guard.Token);
        try
        {
            // Act
            await session.ExecuteAsync(add ? "ALTER TABLE scratch ADD COLUMN c INT GENERATED ALWAYS AS IDENTITY" : "CREATE TABLE scratch(c INT GENERATED ALWAYS AS IDENTITY)", cancellationToken: guard.Token);

            // Assert
            database.Catalog.Sequences.ShouldNotContain(sequence => sequence.Name.Equals("scratch_c_seq", StringComparison.OrdinalIgnoreCase));
            database.Catalog.Sequences.ShouldContain(sequence => sequence.Name.Equals("scratch_c_seq1", StringComparison.OrdinalIgnoreCase));
            release.Set();
            await WaitDoneAsync(database, guard.Token);
            database.Catalog.SchemaState!.ContentHash.ShouldBe(target.Hash);
            database.Provisioning.ShouldBeNull();
        }
        finally { release.Set(); }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Volume path: long device roots use the extended device prefix")]
    public void NormalizeDirectory_LongDevicePath_ShouldKeepDeviceRoot()
    {
        // Arrange
        string suffix = "C:\\" + new string('x', 260);
        // Act
        string normalized = SqlVolumeSpace.NormalizeDirectory(@"\\.\" + suffix);
        // Assert
        normalized.ShouldBe(@"\\?\" + suffix + "\\");
        SqlVolumeSpace.NormalizeDirectory(normalized).ShouldBe(normalized);
    }
}
