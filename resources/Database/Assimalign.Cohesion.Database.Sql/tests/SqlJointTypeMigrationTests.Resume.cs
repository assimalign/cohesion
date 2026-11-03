using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Prepared publication refuses a heap-owned index collision before commit (#1777)")]
    [InlineData("ix_v")]
    [InlineData("IX_V")]
    public async Task PreparePublication_HeapOwnedIndexCollision_ShouldRefuseAndKeepTree(string preparedName)
    {
        // Arrange.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = await Builder(new CrashCaptureSqlStorageStrategy()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await database.ApplySchemaAsync(OwnerDeclaration(false, indexes: [new("ix_v", ["v"], false)]), guard.Token);
        var heap = Table(database);
        database.IndexManager.TryGetIndex(heap.ObjectId, "ix_v", out var original).ShouldBeTrue();
        ulong owner = database.Catalog.ReserveJointTreeOwner(heap, guard.Token);
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, guard.Token);
        try
        {
            await database.Coordinator.ApplyStatementAsync(transaction, async _ =>
                await database.IndexManager.CreatePreparedAsync(transaction, heap.ObjectId, owner,
                    new IndexDefinition(preparedName, IndexKind.BTree, false), guard.Token), cancellationToken: guard.Token);

            // Act.
            var failure = Should.Throw<InvalidOperationException>(() => database.IndexManager.PreparePublication());

            // Assert.
            failure.Message.ShouldContain("heap-owned tree", Case.Sensitive);
            database.IndexManager.TryGetIndex(heap.ObjectId, "ix_v", out var current).ShouldBeTrue();
            current.ShouldBeSameAs(original);
            database.IsOffline.ShouldBeFalse();
        }
        finally
        {
            database.IndexManager.DiscardPrepared();
            database.Catalog.AbandonJointTreeOwners([owner]);
            await database.Coordinator.RollbackAsync(transaction, CancellationToken.None);
        }
        await DrainReclamationAsync(database, guard.Token);
        database.DataStorage.HasOwnerPages(owner).ShouldBeFalse();
        database.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Malformed joint choices retain CS06059 instead of being discarded (#1777)")]
    [InlineData("Missing")]
    [InlineData("WrongName")]
    [InlineData("Unknown")]
    public async Task ApplySchemaAsync_MalformedJointChoices_ShouldKeepCorruptionRefusal(string malformed)
    {
        // Arrange.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = await Builder(new CrashCaptureSqlStorageStrategy()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        var source = OwnerDeclaration(false);
        var target = OwnerDeclaration(true);
        await database.ApplySchemaAsync(source, guard.Token);
        SqlCatalogMigrationStep[] choices = malformed == "Missing"
            ? [new("Joint/RewriteOwner", "rewrite", "rewrite", 1)]
            : [new("Joint/RewriteOwner", "rewrite", "rewrite", 1),
                new(malformed == "Unknown" ? "Joint/Unknown" : "Joint/KeepOwner",
                    malformed == "WrongName" ? "missing" : "items", "items", 2)];
        var intent = new SqlCatalogPendingMigration(target.Hash, target.CanonicalDocument, choices, choices.Length);
        await database.Catalog.SavePendingMigrationAsync(intent, guard.Token);

        // Act.
        var failure = await Should.ThrowAsync<SqlCatalogException>(async () => await database.ApplySchemaAsync(target, guard.Token));

        // Assert.
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS06059");
        database.Catalog.PendingMigration.ShouldBeSameAs(intent);
        database.Catalog.SchemaState!.ContentHash.ShouldBe(source.Hash);
        database.IsOffline.ShouldBeFalse();
    }

    private sealed record OptionalDomainItem(decimal? V, decimal? W, decimal? X);
    private sealed record RequiredDomainItem(decimal V, decimal? W, decimal? X);

    [Theory(DisplayName = "Cohesion Test [Sql] - Same-hash ordinary and joint intents replan after domain registration changes (#1777)")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BuildAsync_DomainMigrationKindChanged_ShouldReplanAndKeepRows(bool initiallyJoint, bool staleSnapshot)
    {
        // Arrange: NOT NULL is ordinary at scale 2 and joint at scale 1.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var release = new ManualResetEventSlim();
        var captured = new TaskCompletionSource<CrashCaptureSqlStorageStrategy>(TaskCreationOptions.RunContinuationsAsynchronously);
        int recordedSteps = 0;
        SqlSchema Declaration(bool changed) => SqlSchema.Create("typed", schema =>
        {
            schema.AllowDestructiveChanges();
            if (changed) { schema.Table<RequiredDomainItem>("items", table => { table.Column(row => row.V, "native_value"); table.Column(row => row.W, "native_value"); table.Column(row => row.X, "native_value"); }); }
            else { schema.Table<OptionalDomainItem>("items", table => { table.Column(row => row.V, "native_value"); table.Column(row => row.W, "native_value"); table.Column(row => row.X, "native_value"); }); }
        });
        var targetDeclaration = Declaration(true);
        var target = targetDeclaration.Compile();
        var storage = new CrashCaptureSqlStorageStrategy();
        var seedBuilder = Builder(storage);
        seedBuilder.Types.Add(SqlType.CreateDomain("native_value", SqlType.Decimal(8, 2)));
        await using (var seed = await seedBuilder.BuildAsync(guard.Token))
        {
            var database = await seed.CreateDatabaseAsync("typed", guard.Token);
            await database.ApplySchemaAsync(Declaration(false).Compile(), guard.Token);
            await using var writer = await database.CreateSessionAsync(guard.Token);
            await writer.ExecuteAsync("INSERT INTO items VALUES(12,13,14)", cancellationToken: guard.Token);
            storage = storage.CaptureDurableImages();
        }
        var first = Builder(storage);
        first.Types.Add(SqlType.CreateDomain("native_value", SqlType.Decimal(8, initiallyJoint ? 1 : 2)));
        await using (var engine = await first.BuildAsync(guard.Token))
        {
            var database = await engine.OpenDatabaseAsync("typed", guard.Token);
            database.SchemaProvisioner.BeforeDeferredStep = (_, token) =>
            {
                var intent = database.Catalog.PendingMigration.ShouldNotBeNull();
                intent.Steps.All(step => step.Kind.StartsWith("Joint/", StringComparison.Ordinal)).ShouldBe(initiallyJoint);
                recordedSteps = intent.StepCount;
                captured.TrySetResult(storage.CaptureDurableImages());
                release.Wait(token);
            };
            try
            {
                await database.SchemaProvisioner.ApplyAsync(target, guard.Token, deferDataScalingSteps: true);
                var image = await captured.Task.WaitAsync(guard.Token);
                if (staleSnapshot)
                {
                    // Unrelated DDL is legal while the worker holds only its table locks.
                    // It makes resume reconstruct every domain column from the live catalog.
                    await using var unrelated = await database.CreateSessionAsync(guard.Token);
                    await unrelated.ExecuteAsync("CREATE TABLE unrelated(id INT)", cancellationToken: guard.Token);
                    image = storage.CaptureDurableImages();
                }
                storage = image;
            }
            finally { release.Set(); }
            await WaitDoneAsync(database, guard.Token);
        }

        // Act: Build opens the crash image with the same target and opposite route.
        var resumedBuilder = Builder(storage);
        resumedBuilder.Types.Add(SqlType.CreateDomain("native_value", SqlType.Decimal(staleSnapshot ? 10 : 8, initiallyJoint ? 2 : 1)));
        resumedBuilder.AddDatabase("typed", declaration => { declaration.Schema(targetDeclaration); declaration.RunMigrationsInline = !initiallyJoint; });
        using var recorder = new Assimalign.Cohesion.Testing.EventSourceRecorder(SqlDatabaseEventSource.Log, System.Diagnostics.Tracing.EventLevel.Informational);
        await using var resumed = await resumedBuilder.BuildAsync(guard.Token);
        var current = await resumed.OpenDatabaseAsync("typed", guard.Token);
        await WaitDoneAsync(current, guard.Token);
        await DrainReclamationAsync(current, guard.Token);

        // Assert.
        if (!initiallyJoint || staleSnapshot)
        {
            recorder.Events.ShouldContain(e => e.EventId == 14 && Equals(e.Payload![5], "Restarted"));
            recorder.Events.ShouldContain(e => e.EventId == 13 && Equals(e.Payload![4], "Replan"));
        }
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
        current.OfflineError.ShouldBeNull();
        current.Catalog.PendingMigration.ShouldBeNull();
        current.Catalog.SchemaState!.ContentHash.ShouldBe(target.Hash);
        Table(current).Columns[0].IsNullable.ShouldBeFalse();
        Table(current).Columns[0].Type.Scale.ShouldBe(initiallyJoint ? 2 : 1);
        Table(current).Columns[0].Type.Precision.ShouldBe(staleSnapshot ? 10 : 8);
        if (staleSnapshot) { current.SchemaProvisioner.LastPlan!.Operations.Count.ShouldBeGreaterThan(recordedSteps); }
        current.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
        await using var reader = await current.CreateSessionAsync(guard.Token);
        (await RowsAsync(reader, "SELECT v,w,x FROM items", guard.Token)).Single().ShouldBe(new object?[] { 12m, 13m, 14m });
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - A post-commit publication fault fences storage and leaves durable state to recovery (#1777)")]
    public async Task PublishStorageReplacementAsync_PostCommitFault_ShouldFenceAndRecover()
    {
        // Arrange: the publication callback runs after the catalog bracket commits.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = new CrashCaptureSqlStorageStrategy();
        CrashCaptureSqlStorageStrategy? crash = null;
        await using (var engine = await Builder(storage).BuildAsync(guard.Token))
        {
            var database = await engine.CreateDatabaseAsync("typed", guard.Token);
            await database.ApplySchemaAsync(OwnerDeclaration(false), guard.Token);
            var old = Table(database);
            await using var writer = await database.CreateSessionAsync(guard.Token);
            await writer.ExecuteAsync("INSERT INTO items VALUES(7,8)", cancellationToken: guard.Token);
            var injected = new InvalidOperationException("injected after publication commit");

            // Act.
            var failure = await Record.ExceptionAsync(async () => await database.Catalog.PublishStorageReplacementAsync(
                [], null, [], [], database.IndexManager.ExportRegistrations(), new(), false, TransactionSequence.None,
                string.Empty, null, guard.Token, kept: [old], expectedKept: [old], publishTrees: () =>
                {
                    crash = storage.CaptureDurableImages();
                    throw injected;
                }));

            // Assert: the executor recognizes this outcome and must leave cleanup to recovery.
            failure.ShouldNotBeNull();
            SqlSchemaProvisioner.IsCommitUnconfirmed(failure).ShouldBeTrue();
            database.CatalogStorage.IsOffline.ShouldBeTrue();
            database.DataStorage.IsOffline.ShouldBeTrue();
            failure.InnerException.ShouldBeSameAs(injected);
        }
        await using var reopened = await Builder(crash!).BuildAsync(guard.Token);
        var current = await reopened.OpenDatabaseAsync("typed", guard.Token);
        await using var reader = await current.CreateSessionAsync(guard.Token);
        (await RowsAsync(reader, "SELECT v,w FROM items", guard.Token)).Single().ShouldBe(new object?[] { 7, 8 });
        current.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
    }
}
