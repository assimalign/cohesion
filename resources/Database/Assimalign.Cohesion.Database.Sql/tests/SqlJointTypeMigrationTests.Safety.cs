using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Joint TYPE: a failed reservation handoff is discarded without queueing")]
    [InlineData(false, "cancel")]
    [InlineData(false, "io")]
    [InlineData(false, "oom")]
    [InlineData(true, "cancel")]
    [InlineData(true, "io")]
    [InlineData(true, "oom")]
    public async Task ApplySchemaAsync_HandoffFailure_ShouldDiscardAndRelease(bool deferred, string kind)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        await using var engine = await Builder(await SeedAsync(timeout.Token)).BuildAsync(timeout.Token);
        var database = await engine.OpenDatabaseAsync("typed", timeout.Token);
        database.SchemaProvisioner.JointStage = phase =>
        {
            if (phase != "Handoff") { return; }
            if (kind == "oom") { throw new OutOfMemoryException("injected handoff failure"); }
            if (kind == "io") { throw new System.IO.IOException("injected handoff failure"); }
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
        };

        // Act
        async Task Apply() => await database.SchemaProvisioner.ApplyAsync(Target().Compile(), cancellation.Token, deferred);
        if (kind == "cancel") { await Should.ThrowAsync<OperationCanceledException>(Apply); }
        else if (kind == "oom") { await Should.ThrowAsync<OutOfMemoryException>(Apply); }
        else { (await Should.ThrowAsync<SqlSchemaMigrationException>(Apply)).Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS03402"); }

        // Assert
        database.SchemaProvisioner.Deferred.ShouldBeNull();
        database.Catalog.PendingMigration.ShouldBeNull();
        database.Catalog.SchemaState!.ContentHash.ShouldBe(Source().Compile().Hash);
        database.Provisioning!.FailureCode.ShouldBe("CS03402");
        database.OfflineError.ShouldBeNull();
        await using var writer = await database.CreateSessionAsync(timeout.Token);
        await writer.ExecuteAsync("INSERT INTO items VALUES(1000,9)", cancellationToken: timeout.Token);
        database.SchemaProvisioner.JointStage = null;
        await database.ApplySchemaAsync(Target().Compile(), timeout.Token);
        database.Provisioning.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint TYPE: a cleanup fault preserves both causes, discards intent and releases prepared owners")]
    public async Task ApplySchemaAsync_DiscardFailure_ShouldPreserveCausesAndReclaim()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = await Builder(await SeedAsync(timeout.Token)).BuildAsync(timeout.Token);
        var database = await engine.OpenDatabaseAsync("typed", timeout.Token);
        int discardAttempts = 0;
        database.SchemaProvisioner.JointStage = phase =>
        { if (phase == "TablePrepared") { throw new InvalidOperationException("injected preparation failure"); } };
        database.Catalog.BeforeJointDiscard = () =>
        { if (++discardAttempts == 1) { throw new System.IO.IOException("injected cleanup failure"); } };

        // Act
        var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await database.ApplySchemaAsync(Target().Compile(), timeout.Token));

        // Assert
        failure.ToString().ShouldContain("injected preparation failure", Case.Sensitive);
        failure.ToString().ShouldContain("injected cleanup failure", Case.Sensitive);
        failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS03402");
        discardAttempts.ShouldBe(2);
        database.Catalog.PendingMigration.ShouldBeNull();
        do
        {
            database.TableReclamation.RunBatch(timeout.Token);
            if (database.TableReclamation.HasWork) { await Task.Delay(1, timeout.Token); }
        } while (database.TableReclamation.HasWork);
        database.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
        database.OfflineError.ShouldBeNull();
        await using var writer = await database.CreateSessionAsync(timeout.Token);
        await writer.ExecuteAsync("INSERT INTO items VALUES(1000,9)", cancellationToken: timeout.Token);
        database.SchemaProvisioner.JointStage = null;
        database.Catalog.BeforeJointDiscard = null;
        await database.ApplySchemaAsync(Target().Compile(), timeout.Token);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Non-TYPE apply: compatible widening retains reversible 49a execution and usable tables")]
    public async Task ApplySchemaAsync_CompatibleFacetFailure_ShouldCompensateWithoutJointFence()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = SqlDatabaseEngine.Create("compatible-migration", new());
        var database = await engine.CreateDatabaseAsync("typed", timeout.Token);
        var source = Compiled(new CompiledSchemaTable("items", "row", [new("v", DatabaseType.String, true, MaxLength: 8)], null, [], []));
        var target = Compiled(new CompiledSchemaTable("items", "row", [new("v", DatabaseType.String, true, MaxLength: 12)], null, [],
            [new("ck_v", CompiledSchemaConstraintKind.Check, [], null, [], new("v = 'ok'"))]));
        await database.ApplySchemaAsync(source, timeout.Token);
        await using var writer = await database.CreateSessionAsync(timeout.Token);
        await writer.ExecuteAsync("INSERT INTO items VALUES('bad')", cancellationToken: timeout.Token);
        int jointStages = 0;
        database.SchemaProvisioner.JointStage = _ => jointStages++;

        // Act
        await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await database.ApplySchemaAsync(target, timeout.Token));

        // Assert
        jointStages.ShouldBe(0);
        Table(database).FindColumn("v")!.Type.MaxLength.ShouldBe(8);
        database.Catalog.PendingMigration.ShouldBeNull();
        database.OfflineError.ShouldBeNull();
        await writer.ExecuteAsync("INSERT INTO items VALUES('usable')", cancellationToken: timeout.Token);
        await database.ApplySchemaAsync(source, timeout.Token);
        database.Catalog.SchemaState!.ContentHash.ShouldBe(source.Hash);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint TYPE: dropping an FK and its parent stays unpublished until successful retry")]
    public async Task ApplySchemaAsync_DroppedReferenceParentFailure_ShouldKeepOldEnforcementAndRetry()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = SqlDatabaseEngine.Create("joint-drop-reference", new());
        var database = await engine.CreateDatabaseAsync("typed", timeout.Token);
        var parent = new CompiledSchemaTable("parents", "parent", [new("id", DatabaseType.Int32, false)], new("pk_p", ["id"]), [], []);
        var source = Compiled(parent, new("items", "child",
            [new("pid", DatabaseType.Int32, true), new("note", DatabaseType.Int32, true)], null, [],
            [new("fk_p", CompiledSchemaConstraintKind.Reference, ["pid"], "parents", ["id"])]));
        var target = Compiled(new CompiledSchemaTable("items", "child",
            [new("pid", DatabaseType.Int32, true), new("note", DatabaseType.Int64, true)], null, [], []));
        await database.ApplySchemaAsync(source, timeout.Token);
        await using var writer = await database.CreateSessionAsync(timeout.Token);
        await writer.ExecuteAsync("INSERT INTO parents VALUES(1)", cancellationToken: timeout.Token);
        await writer.ExecuteAsync("INSERT INTO items VALUES(1,7)", cancellationToken: timeout.Token);
        database.SchemaProvisioner.JointStage = phase => { if (phase == "Validated") { throw new InvalidOperationException("injected"); } };

        // Act
        await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await database.ApplySchemaAsync(target, timeout.Token));

        // Assert
        await Should.ThrowAsync<DatabaseException>(async () => await writer.ExecuteAsync("DELETE FROM parents", cancellationToken: timeout.Token));
        await Should.ThrowAsync<DatabaseException>(async () => await writer.ExecuteAsync("INSERT INTO items VALUES(999,8)", cancellationToken: timeout.Token));
        database.OfflineError.ShouldBeNull();
        database.Catalog.PendingMigration.ShouldBeNull();
        database.SchemaProvisioner.JointStage = null;
        await database.ApplySchemaAsync(target, timeout.Token);
        database.Catalog.TryGetTable("dbo", "parents", out _).ShouldBeFalse();
        Table(database).Constraints.ShouldBeEmpty();
        await writer.ExecuteAsync("INSERT INTO items VALUES(999,8)", cancellationToken: timeout.Token);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint TYPE: each data refusal leaves the old constraints, rows and retry available")]
    [InlineData("conversion")]
    [InlineData("check")]
    [InlineData("othercheck")]
    [InlineData("notnull")]
    [InlineData("unique")]
    [InlineData("foreignkey")]
    [InlineData("domain")]
    public async Task ApplySchemaAsync_DataRefusal_ShouldKeepOldSchemaOnline(string kind)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var builder = SqlDatabaseEngine.CreateBuilder("joint-refusals");
        builder.Types.Add(SqlType.CreateDomain("small_value", SqlType.BigInt,
            SqlScalarFunction.Create<long, bool>("small_check", value => value < 2, SqlFunctionVolatility.Immutable)));
        await using var engine = await builder.BuildAsync(timeout.Token);
        var database = await engine.CreateDatabaseAsync("typed", timeout.Token);
        var source = Source().Compile();
        if (kind == "notnull")
        { source = ChangeItems(source, columns: source.Tables.Single(table => table.Name == "items").Columns.Select(column => column.Name == "Value" ? column with { IsNullable = true } : column).ToArray()); }
        await database.ApplySchemaAsync(source, timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync(kind == "notnull" ? "INSERT INTO items VALUES(1,NULL),(2,2)" : "INSERT INTO items VALUES(1,1),(2,2)", cancellationToken: timeout.Token);
        var target = Target().Compile();
        var items = target.Tables.Single(table => table.Name == "items");
        switch (kind)
        {
            case "conversion":
                await session.ExecuteAsync("INSERT INTO items VALUES(3,200)", cancellationToken: timeout.Token);
                target = ChangeItems(target, columns: items.Columns.Select(column => column.Name == "Value" ? column with { Type = DatabaseType.Int8 } : column).ToArray());
                break;
            case "check":
                target = ChangeItems(target, constraints: [new("ck_small", CompiledSchemaConstraintKind.Check, [], null, [], new("Value < 2"))]);
                break;
            case "othercheck":
                await session.ExecuteAsync("INSERT INTO other VALUES(1)", cancellationToken: timeout.Token);
                target = Compiled(target.Tables.Select(table => table.Name != "other" ? table : new CompiledSchemaTable(table.Name,
                    table.RowType, table.Columns, table.PrimaryKey, table.Indexes,
                    [new("ck_other", CompiledSchemaConstraintKind.Check, [], null, [], new("Id > 1"))])).ToArray());
                break;
            case "unique":
                await session.ExecuteAsync("INSERT INTO items VALUES(3,1)", cancellationToken: timeout.Token);
                target = ChangeItems(target, indexes: [new("uq_value", ["Value"], true)]);
                break;
            case "foreignkey":
                target = Compiled(target.Tables.Append(new CompiledSchemaTable("parents", "parent", [new("id", DatabaseType.Int64, false)], new("pk_parent", ["id"]), [], [])).ToArray());
                target = ChangeItems(target, constraints: [new("fk_value", CompiledSchemaConstraintKind.Reference, ["Value"], "parents", ["id"])]);
                break;
            case "domain":
                target = ChangeItems(target, columns: items.Columns.Select(column => column.Name == "Value" ? column with { CustomType = "small_value", DefaultLiteral = null } : column).ToArray());
                break;
        }
        var previous = Table(database);
        using var events = new Assimalign.Cohesion.Testing.EventSourceRecorder(SqlDatabaseEventSource.Log,
            System.Diagnostics.Tracing.EventLevel.Error);

        // Act
        var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await database.ApplySchemaAsync(target, timeout.Token));

        // Assert
        failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS03402");
        Table(database).ShouldBeSameAs(previous);
        database.OfflineError.ShouldBeNull();
        database.Catalog.PendingMigration.ShouldBeNull();
        database.Provisioning!.FailureCode.ShouldBe("CS03402");
        events.Events.ShouldContain(entry => entry.EventName == "DeferredStepFailed" && entry.Payload!.Contains("CS03402"));
        do
        {
            database.TableReclamation.RunBatch(timeout.Token);
            if (database.TableReclamation.HasWork) { await Task.Delay(1, timeout.Token); }
        } while (database.TableReclamation.HasWork);
        database.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
        await session.ExecuteAsync("INSERT INTO items VALUES(1000,9)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("DELETE FROM items", cancellationToken: timeout.Token);
        await session.ExecuteAsync("DELETE FROM other", cancellationToken: timeout.Token);
        await database.ApplySchemaAsync(target, timeout.Token);
        database.Catalog.SchemaState!.ContentHash.ShouldBe(target.Hash);
        database.Provisioning.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint TYPE: new-table FKs, table drops and column drops share the conversion commit")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplySchemaAsync_MixedChanges_ShouldKeepAllObjectsUntilPublication(bool fail)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = SqlDatabaseEngine.Create("joint-mixed", new());
        var database = await engine.CreateDatabaseAsync("typed", timeout.Token);
        var original = Source().Compile();
        var oldItems = original.Tables.Single(table => table.Name == "items");
        original = ChangeItems(original, columns: oldItems.Columns.Append(new CompiledSchemaColumn("legacy", DatabaseType.Int32, true)).ToArray());
        original = Compiled(original.Tables.Append(new CompiledSchemaTable("gone", "gone", [new("id", DatabaseType.Int32, false)], null, [], [])).ToArray());
        await database.ApplySchemaAsync(original, timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("INSERT INTO items VALUES(1,7,99)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("INSERT INTO gone VALUES(55)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("CREATE SCHEMA ext", cancellationToken: timeout.Token);
        await session.ExecuteAsync("CREATE TABLE ext.gone(id INT)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("INSERT INTO ext.gone VALUES(33)", cancellationToken: timeout.Token);
        var target = Compiled(Target().Compile().Tables.Append(new CompiledSchemaTable("notes", "notes",
            [new("id", DatabaseType.Int32, false), new("pid", DatabaseType.Int32, true)], new("pk_notes", ["id"]), [],
            [new("fk_notes", CompiledSchemaConstraintKind.Reference, ["pid"], "items", ["Id"])] )).ToArray());
        int observations = 0;
        database.SchemaProvisioner.JointStage = stage =>
        {
            if (stage is not ("TablePrepared" or "Validated" or "Publishing")) { return; }
            observations++;
            Table(database).FindColumn("legacy").ShouldNotBeNull();
            database.Catalog.TryGetTable("dbo", "gone", out _).ShouldBeTrue();
            database.Catalog.TryGetTable("dbo", "notes", out _).ShouldBeFalse();
            if (fail && stage == "Validated") { throw new InvalidOperationException("injected mixed failure"); }
        };

        // Act
        if (fail)
        {
            await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await database.ApplySchemaAsync(target, timeout.Token));
            (await RowsAsync(session, "SELECT legacy FROM items", timeout.Token)).Single()[0].ShouldBe(99);
            (await RowsAsync(session, "SELECT id FROM gone", timeout.Token)).Single()[0].ShouldBe(55);
            await session.ExecuteAsync("INSERT INTO gone VALUES(66)", cancellationToken: timeout.Token);
            database.SchemaProvisioner.JointStage = null;
        }
        await database.ApplySchemaAsync(target, timeout.Token);

        // Assert
        observations.ShouldBe(fail ? 2 : 3);
        database.Catalog.TryGetTable("dbo", "gone", out _).ShouldBeFalse();
        database.Catalog.TryGetTable("ext", "gone", out _).ShouldBeTrue();
        (await RowsAsync(session, "SELECT id FROM ext.gone", timeout.Token)).Single()[0].ShouldBe(33);
        Table(database).FindColumn("legacy").ShouldBeNull();
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("INSERT INTO notes VALUES(1,999)", cancellationToken: timeout.Token));
        await session.ExecuteAsync("INSERT INTO notes VALUES(1,1)", cancellationToken: timeout.Token);
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("DELETE FROM items", cancellationToken: timeout.Token));
        (await RowsAsync(session, "SELECT Value FROM items", timeout.Token)).Single()[0].ShouldBe(7L);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint TYPE: all durable publication images reopen at exactly one schema")]
    [InlineData(StorageCommitDurability.Synchronous)]
    [InlineData(StorageCommitDurability.Grouped)]
    [InlineData(StorageCommitDurability.None)]
    public async Task OpenAsync_CrashBoundaries_ShouldRecoverExactlyOldOrNew(StorageCommitDurability durability)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var storage = new CrashCaptureSqlStorageStrategy(durability);
        var phases = new List<CrashCaptureSqlStorageStrategy>();
        CrashCaptureSqlStorageStrategy.CrashPointRecorder? recorder = null;
        var source = Compiled(Source().Compile().Tables.Append(new CompiledSchemaTable("gone", "gone",
            [new("id", DatabaseType.Int32, false)], null, [], [])).ToArray());
        var target = Compiled(Target().Compile().Tables.Append(new CompiledSchemaTable("notes", "notes",
            [new("pid", DatabaseType.Int32, true)], null, [],
            [new("fk_notes", CompiledSchemaConstraintKind.Reference, ["pid"], "items", ["Id"])])).ToArray());
        await using (var engine = await Builder(storage).BuildAsync(timeout.Token))
        {
            var database = await engine.CreateDatabaseAsync("typed", timeout.Token);
            await database.ApplySchemaAsync(source, timeout.Token);
            await using var session = await database.CreateSessionAsync(timeout.Token);
            await session.ExecuteAsync("INSERT INTO items VALUES(1,10),(2,20)", cancellationToken: timeout.Token);
            await session.ExecuteAsync("INSERT INTO gone VALUES(55)", cancellationToken: timeout.Token);
            database.SchemaProvisioner.JointStage = phase =>
            {
                phases.Add(storage.CaptureDurableImages());
            };

            // Act
            recorder = storage.RecordCrashPoints();
            await database.ApplySchemaAsync(target, timeout.Token);
            recorder.Dispose();
        }

        // Assert
        recorder.ShouldNotBeNull().Count.ShouldBeGreaterThan(1);
        int old = 0, changed = 0;
        foreach (var image in phases.Concat(Enumerable.Range(0, recorder.Count).Select(recorder.Open)))
        {
            await using var recovered = await Builder(image).BuildAsync(timeout.Token);
            var database = await recovered.OpenDatabaseAsync("typed", timeout.Token);
            bool after = Table(database).FindColumn("Value")!.Type.Type == DatabaseType.Int64;
            if (after) { changed++; } else { old++; }
            if (after) { Table(database).FindColumn("Added").ShouldNotBeNull(); }
            else { Table(database).FindColumn("Added").ShouldBeNull(); }
            database.Catalog.SchemaState!.ContentHash.ShouldBe(after ? target.Hash : source.Hash);
            database.Catalog.TryGetTable("dbo", "notes", out _).ShouldBe(after);
            database.Catalog.TryGetTable("dbo", "gone", out _).ShouldBe(!after);
            database.Catalog.PendingMigration.ShouldBeNull();
            database.OfflineError.ShouldBeNull();
            await using var reader = await database.CreateSessionAsync(timeout.Token);
            (await RowsAsync(reader, "SELECT Value FROM items ORDER BY Id", timeout.Token)).Select(row => Convert.ToInt64(row[0])).ShouldBe(new long[] { 10, 20 });
            if (after)
            {
                await Should.ThrowAsync<DatabaseException>(async () =>
                    await reader.ExecuteAsync("INSERT INTO notes VALUES(999)", cancellationToken: timeout.Token));
            }
            else { (await RowsAsync(reader, "SELECT id FROM gone", timeout.Token)).Single()[0].ShouldBe(55); }
            await reader.ExecuteAsync(after ? "INSERT INTO items(Id,Value) VALUES(3,30)" : "INSERT INTO items VALUES(3,30)", cancellationToken: timeout.Token);
        }
        old.ShouldBeGreaterThan(0);
        changed.ShouldBeGreaterThan(0);
        _output.WriteLine($"Joint {durability}: phase/publication images={phases.Count + recorder.Count}, old={old}, new={changed}.");
    }

    private static SqlCompiledSchema Compiled(params CompiledSchemaTable[] tables)
        => new(SqlCompiledSchema.CurrentFormat, "typed", true, [], tables, []);

    private static SqlCompiledSchema ChangeItems(SqlCompiledSchema schema, IReadOnlyList<CompiledSchemaColumn>? columns = null,
        IReadOnlyList<CompiledSchemaIndex>? indexes = null, IReadOnlyList<CompiledSchemaConstraint>? constraints = null)
        => Compiled(schema.Tables.Select(table => table.Name != "items" ? table : new CompiledSchemaTable(table.Name, table.RowType,
            columns ?? table.Columns, table.PrimaryKey, indexes ?? table.Indexes, constraints ?? table.Constraints)).ToArray());
}
