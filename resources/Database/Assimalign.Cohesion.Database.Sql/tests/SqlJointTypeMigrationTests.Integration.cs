using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Joint tree reservation waits for catalog admission and canceled waits write nothing (#1283, #1777)")]
    public async Task ReserveJointTreeOwner_CatalogAdmissionCanceled_ShouldLeaveNoBracketOrIntent()
    {
        // Arrange.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(guard.Token);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var engine = await Builder(new CrashCaptureSqlStorageStrategy()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await database.ApplySchemaAsync(OwnerDeclaration(false), guard.Token);
        var table = Table(database);
        ulong next = database.Catalog.NextReservedIdentity;
        long lsn = database.CatalogStorage.WriteAheadJournal.LastLsn;
        var holder = Task.Factory.StartNew(() =>
        {
            using var admission = TransactionCheckpointInterlock.ForStorage(database.CatalogStorage).Enter("Test", cancellationToken: guard.Token);
            entered.Set();
            release.Wait(guard.Token);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        entered.Wait(guard.Token);
        var reserving = new SqlBindingCaller(() =>
        {
            database.Catalog.ReserveJointTreeOwner(table, cancel.Token);
            return Task.CompletedTask;
        });
        try
        {
            // Act.
            (await reserving.WaitUntilBlockedAsync()).ShouldBeTrue();
            cancel.Cancel();
            (await reserving.JoinAsync()).ShouldBeAssignableTo<OperationCanceledException>();
        }
        finally
        {
            release.Set();
            await holder.WaitAsync(guard.Token);
            await reserving.JoinAsync();
        }

        // Assert.
        database.CatalogStorage.WriteAheadJournal.LastLsn.ShouldBe(lsn);
        database.CatalogStorage.HasActiveTransactions.ShouldBeFalse();
        database.Catalog.NextReservedIdentity.ShouldBe(next);
        database.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
    }

    private sealed record DomainFlipItem(string V);

    [Theory(DisplayName = "Cohesion Test [Sql] - Matching interrupted joint intent replans after a domain-only classification flip (#1777)")]
    [InlineData(false, "OwnersDurable")]
    [InlineData(true, "AfterTreeBuild")]
    public async Task BuildAsync_DomainClassificationChanged_ShouldDiscardIntentAndReplan(bool initiallyKeeps, string interruptedStage)
    {
        // Arrange: the schema hash excludes the registered domain's VARCHAR facet.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        SqlSchema Declaration(bool changed) => SqlSchema.Create("typed", schema =>
        {
            schema.AllowDestructiveChanges();
            if (changed) { schema.Table<OwnerWide>("rewrite", table => table.Column(row => row.Value)); }
            else { schema.Table<OwnerNarrow>("rewrite", table => table.Column(row => row.Value)); }
            schema.Table<DomainFlipItem>("items", table =>
            {
                table.Column(row => row.V, "native_value");
                if (changed) { table.Index(row => row.V); }
            });
        });
        var source = Declaration(false).Compile();
        var targetDeclaration = Declaration(true);
        var target = targetDeclaration.Compile();
        var storage = new CrashCaptureSqlStorageStrategy();
        var seedBuilder = Builder(storage);
        seedBuilder.Types.Add(SqlType.CreateDomain("native_value", SqlType.VarChar(8)));
        ulong heap;
        await using (var seed = await seedBuilder.BuildAsync(guard.Token))
        {
            var database = await seed.CreateDatabaseAsync("typed", guard.Token);
            await database.ApplySchemaAsync(source, guard.Token);
            heap = Table(database).ObjectId;
            await using var session = await database.CreateSessionAsync(guard.Token);
            await session.ExecuteAsync("INSERT INTO items VALUES('ab')", cancellationToken: guard.Token);
            storage = storage.CaptureDurableImages();
        }
        var first = Builder(storage);
        first.Types.Add(SqlType.CreateDomain("native_value", SqlType.VarChar(initiallyKeeps ? 8 : 4)));
        CrashCaptureSqlStorageStrategy? crash = null;
        ulong[] abandonedOwners = [];
        ulong firstPreparedOwner = 0;
        await using (var engine = await first.BuildAsync(guard.Token))
        {
            var database = await engine.OpenDatabaseAsync("typed", guard.Token);
            firstPreparedOwner = database.Catalog.NextReservedIdentity;
            database.SchemaProvisioner.JointStage = stage =>
            {
                if (stage != interruptedStage) { return; }
                database.Catalog.PendingMigration!.Steps.Single(step => step.Table == "items" && step.Kind is "Joint/KeepOwner" or "Joint/RewriteOwner").Kind
                    .ShouldBe(initiallyKeeps ? "Joint/KeepOwner" : "Joint/RewriteOwner");
                abandonedOwners = Enumerable.Range(0, checked((int)(database.Catalog.NextReservedIdentity - firstPreparedOwner))).Select(offset => firstPreparedOwner + (ulong)offset).ToArray();
                abandonedOwners.ShouldNotBeEmpty();
                crash = storage.CaptureDurableImages();
                throw new InvalidOperationException("interrupted after owner or tree preparation");
            };
            await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await database.ApplySchemaAsync(target, guard.Token));
        }

        // Act: reopen with the identical target hash and a different domain registration.
        var resumedBuilder = Builder(crash!);
        resumedBuilder.Types.Add(SqlType.CreateDomain("native_value", SqlType.VarChar(initiallyKeeps ? 4 : 8)));
        resumedBuilder.AddDatabase("typed", declaration => { declaration.Schema(targetDeclaration); declaration.RunMigrationsInline = true; });
        using var recorder = new Assimalign.Cohesion.Testing.EventSourceRecorder(SqlDatabaseEventSource.Log, System.Diagnostics.Tracing.EventLevel.Informational);
        await using var resumed = await resumedBuilder.BuildAsync(guard.Token);
        var current = await resumed.OpenDatabaseAsync("typed", guard.Token);

        await DrainReclamationAsync(current, guard.Token);

        // Assert: the old schema survived until a fresh plan could publish.
        recorder.Events.ShouldContain(e => e.EventId == 14 && Equals(e.Payload![5], "Restarted"));
        recorder.Events.ShouldContain(e => e.EventId == 13 && Equals(e.Payload![4], "Replan"));
        recorder.Events.ShouldContain(e => e.EventId == 14 && Equals(e.Payload![5], "Applied") && Equals(e.Payload[4], 2));
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
        foreach (ulong oldOwner in abandonedOwners) { current.DataStorage.HasOwnerPages(oldOwner).ShouldBeFalse(); }
        current.DataStorage.HasOwnerPages(heap).ShouldBe(!initiallyKeeps);
        current.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
        current.Catalog.PendingMigration.ShouldBeNull();
        current.Catalog.SchemaState!.ContentHash.ShouldBe(target.Hash);
        current.OfflineError.ShouldBeNull();
        (Table(current).ObjectId == heap).ShouldBe(!initiallyKeeps);
        Table(current).Columns[0].Type.MaxLength.ShouldBe(initiallyKeeps ? 4 : 8);
        await using var reader = await current.CreateSessionAsync(guard.Token);
        (await RowsAsync(reader, "SELECT v FROM items", guard.Token)).Single()[0].ShouldBe("ab");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint kept columns publish declared case and keep the heap (#1777)")]
    public async Task ApplyAsync_KeptColumnCase_ShouldPublishDeclaredSpelling()
    {
        // Arrange.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = await Builder(new CrashCaptureSqlStorageStrategy()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await database.ApplySchemaAsync(OwnerDeclaration(false), guard.Token);
        ulong heap = Table(database).ObjectId;
        var target = ChangeItems(OwnerDeclaration(true), columns:
            [new("V", DatabaseType.Int32, true), new("W", DatabaseType.Int32, true)]);

        // Act.
        await database.ApplySchemaAsync(target, guard.Token);

        // Assert.
        Table(database).ObjectId.ShouldBe(heap);
        Table(database).Columns.Select(column => column.Name).ShouldBe(["V", "W"]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Kept publication refuses mismatched physical identity or type (#1777)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishStorageReplacementAsync_InvalidKeptShape_ShouldRefuseBeforeMarkers(bool identity)
    {
        // Arrange.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = await Builder(new CrashCaptureSqlStorageStrategy()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await database.ApplySchemaAsync(OwnerDeclaration(false), guard.Token);
        var old = Table(database);
        var columns = old.Columns.ToArray();
        if (!identity) { columns[0] = new SqlCatalogColumn(columns[0].Name, new DatabaseTypeInfo(DatabaseType.Int64), true); }
        var invalid = new SqlCatalogTable(identity ? old.ObjectId + 100 : old.ObjectId, old.Schema, old.Name,
            columns, old.PrimaryKeyColumns, old.Owner, old.OwningSchema, old.Constraints, old.DroppedColumnOrdinals);

        // Act.
        var failure = await Should.ThrowAsync<SqlCatalogException>(async () => await database.Catalog.PublishStorageReplacementAsync(
            [], null, [], [], database.IndexManager.ExportRegistrations(), new(), false, TransactionSequence.None,
            string.Empty, null, guard.Token, kept: [invalid], expectedKept: [old]));

        // Assert.
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS05060");
        failure.Message.ShouldContain(identity ? "ObjectId" : "Columns[v].Type", Case.Sensitive);
        Table(database).ShouldBeSameAs(old);
        database.Catalog.IsTruncatedTable(old).ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Canceled admission and failed physical publication leave no kept-read marker (#1283, #1777)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishStorageReplacementAsync_FailedBeforeCommit_ShouldKeepReadFastPath(bool cancelAdmission)
    {
        // Arrange.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(guard.Token);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var engine = await Builder(new CrashCaptureSqlStorageStrategy()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await database.ApplySchemaAsync(OwnerDeclaration(false), guard.Token);
        var old = Table(database);
        await using var reader = await database.CreateSessionAsync(guard.Token);
        await reader.ExecuteAsync("INSERT INTO items VALUES(7,8)", cancellationToken: guard.Token);
        await reader.ExecuteAsync("INSERT INTO rewrite VALUES(1)", cancellationToken: guard.Token);
        Task? holder = null;
        SqlBindingCaller? publishing = null;
        try
        {
            if (cancelAdmission)
            {
                holder = Task.Factory.StartNew(() =>
                {
                    using var lease = TransactionCheckpointInterlock.ForStorage(database.CatalogStorage).Enter("Test", cancellationToken: guard.Token);
                    entered.Set();
                    release.Wait(guard.Token);
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                entered.Wait(guard.Token);
            }
            publishing = new SqlBindingCaller(async () => await database.Catalog.PublishStorageReplacementAsync(
                [], null, [], [], database.IndexManager.ExportRegistrations(), new(), false, TransactionSequence.None,
                string.Empty, null, cancel.Token, kept: [old], expectedKept: [old],
                // The missing intent fails inside the real bracket, after table upsert
                // and marker preparation. Disposal rolls those physical writes back.
                treeOwners: cancelAdmission ? [] : [ulong.MaxValue]));

            // Act.
            if (cancelAdmission)
            {
                (await publishing.WaitUntilBlockedAsync()).ShouldBeTrue();
                cancel.Cancel();
            }
            var failure = await publishing.JoinAsync();

            // Assert.
            if (cancelAdmission) { failure.ShouldBeAssignableTo<OperationCanceledException>(); }
            else { failure.ShouldBeOfType<KeyNotFoundException>(); }
        }
        finally
        {
            release.Set();
            if (holder is not null) { await holder.WaitAsync(guard.Token); }
            if (publishing is not null) { await publishing.JoinAsync(); }
        }
        database.CatalogStorage.HasActiveTransactions.ShouldBeFalse();
        Table(database).ShouldBeSameAs(old);
        database.Catalog.IsTruncatedTable(old).ShouldBeFalse();
        (await RowsAsync(reader, "SELECT i.v,r.v FROM items i CROSS JOIN rewrite r", guard.Token))
            .Single().ShouldBe(new object?[] { 7, 1 });
    }
}
