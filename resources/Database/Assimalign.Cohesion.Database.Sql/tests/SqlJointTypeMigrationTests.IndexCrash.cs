using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Unpublished index root and chunk crash images reclaim exact owners on two reopens (#1808)")]
    [InlineData("root-committed")]
    [InlineData("chunk-before-commit")]
    [InlineData("chunk-committed")]
    public async Task OpenAsync_UnpublishedIndexBoundary_ShouldReclaimOwnerAndKeepHeap(string boundary)
    {
        // Arrange.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var storage = new CrashCaptureSqlStorageStrategy();
        await using var engine = await Builder(storage).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync("CREATE TABLE items(v INT)", cancellationToken: guard.Token);
        SeedRaw(database, [1], 130);
        CrashCaptureSqlStorageStrategy? image = null;
        ulong owner = 0;
        SqlPlanExecutor.IndexBuildBoundary = (stage, _, _) =>
        {
            if (stage != boundary || image is not null) { return; }
            var registration = database.IndexManager.ExportRegistrations().Single();
            owner = database.IndexManager.PageOwner(registration);
            database.DataStorage.WriteBackDirtyPages(int.MaxValue);
            database.DataStorage.WriteAheadJournal.Flush(forceDurable: true);
            image = storage.CaptureDurableImages();
            throw new InvalidOperationException("injected build boundary");
        };

        // Act.
        try { await Should.ThrowAsync<InvalidOperationException>(async () => await writer.ExecuteAsync("CREATE INDEX ix ON items(v)", cancellationToken: guard.Token)); }
        finally { SqlPlanExecutor.IndexBuildBoundary = null; }

        // Assert: the intent is discoverable after restart and no live tree is lost.
        image.ShouldNotBeNull();
        for (int reopen = 0; reopen < 2; reopen++)
        {
            var reopenedStorage = image.CaptureDurableImages();
            await using var recovered = await Builder(reopenedStorage).BuildAsync(guard.Token);
            var current = await recovered.OpenDatabaseAsync("typed", guard.Token);
            await DrainReclamationAsync(current, guard.Token);
            current.DataStorage.GetOwnerIndexPages(owner).ShouldBeEmpty();
            current.Catalog.GetIndexRegistrations().ShouldBeEmpty();
            current.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
            current.IndexManager.ExportRegistrations().ShouldBeEmpty();
            await using var reader = await current.CreateSessionAsync(guard.Token);
            (await RowsAsync(reader, "SELECT COUNT(*) FROM items", guard.Token)).Single()[0].ShouldBe(130L);
            image = reopenedStorage.CaptureDurableImages();
        }
    }
}
