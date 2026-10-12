using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlDeferredProvisioningTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - 49a: ordered additions survive deferral and every committed-step crash")]
    public async Task BuildAsync_RequiredThenOptionalColumn_ShouldPreserveOrderAcrossRestart()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var desired = SqlSchema.Create("pending", schema =>
        {
            schema.AllowDestructiveChanges();
            schema.Table<OrderedItem>("items", table =>
            {
                table.Key(row => row.Id);
                table.Column(row => row.Required);
                table.Default(row => row.Required, 0);
                table.Column(row => row.Optional);
            });
        });
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var seed = await Builder(storage).BuildAsync(timeout.Token))
        {
            var database = await seed.CreateDatabaseAsync("pending", timeout.Token);
            await database.ApplySchemaAsync(SqlSchema.Create("pending", schema =>
                schema.Table<OrderedItem>("items", table => table.Key(row => row.Id))).Compile(), timeout.Token);
            await using var session = await database.CreateSessionAsync(timeout.Token);
            await session.ExecuteAsync("INSERT INTO items (Id) VALUES (1)", cancellationToken: timeout.Token);
            storage = storage.CaptureDurableImages();
        }
        var crashes = new List<CrashCaptureSqlStorageStrategy>();
        var builder = Builder(storage);
        builder.AddWorker(engine =>
        {
            engine.BeforeProvisioning = database => database.SchemaProvisioner.StepApplied = _ =>
                crashes.Add(storage.CaptureDurableImages());
            return new RecordingWorker(engine);
        });

        // Act
        await using (var engine = await builder.AddDatabase(desired).BuildAsync(timeout.Token))
        {
            var database = await engine.OpenDatabaseAsync("pending", timeout.Token);
            await WaitDoneAsync(database, timeout.Token);
            database.Catalog.TryGetTable("dbo", "items", out var items).ShouldBeTrue();
            items.Columns.Select(column => column.Name).ShouldBe(["Id", "Required", "Optional"]);
        }

        // Assert: each image retains the target without a repeated declaration.
        crashes.Count.ShouldBe(2);
        foreach (var image in crashes)
        {
            await using var engine = await Builder(image).AddDatabase("pending").BuildAsync(timeout.Token);
            var database = await engine.OpenDatabaseAsync("pending", timeout.Token);
            await WaitDoneAsync(database, timeout.Token);
            database.Catalog.TryGetTable("dbo", "items", out var items).ShouldBeTrue();
            items.Columns.Select(column => column.Name).ShouldBe(["Id", "Required", "Optional"]);
            database.Catalog.SchemaState.ShouldNotBeNull().ContentHash.ShouldBe(desired.Compile().Hash);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - 49a: the first cheap commit already retains the complete migration intent")]
    public async Task BuildAsync_CrashAfterCheapColumn_ShouldResumeWithoutDeclaration()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var desired = Desired();
        var storage = await SeedAsync(new CrashCaptureSqlStorageStrategy(), null, desired, timeout.Token);
        CrashCaptureSqlStorageStrategy? crash = null;
        int captures = 0;
        var builder = Builder(storage);
        builder.AddWorker(engine =>
        {
            engine.BeforeProvisioning = database => database.SchemaProvisioner.StepApplied = step =>
            {
                if (step.Operation.Kind == SqlSchemaMigrationOperationKind.AddColumn)
                {
                    crash = storage.CaptureDurableImages();
                    Interlocked.Increment(ref captures);
                }
            };
            return new RecordingWorker(engine);
        });
        await using (var engine = await builder.AddDatabase(desired).BuildAsync(timeout.Token))
        {
            await WaitDoneAsync(await engine.OpenDatabaseAsync("pending", timeout.Token), timeout.Token);
        }

        // Act: inspect the restart before its worker can complete the remaining steps.
        using var release = new ManualResetEventSlim();
        var restarted = Builder(crash.ShouldNotBeNull());
        restarted.AddWorker(engine =>
        {
            engine.BeforeProvisioning = database => database.SchemaProvisioner.BeforeDeferredStep = (_, token) => release.Wait(token);
            return new RecordingWorker(engine);
        });
        await using var recovered = await restarted.AddDatabase("pending").BuildAsync(timeout.Token);
        var pending = await recovered.OpenDatabaseAsync("pending", timeout.Token);
        try
        {
            // Assert
            captures.ShouldBe(1);
            pending.Catalog.TryGetTable("dbo", "items", out var items).ShouldBeTrue();
            items.FindColumn("Note").ShouldNotBeNull();
            pending.Catalog.PendingMigration.ShouldNotBeNull().ContentHash.ShouldBe(desired.Compile().Hash);
            pending.Provisioning.ShouldNotBeNull();
        }
        finally
        {
            release.Set();
        }
        await WaitDoneAsync(pending, timeout.Token);
        pending.Catalog.SchemaState.ShouldNotBeNull().ContentHash.ShouldBe(desired.Compile().Hash);
        pending.Catalog.PendingMigration.ShouldBeNull();
        pending.Catalog.TryGetTable("dbo", "items", out var completed).ShouldBeTrue();
        completed.FindColumn("Value").ShouldNotBeNull().IsNullable.ShouldBeFalse();
        completed.Constraints.ShouldContain(constraint => constraint.Name == "ck_value");
        pending.Catalog.GetIndexes(completed.ObjectId).Count.ShouldBe(2);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - 49a: foreign-key DDL waits for its deferred parent key before binding")]
    [InlineData("CREATE TABLE child (Value INT, CONSTRAINT fk FOREIGN KEY (Value) REFERENCES parent(Value))")]
    [InlineData("CREATE TABLE child (Value INT REFERENCES parent(Value))")]
    [InlineData("ALTER TABLE child ADD CONSTRAINT fk FOREIGN KEY (Value) REFERENCES parent(Value)")]
    [InlineData("ALTER TABLE child ADD COLUMN ParentValue INT REFERENCES parent(Value)")]
    public async Task ExecuteAsync_ForeignKeyToDeferredParent_ShouldBindCompletedUniqueIndex(string command)
    {
        // Arrange: keep child outside the schema so its DDL is independently admissible.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var storage = new CrashCaptureSqlStorageStrategy();
        await using var engine = await Builder(storage).BuildAsync(timeout.Token);
        var database = await engine.CreateDatabaseAsync("pending", timeout.Token);
        var parent = new CompiledSchemaTable("parent", "Parent", [new("Value", DatabaseType.Int32, false)], null, [], []);
        var source = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "pending", true, [], [parent], []);
        await database.ApplySchemaAsync(source, timeout.Token);
        await using var seed = await database.CreateSessionAsync(timeout.Token);
        await seed.ExecuteAsync("INSERT INTO parent (Value) VALUES (1)", cancellationToken: timeout.Token);
        if (command.StartsWith("ALTER", StringComparison.Ordinal))
        {
            await seed.ExecuteAsync("CREATE TABLE child (Value INT)", cancellationToken: timeout.Token);
        }
        var desired = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "pending", true, [],
            [new CompiledSchemaTable(parent.Name, parent.RowType, parent.Columns, null, [new("uq_value", ["Value"], true)], [])], []);
        database.SchemaProvisioner.BeforeDeferredStep = (_, token) =>
        {
            entered.TrySetResult();
            release.Wait(token);
        };
        await database.SchemaProvisioner.ApplyAsync(desired, timeout.Token, deferDataScalingSteps: true);
        await entered.Task.WaitAsync(timeout.Token);
        await using var ddl = await database.CreateSessionAsync(timeout.Token);
        Task<QueryResult> waiting = ddl.ExecuteAsync(command, cancellationToken: timeout.Token).AsTask();
        int observations = 0;
        var elapsed = Stopwatch.StartNew();
        try
        {
            // Act: minimum interval, then until the observation counter proves admission ran.
            do
            {
                waiting.IsCompleted.ShouldBeFalse();
                observations++;
                await Task.Delay(1, timeout.Token);
                elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
            }
            while (elapsed.Elapsed < TimeSpan.FromMilliseconds(150) || observations < 10);
        }
        finally
        {
            release.Set();
        }
        await waiting;
        await WaitDoneAsync(database, timeout.Token);

        // Assert: completion is the DDL result, and the new FK enforces the published parent key.
        observations.ShouldBeGreaterThanOrEqualTo(10);
        database.Catalog.TryGetTable("dbo", "child", out var child).ShouldBeTrue();
        child.Constraints.Count.ShouldBe(1);
        string column = command.Contains("ParentValue", StringComparison.Ordinal) ? "ParentValue" : "Value";
        var rejection = await Should.ThrowAsync<DatabaseException>(async () =>
            await ddl.ExecuteAsync($"INSERT INTO child ({column}) VALUES (2)", cancellationToken: timeout.Token));
        rejection.Message.ShouldContain("FOREIGN KEY");
    }

    private sealed record OrderedItem(int Id, int Required, int? Optional);
}
