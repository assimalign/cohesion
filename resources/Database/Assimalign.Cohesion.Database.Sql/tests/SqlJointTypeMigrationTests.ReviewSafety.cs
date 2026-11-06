using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Review fix: sparse metadata and zero-operation Builds admit exactly at need (#1778)")]
    [InlineData("nullable", false)]
    [InlineData("nullable", true)]
    [InlineData("default", false)]
    [InlineData("default", true)]
    [InlineData("empty", false)]
    [InlineData("empty", true)]
    [InlineData("reconcile", false)]
    [InlineData("reconcile", true)]
    [InlineData("index", false)]
    [InlineData("index", true)]
    public async Task BuildAsync_ReviewSparseExactAdmission_ShouldChargeOnlyAllocatedWork(string kind, bool sparse)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var storage = new CrashCaptureSqlStorageStrategy();
        var source = kind == "empty" ? SqlSchema.Create("typed", _ => { }) : ParityDeclaration(kind == "nullable" ? "missing" : "default", false);
        var target = kind is "empty" or "reconcile" ? source : ParityDeclaration(kind == "nullable" ? "missing" : "default", true, index: kind == "index");
        if (kind != "empty")
        {
            await using var seed = await Builder(storage).AddDatabase(source).BuildAsync(guard.Token);
            var db = await seed.OpenDatabaseAsync("typed", guard.Token);
            await using var writer = await db.CreateSessionAsync(guard.Token);
            await writer.ExecuteAsync(kind == "nullable" ? "INSERT INTO items VALUES(1,42)" : "INSERT INTO items VALUES(1,42,9)", cancellationToken: guard.Token);
            if (kind == "reconcile") { await writer.ExecuteAsync("CREATE TABLE unrelated(id INT)", cancellationToken: guard.Token); }
            db.Coordinator.Checkpoint(guard.Token); db.CatalogStorage.Checkpoint();
            storage = storage.CaptureDurableImages();
        }
        long need = 0;
        for (int admission = 0; admission < 3; admission++)
        {
            var image = storage.CaptureDurableImages(); image.UnixSparseGrowth = sparse;
            var builder = Builder(image).AddDatabase("typed", database => { database.Schema(target); database.RunMigrationsInline = kind == "index"; });
            if (admission != 0)
            {
                builder.AddWorker(owner =>
                {
                    owner.BeforeProvisioning = db => db.SchemaProvisioner.JointStage = stage =>
                    { if (stage == "Planning") { image.VolumeCapacityBytes = image.AllocatedBytes + need - (admission == 2 ? Page.Size : 0); } };
                    return new RecordingWorker(owner);
                });
            }
            // Act / Assert
            if (admission == 2)
            {
                var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await builder.BuildAsync(guard.Token));
                failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS03402");
                image.CapacityFailures.ShouldBe(0);
                long retryCapacity = image.VolumeCapacityBytes + Page.Size;
                var retryImage = image.CaptureDurableImages(); retryImage.UnixSparseGrowth = sparse;
                var retry = Builder(retryImage).AddDatabase("typed", database => { database.Schema(target); database.RunMigrationsInline = kind == "index"; });
                retry.AddWorker(owner =>
                {
                    owner.BeforeProvisioning = db => db.SchemaProvisioner.JointStage = stage =>
                    { if (stage == "Planning") { retryImage.VolumeCapacityBytes = retryCapacity; } };
                    return new RecordingWorker(owner);
                });
                await using var reopened = await retry.BuildAsync(guard.Token);
                var retried = await reopened.OpenDatabaseAsync("typed", guard.Token);
                await WaitDoneAsync(retried, guard.Token);
                retried.IsOffline.ShouldBeFalse(); retried.Provisioning.ShouldBeNull();
                retried.SchemaProvisioner.JointSpaceNeed.ShouldBe(need);
                retryImage.CapacityFailures.ShouldBe(0);
                _output.WriteLine($"refused Build retry {kind}, sparse={sparse}: added exactly {Page.Size} bytes; N stayed {need}; cap={retryCapacity}");
            }
            else
            {
                await using var engine = await builder.BuildAsync(guard.Token);
                var db = await engine.OpenDatabaseAsync("typed", guard.Token); await WaitDoneAsync(db, guard.Token);
                db.Provisioning.ShouldBeNull();
                if (admission == 0) { need = db.SchemaProvisioner.JointSpaceNeed; _output.WriteLine($"measured Build {kind}, sparse={sparse}: need={need}"); }
                db.SchemaProvisioner.JointSpaceNeed.ShouldBe(need);
                // The separate intent phase adds F; the existing bound still
                // applies to the publication/discard budget after preparation.
                if (kind != "index") { (need - 1_254_216).ShouldBeLessThan(5L * 1024 * 1024); }
                db.Catalog.SchemaState!.ContentHash.ShouldBe(target.Compile().Hash);
            }
        }
        _output.WriteLine($"review exact Build {kind}, sparse={sparse}: need={need}; one-page-under={need - Page.Size}");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Review fix: non-trailing drops preserve SQL column order, updates, missing values and a later rewrite (#1778)")]
    public async Task ApplyAsync_ReviewNonTrailingDropSequence_ShouldPreserveLiveOrdinalsAndFrozenMissingValues()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var seed = await Builder(storage).AddDatabase(ReviewDropDeclaration(true, false)).BuildAsync(guard.Token))
        {
            var db = await seed.OpenDatabaseAsync("typed", guard.Token);
            await using var session = await db.CreateSessionAsync(guard.Token);
            await session.ExecuteAsync("INSERT INTO items VALUES(1,999,11,42)", cancellationToken: guard.Token);
            storage = storage.CaptureDurableImages();
        }
        SqlCompiledSchema final;
        await using (var engine = await Builder(storage).AddDatabase(ReviewDropDeclaration(true, true)).BuildAsync(guard.Token))
        {
            var db = await engine.OpenDatabaseAsync("typed", guard.Token); await WaitDoneAsync(db, guard.Token);
            ulong owner = Table(db).ObjectId;
            await using var session = await db.CreateSessionAsync(guard.Token);
            // Act: UPDATE an old row with bytes at the dropped ordinal; INSERT without a list.
            await session.ExecuteAsync("UPDATE items SET Value=43 WHERE Id=1", cancellationToken: guard.Token);
            await session.ExecuteAsync("INSERT INTO items VALUES(2,12,44)", cancellationToken: guard.Token);
            await using (var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT * FROM items ORDER BY Id", cancellationToken: guard.Token))
            { result.Columns.Select(column => column.Name).ShouldBe(new[] { "Id", "Neighbour", "Value" }); }
            (await RowsAsync(session, "SELECT * FROM items ORDER BY Id", guard.Token)).Select(row => string.Join(',', row)).ShouldBe(new[] { "1,11,43", "2,12,44" });
            var shape = ReviewDropDeclaration(true, true).Compile().Tables.Single();
            var appended = new CompiledSchemaColumn("frozen", DatabaseType.Int32, true, DefaultLiteral: "7");
            SqlCompiledSchema Next(bool neighbour, int missing, bool rewrite = false) => Compiled(new CompiledSchemaTable(shape.Name, shape.RowType,
                shape.Columns.Where(column => neighbour || column.Name != "Neighbour").Select(column => column.Name == "Value"
                    ? column with { Type = rewrite ? DatabaseType.Int64 : DatabaseType.Decimal, Precision = rewrite ? null : 5, Scale = rewrite ? null : 0, CustomType = rewrite ? null : "DECIMAL(5,0)" } : column)
                    .Append(appended with { DefaultLiteral = missing.ToString(System.Globalization.CultureInfo.InvariantCulture) }).ToArray(), shape.PrimaryKey, shape.Indexes, shape.Constraints));
            await db.SchemaProvisioner.ApplyAsync(Next(true, 7), guard.Token);
            await db.SchemaProvisioner.ApplyAsync(Next(true, 9), guard.Token);
            (await RowsAsync(session, "SELECT Id FROM items WHERE Value=43", guard.Token)).Single()[0].ShouldBe(1);
            session.LastStatementMetrics!.AccessPath.ShouldBe("seek:IX_items_Value");
            await db.SchemaProvisioner.ApplyAsync(Next(false, 9), guard.Token);
            Table(db).ObjectId.ShouldBe(owner); Table(db).DroppedColumnOrdinals.ShouldBe(new[] { 1, 2 });
            await session.ExecuteAsync("INSERT INTO items VALUES(3,45,9)", cancellationToken: guard.Token);
            final = Next(false, 9, true);
            storage = storage.CaptureDurableImages();
        }
        await using var reopened = await Builder(storage).BuildAsync(guard.Token);
        var current = await reopened.OpenDatabaseAsync("typed", guard.Token);
        await using var reader = await current.CreateSessionAsync(guard.Token);
        // Assert: reopen reads frozen 7 even though today's insert default is 9.
        (await RowsAsync(reader, "SELECT frozen FROM items ORDER BY Id", guard.Token)).Select(row => row[0]).ShouldBe(new object?[] { 7, 7, 9 });
        await current.ApplySchemaAsync(final, guard.Token);
        Table(current).DroppedColumnOrdinals.ShouldBeEmpty();
        (await RowsAsync(reader, "SELECT * FROM items ORDER BY Id", guard.Token)).Select(row => string.Join(',', row)).ShouldBe(new[] { "1,43,7", "2,44,7", "3,45,9" });
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Review fix: a new-name admission does not retain object zero in an open user transaction (#1778)")]
    public async Task ApplyAsync_ReviewObjectZeroRendezvous_ShouldReleaseSyntheticGrantBeforeNextJob()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = await Builder(new()).BuildAsync(guard.Token);
        var db = await engine.CreateDatabaseAsync("typed", guard.Token);
        CompiledSchemaTable NewTable(string name) => new(name, name, [new("Id", DatabaseType.Int32, true)], null, [], []);
        await db.ApplySchemaAsync(Compiled(), guard.Token);
        await using var user = await db.CreateSessionAsync(guard.Token);
        await using var tx = await user.BeginTransactionAsync(guard.Token);
        db.SchemaProvisioner.JointStage = stage => { if (stage == "Publishing") { entered.TrySetResult(); release.Wait(guard.Token); } };
        var first = Task.Run(async () => await db.ApplySchemaAsync(Compiled(NewTable("new_one")), guard.Token), guard.Token);
        await entered.Task.WaitAsync(guard.Token);
        using var listener = new LockWaitListener(db.Name);
        listener.Observed = value => { if (value.EventName == "LockWaitStart") { waiting.TrySetResult(); } };
        try
        {
            var read = RowsAsync(user, "SELECT * FROM new_one", guard.Token);
            await waiting.Task.WaitAsync(guard.Token); read.IsCompleted.ShouldBeFalse();
            release.Set(); await first; (await read).ShouldBeEmpty();
            db.SchemaProvisioner.JointStage = null;
            waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // Act: the transaction remains open while a second unrelated new table publishes.
            var second = db.ApplySchemaAsync(Compiled(NewTable("new_one"), NewTable("new_two")), guard.Token).AsTask();
            (await Task.WhenAny(second, waiting.Task).WaitAsync(guard.Token)).ShouldBeSameAs(second);
            await second;
            // Assert
            db.Catalog.TryGetTable("dbo", "new_two", out _).ShouldBeTrue();
            await tx.CommitAsync(guard.Token);
        }
        finally { release.Set(); await first.WaitAsync(guard.Token); }
    }

    private sealed record ReviewDomainRow(int? Id);

    [Theory(DisplayName = "Cohesion Test [Sql] - Review fix: inherited and explicit NULL SQL-domain defaults reconcile across Builds (#1778)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildAsync_ReviewSqlDomainDefault_ShouldReconcileWithoutDestructiveDrift(bool explicitNull)
    {
        // Arrange: declared scripts install a SQL domain; tools apply the typed schema after Build.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = new CrashCaptureSqlStorageStrategy();
        var schema = SqlSchema.Create("typed", declaration => declaration.Table<ReviewDomainRow>("items", table =>
        { table.Column(row => row.Id, "review_domain"); if (explicitNull) { table.DefaultSql(row => row.Id, "NULL"); } }));
        for (int build = 0; build < 2; build++)
        {
            // Act
            await using var engine = await Builder(storage).AddDatabase("typed", database =>
            { database.Script("001", "CREATE DOMAIN review_domain AS INT DEFAULT 7"); }).BuildAsync(guard.Token);
            var db = await engine.OpenDatabaseAsync("typed", guard.Token);
            if (build > 0)
            {
                // Verify still reports the unrelated DDL generation change, without inventing default drift.
                var drift = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await db.VerifySchemaAsync(schema.Compile(), guard.Token));
                drift.Message.ShouldStartWith("CS03103:", Case.Sensitive);
                drift.Message.ShouldContain("DDL generation changed", Case.Sensitive);
                drift.Message.ShouldNotContain("different default", Case.Sensitive);
            }
            await db.ApplySchemaAsync(schema.Compile(), guard.Token);
            await db.VerifySchemaAsync(schema.Compile(), guard.Token);
            // Assert
            Table(db).FindColumn("Id")!.DefaultLiteral.ShouldBe(explicitNull ? "NULL" : "7");
            db.Catalog.SchemaState!.ContentHash.ShouldBe(schema.Compile().Hash);
            db.IsOffline.ShouldBeFalse(); db.Provisioning.ShouldBeNull();
            await using var session = await db.CreateSessionAsync(guard.Token);
            await session.ExecuteAsync("INSERT INTO items DEFAULT VALUES", cancellationToken: guard.Token);
            (await RowsAsync(session, "SELECT Id FROM items", guard.Token)).Select(row => row[0]).ShouldAllBe(value => explicitNull ? value == null : Equals(value, 7));
            if (build == 0) { await session.ExecuteAsync("CREATE TABLE unrelated(id INT)", cancellationToken: guard.Token); }
            storage = storage.CaptureDurableImages();
        }
    }
}
