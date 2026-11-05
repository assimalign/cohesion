using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    public static IEnumerable<object[]> KeepParityCases()
    {
        foreach (string kind in new[] { "nullable", "missing", "required", "drop", "widen", "unbounded", "precision-widen", "precision-tighten", "alias", "not-null", "check", "drop-check", "drop-index", "default", "drop-default" })
        {
            foreach (bool composite in new[] { false, true })
            {
                foreach (bool ordinaryCreation in new[] { false, true })
                { yield return [kind, composite, ordinaryCreation]; }
            }
        }
    }

    private static SqlCompiledSchema KeepParitySchema(string kind, bool target, bool composite)
    {
        var columns = new List<CompiledSchemaColumn>
        {
            new("id", DatabaseType.Int32, false), new("part", DatabaseType.Int32, false),
            new("value", DatabaseType.Decimal, true, Precision: target && kind == "precision-widen" ? 12 : target && kind == "precision-tighten" ? 5 : 10,
                Scale: 0, CustomType: target && kind == "alias" ? "DECIMAL(10,0)" : null,
                DefaultLiteral: kind == "default" ? target ? "2" : "1" : kind == "drop-default" && !target ? "1" : null),
            new("text", DatabaseType.String, true, MaxLength: target && kind == "unbounded" ? null : target && kind == "widen" ? 12 : 8),
        };
        if (!target || kind != "drop") { columns.Add(new("legacy", DatabaseType.Int32, !(target && kind == "not-null"))); }
        if (target && kind is "nullable" or "missing" or "required")
        { columns.Add(new("added", DatabaseType.Int32, kind != "required", DefaultLiteral: kind == "nullable" ? null : "7")); }
        return Compiled(new CompiledSchemaTable("items", "row", columns, new("pk_items", composite ? ["id", "part"] : ["id"]),
            kind == "drop-index" && !target ? [new("ix_text", ["text"], false)] : [],
            kind == "check" && target || kind == "drop-check" && !target
                ? [new("ck_value", CompiledSchemaConstraintKind.Check, [], null, [], new("value > 0"))] : []));
    }

    private static async Task ApplyOrdinaryParityAsync(SqlDatabase database, SqlCompiledSchema? source, SqlCompiledSchema target, CancellationToken token)
    {
        var script = SqlMigrationScriptGenerator.Generate(SqlSchemaMigrationPlanner.Plan(source, target), source);
        var locks = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, token);
        try
        {
            await using var session = database.CreateSchemaSession("typed", token, locks.Sequence);
            foreach (var step in script.Steps) { await session.ExecuteAsync(step.Request, token); }
            await database.Catalog.SaveSchemaStateAsync(new(target.Hash, target.CanonicalDocument, database.Catalog.DdlGeneration), token);
        }
        finally { await database.Coordinator.RollbackAsync(locks, CancellationToken.None); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint parity: every metadata/validation step keeps keyed heaps and key names with zero rewrites")]
    [MemberData(nameof(KeepParityCases))]
    public async Task ApplyAsync_KeepParity_ShouldRetainLayoutKeyNamesAndCost(string kind, bool composite, bool ordinaryCreation)
    {
        // Arrange: both historical per-step keys and keys created by a joint job.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var source = KeepParitySchema(kind, false, composite);
        var target = KeepParitySchema(kind, true, composite);
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var seed = await Builder(storage).BuildAsync(guard.Token))
        {
            var database = await seed.CreateDatabaseAsync("typed", guard.Token);
            var initial = kind == "drop-index" ? target : source;
            if (ordinaryCreation) { await ApplyOrdinaryParityAsync(database, null, initial, guard.Token); }
            else { await database.ApplySchemaAsync(initial, guard.Token); }
            // Build an alternate-owned tree on the real keyed heap. Heap-owned
            // removal remains #1808 and is measured by the separate negative control.
            if (kind == "drop-index") { await database.ApplySchemaAsync(source, guard.Token); }
            await using var writer = await database.CreateSessionAsync(guard.Token);
            await writer.ExecuteAsync("INSERT INTO items VALUES " + string.Join(',', Enumerable.Range(1, 128).Select(id => $"({id},{id % 7},42,'ok',9)")), cancellationToken: guard.Token);
            database.Coordinator.Checkpoint(guard.Token);
            database.CatalogStorage.Checkpoint();
            storage = storage.CaptureDurableImages();
        }
        var written = new long[2];
        for (int mode = 0; mode < 2; mode++)
        {
            var image = storage.CaptureDurableImages();
            await using var engine = await Builder(image).BuildAsync(guard.Token);
            var database = await engine.OpenDatabaseAsync("typed", guard.Token);
            ulong owner = Table(database).ObjectId;
            string key = database.Catalog.GetIndexes(owner).Single(index => index.IsPrimaryKey).Name;
            long rewritten = 0;
            long decoded = 0;
            long before = image.PageWrites("typed");
            SqlPlanExecutor.RewriteStage = (stage, rows) => { if (stage == "Copied") { rewritten += rows; } };
            SqlPlanExecutor.RowDecoded = _ => decoded++;
            try
            {
                // Act: count the old intent/step/marker path as 49a did, then joint.
                if (mode == 0)
                {
                    var script = SqlMigrationScriptGenerator.Generate(SqlSchemaMigrationPlanner.Plan(source, target), source);
                    await database.Catalog.SavePendingMigrationAsync(new(target.Hash, target.CanonicalDocument,
                        script.Steps.Select((step, index) => new SqlCatalogMigrationStep(step.Kind, "items", step.Operation.ObjectName, index + 1)).ToArray(), script.Steps.Count), guard.Token);
                    await ApplyOrdinaryParityAsync(database, source, target, guard.Token);
                }
                else { await database.ApplySchemaAsync(target, guard.Token); }
                if (kind == "drop-index") { await DrainReclamationAsync(database, guard.Token); }
                database.Coordinator.Checkpoint(guard.Token);
                database.CatalogStorage.Checkpoint();
                written[mode] = image.PageWrites("typed") - before;

                // Assert
                rewritten.ShouldBe(0);
                Table(database).ObjectId.ShouldBe(owner);
                database.Catalog.GetIndexes(owner).Single(index => index.IsPrimaryKey).Name.ShouldBe(key);
                if (mode == 1 && kind is "nullable" or "missing" or "drop" or "widen" or "unbounded" or "precision-widen" or "alias" or "drop-check" or "drop-index" or "default" or "drop-default")
                { decoded.ShouldBe(0); }
                await using var reader = await database.CreateSessionAsync(guard.Token);
                (await RowsAsync(reader, "SELECT CONSTRAINT_NAME FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS WHERE TABLE_NAME='items' AND CONSTRAINT_TYPE='PRIMARY KEY'", guard.Token)).Single()[0].ShouldBe(key);
                string fields = string.Join(',', target.Tables.Single().Columns.Select(column => column.Name));
                string values = string.Join(',', target.Tables.Single().Columns.Select(column => column.Name switch
                { "id" => "1", "part" => "1", "text" => "'ok'", "added" => "7", _ => "9" }));
                var violation = await Should.ThrowAsync<DatabaseException>(async () => await reader.ExecuteAsync($"INSERT INTO items({fields}) VALUES({values})", cancellationToken: guard.Token));
                violation.Message.ShouldContain(key, Case.Sensitive);
                await reader.ExecuteAsync($"INSERT INTO items({fields}) VALUES({values}) ON CONFLICT ON CONSTRAINT {key} DO UPDATE SET value=EXCLUDED.value", cancellationToken: guard.Token);
                if (kind == "drop") { Table(database).DroppedColumnOrdinals.ShouldBe(new[] { 4 }); }
                if (kind is "missing" or "required")
                { (await RowsAsync(reader, "SELECT added FROM items WHERE id=2", guard.Token)).Single()[0].ShouldBe(7); }
            }
            finally { SqlPlanExecutor.RewriteStage = null; SqlPlanExecutor.RowDecoded = null; }
        }
        _output.WriteLine($"Keep parity {kind}, composite={composite}, ordinary creation={ordinaryCreation}: rows rewritten 0 -> 0; pages {written[0]} -> {written[1]}");
        written[1].ShouldBeLessThanOrEqualTo(written[0] * 110 / 100);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint parity: zero-operation reconciliation records the current generation once")]
    public async Task ApplySchemaAsync_ZeroOperations_ShouldRefreshMarkerAndReleaseAdmission()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = await Builder(new()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        var schema = KeepParitySchema("default", false, false);
        await database.ApplySchemaAsync(schema, guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync("CREATE TABLE unrelated(id INT)", cancellationToken: guard.Token);

        // Act
        var result = await database.ApplySchemaAsync(schema, guard.Token);

        // Assert
        result.OperationCount.ShouldBe(0);
        database.Catalog.SchemaState!.Generation.ShouldBe(database.Catalog.DdlGeneration);
        database.SchemaProvisioner.JointMetrics!.SelfCommits.ShouldBe(1);
        (await database.ApplySchemaAsync(schema, guard.Token)).WasAlreadyApplied.ShouldBeTrue();
    }
}
