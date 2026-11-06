using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Frozen stars and NATURAL keys protect columns in every joint apply (#1778, #1792)")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ApplyAsync_FrozenJoinView_ShouldRetainStatementDependencyCode(bool natural, bool deferred)
    {
        // Arrange: the NATURAL key is used by the join even when absent from the output.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = await Builder(new()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        var source = StarIntegrationSchema(false);
        await database.ApplySchemaAsync(source, guard.Token);
        await using var session = await database.CreateSessionAsync(guard.Token);
        await session.ExecuteAsync("CREATE TABLE u(legacy INT)", cancellationToken: guard.Token);
        string body = natural ? "SELECT t.id FROM items t NATURAL JOIN u" : "SELECT t.* FROM items t";
        await session.ExecuteAsync("CREATE VIEW frozen AS " + body, cancellationToken: guard.Token);
        var view = database.Catalog.Views.Single();
        view.Definition.ShouldNotContain("NATURAL", Case.Sensitive); view.Definition.ShouldNotContain(".*", Case.Sensitive);
        // Statement DDL has the same dependency on an ad-hoc copy of the shape.
        await session.ExecuteAsync("CREATE TABLE comparison(id INT,legacy INT)", cancellationToken: guard.Token);
        await session.ExecuteAsync("CREATE VIEW comparison_view AS " + body.Replace("items", "comparison", StringComparison.Ordinal), cancellationToken: guard.Token);
        var statement = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "ALTER TABLE comparison DROP COLUMN legacy", cancellationToken: guard.Token));
        // Act
        var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () =>
        {
            if (deferred) { await database.SchemaProvisioner.ApplyAsync(StarIntegrationSchema(true), guard.Token, deferDataScalingSteps: true); }
            else { await database.ApplySchemaAsync(StarIntegrationSchema(true), guard.Token); }
        });
        // Assert: the migration policy wrapper keeps the original dependency diagnostic.
        statement.Diagnostics.ShouldContain(d => d.Code == "CS03302");
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03105");
        Exception? cause = failure;
        while (cause is not null && (cause is not DatabaseException coded || !coded.Diagnostics.Any(d => d.Code == "CS03302"))) { cause = cause.InnerException; }
        cause.ShouldNotBeNull();
        Table(database).FindColumn("legacy").ShouldNotBeNull();
        database.Catalog.SchemaState!.ContentHash.ShouldBe(source.Hash);
        database.Catalog.PendingMigration.ShouldBeNull(); database.SchemaProvisioner.Deferred.ShouldBeNull();
        database.IsOffline.ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - A qualified star queued behind a joint drop binds the published shape (#1778, #1792)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyAsync_QueuedStarRead_ShouldBindPublishedColumns(bool deferred)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = await Builder(new()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await database.ApplySchemaAsync(StarIntegrationSchema(false), guard.Token);
        await using var session = await database.CreateSessionAsync(guard.Token);
        await session.ExecuteAsync("INSERT INTO items VALUES(1,9)", cancellationToken: guard.Token);
        database.SchemaProvisioner.JointStage = stage =>
        { if (stage == "OwnersDurable") { entered.TrySetResult(); release.Wait(guard.Token); } };
        using var listener = new LockWaitListener(database.Name);
        listener.Observed = value => { if (value.EventName == "LockWaitStart") { waiting.TrySetResult(); } };
        var applying = Task.Run(async () =>
        {
            if (deferred) { await database.SchemaProvisioner.ApplyAsync(StarIntegrationSchema(true), guard.Token, deferDataScalingSteps: true); }
            else { await database.ApplySchemaAsync(StarIntegrationSchema(true), guard.Token); }
        }, guard.Token);
        Task<Execution.DatabaseResult>? reading = null;
        try
        {
            await entered.Task.WaitAsync(guard.Token);
            // Act
            reading = session.ExecuteAsync("SELECT t.* FROM items t", cancellationToken: guard.Token).AsTask();
            (await Task.WhenAny(waiting.Task, reading).WaitAsync(guard.Token)).ShouldBeSameAs(waiting.Task);
            reading.IsCompleted.ShouldBeFalse();
        }
        finally { release.Set(); }
        await applying.WaitAsync(guard.Token); await WaitDoneAsync(database, guard.Token);
        await using var result = (await reading!.WaitAsync(guard.Token)).ShouldBeAssignableTo<Execution.DatabaseResultSet>();
        // Assert
        result.Columns.Select(c => c.Name).ShouldBe(["id"]);
        int count = 0;
        await foreach (var row in result.GetRowsAsync(guard.Token)) { row.GetValue(0).ShouldBe(1); count++; }
        count.ShouldBe(1); Table(database).FindColumn("legacy").ShouldBeNull();
    }

    private static SqlCompiledSchema StarIntegrationSchema(bool drop) => Compiled(new CompiledSchemaTable("items", "row",
        drop ? [new("id", DatabaseType.Int32, false)] : [new("id", DatabaseType.Int32, false), new("legacy", DatabaseType.Int32, true)], null, [], []));
}
