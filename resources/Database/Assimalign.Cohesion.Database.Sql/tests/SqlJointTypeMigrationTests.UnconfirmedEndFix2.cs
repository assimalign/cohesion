using System;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Testing;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Unconfirmed joint publication ends every prepared index build once (#1808)")]
    public async Task ApplyAsync_UnconfirmedPreparedReplacement_ShouldEndEveryBuildOnce()
    {
        // Arrange: the catalog commit is durable, but its acknowledgement fails.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var strategy = new CrashCaptureSqlStorageStrategy(StorageCommitDurability.Grouped);
        await using var engine = await Builder(strategy).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        SqlCompiledSchema Declare(bool target) => Compiled(new CompiledSchemaTable("items", "row",
            [new("v", DatabaseType.Int32, false), new("w", DatabaseType.Int32, true)], new("pk", ["v"]),
            [new("ix", target ? ["w", "v"] : ["w"], false)], []));
        await database.ApplySchemaAsync(Declare(false), guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync("INSERT INTO items VALUES(1,1)", cancellationToken: guard.Token);
        using var events = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Informational);
        database.SchemaProvisioner.JointStage = stage =>
        {
            if (stage != "Publishing") { return; }
            database.CatalogStorage.OnCommitPending = () =>
            {
                database.CatalogStorage.FlushPendingCommits();
                throw new NotSupportedException("injected unconfirmed index publication");
            };
        };

        // Act.
        var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () =>
            await database.ApplySchemaAsync(Declare(true), guard.Token));
        database.SchemaProvisioner.JointStage = null;

        // Assert: the failed attempt ends without abandoning possibly published trees.
        failure.Diagnostics.ShouldContain(item => item.Code == "CS03403");
        database.IsOffline.ShouldBeTrue();
        var began = events.Events.Where(item => item.EventId == 28).ToArray();
        began.ShouldNotBeEmpty();
        foreach (var start in began)
        {
            var ended = events.Events.Where(item => item.EventId == 29 && Equals(item.Payload![2], start.Payload![2])).ShouldHaveSingleItem();
            ended.Payload![3].ShouldBe(false);
            database.DataStorage.GetOwnerIndexPageCount(unchecked((ulong)(long)start.Payload![2]!)).ShouldBeGreaterThan(0);
        }
        await using var recovered = await Builder(strategy.CaptureDurableImages()).BuildAsync(guard.Token);
        var reopened = await recovered.OpenDatabaseAsync("typed", guard.Token);
        reopened.Catalog.SchemaState!.ContentHash.ShouldBe(Declare(true).Hash);
        await using var reader = await reopened.CreateSessionAsync(guard.Token);
        (await RowsAsync(reader, "SELECT v,w FROM items", guard.Token)).Single().ShouldBe(new object?[] { 1, 1 });
    }
}
