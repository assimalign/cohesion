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
    private const long integrationSmallBudget = 128 * 1024;
    private const long integrationLargeBudget = 32 * 1024 * 1024;
    private const int integrationRows = 8192;

    private static SqlCompiledSchema IntegrationSchema(bool rewritten, bool extraIndex = false)
        => Compiled(new CompiledSchemaTable("items", "row",
            [new("Value", rewritten ? DatabaseType.Int64 : DatabaseType.Int32, false), new("Payload", DatabaseType.String, false)],
            null, rewritten ? extraIndex ? [new("ix_value", ["Value"], false), new("ix_payload", ["Payload"], false)]
                : [new("ix_value", ["Value"], false)] : [], []));

    private static async Task<CrashCaptureSqlStorageStrategy> SeedIntegrationAsync(CancellationToken token)
    {
        var storage = new CrashCaptureSqlStorageStrategy();
        await using var engine = await Builder(storage).BuildAsync(token);
        var database = await engine.CreateDatabaseAsync("typed", token);
        await database.ApplySchemaAsync(IntegrationSchema(false), token);
        SeedRaw(database, [1, new string('x', 256)], integrationRows);
        database.Coordinator.Checkpoint(token);
        return storage.CaptureDurableImages();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint copy and kept-owner index build bypass plain SELECT buffers at low and high budgets (#1778, #1762)")]
    [InlineData(integrationSmallBudget)]
    [InlineData(integrationLargeBudget)]
    public async Task ApplyAsync_TableLargerThanStatementBudget_ShouldPublishAndReleaseMemory(long budget)
    {
        // Arrange: retained SELECT output alone would exceed the small budget sixteen times.
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var builder = Builder(await SeedIntegrationAsync(guard.Token));
        builder.Options.StatementMemoryBudget = budget;
        await using var engine = await builder.BuildAsync(guard.Token);
        var database = await engine.OpenDatabaseAsync("typed", guard.Token);
        ulong original = Table(database).ObjectId;

        // Act: first copy a replacement heap and its index, then build a tree on the kept heap.
        await database.SchemaProvisioner.ApplyAsync(IntegrationSchema(true), guard.Token, deferDataScalingSteps: true);
        await WaitDoneAsync(database, guard.Token);
        ulong replacement = Table(database).ObjectId;
        replacement.ShouldNotBe(original);
        var copy = database.SchemaProvisioner.JointMetrics.ShouldNotBeNull();
        copy.HashMemoryBytes.ShouldBe(0);
        copy.PeakHashMemoryBytes.ShouldBeGreaterThan(0);
        copy.PeakHashMemoryBytes.ShouldBeLessThanOrEqualTo(integrationSmallBudget);
        copy.SelfCommits.ShouldBe(1);
        await database.SchemaProvisioner.ApplyAsync(IntegrationSchema(true, extraIndex: true), guard.Token, deferDataScalingSteps: true);
        await WaitDoneAsync(database, guard.Token);

        // Assert: the same complete data and index registrations survive either budget.
        database.Provisioning.ShouldBeNull();
        database.IsOffline.ShouldBeFalse();
        Table(database).ObjectId.ShouldBe(replacement);
        database.Catalog.GetIndexes(replacement).Select(index => index.Name).Order().ShouldBe(["ix_payload", "ix_value"]);
        database.SchemaProvisioner.JointMetrics.ShouldNotBeNull().HashMemoryBytes.ShouldBe(0);
        database.SchemaProvisioner.JointMetrics.SelfCommits.ShouldBe(1);
        await using var session = await database.CreateSessionAsync(guard.Token);
        (await RowsAsync(session, "SELECT COUNT(*), SUM(Value) FROM items WHERE Value = 1", guard.Token))
            .ShouldHaveSingleItem().ShouldBe(new object?[] { (long)integrationRows, (long)integrationRows });
        session.LastStatementMetrics.ShouldNotBeNull().HashMemoryBytes.ShouldBe(0);
        _output.WriteLine($"budget={budget}; rows={integrationRows}; copyPeak={copy.PeakHashMemoryBytes}; copyHash={copy.HashMemoryBytes}; indexHash={database.SchemaProvisioner.JointMetrics.HashMemoryBytes}");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Declared CTAS still refuses CS05067 online and the next Build resumes that statement (#1778, #1762)")]
    public async Task ProvisionAsync_DeclaredCtasOverBudget_ShouldStayOnlineAndResumeOnLargerBuild()
    {
        // Arrange: run the declaration's normal provisioning entry on a live engine so its
        // availability after refusal can be observed before Build's cleanup closes it.
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        const string script = "CREATE TABLE script_marker (Value INT); INSERT INTO script_marker VALUES (1); " +
            "CREATE TABLE copied AS SELECT Payload FROM items ORDER BY Payload; INSERT INTO script_marker VALUES (2);";
        var storage = await SeedIntegrationAsync(guard.Token);
        var builder = Builder(storage);
        builder.Options.StatementMemoryBudget = integrationSmallBudget;
        CrashCaptureSqlStorageStrategy crash;
        await using (var engine = await builder.BuildAsync(guard.Token))
        {
            var database = await engine.OpenDatabaseAsync("typed", guard.Token);
            await database.ApplySchemaAsync(IntegrationSchema(true), guard.Token);
            var declaration = new SqlDatabaseBuilder(SqlDatabaseEngine.CreateBuilder("script-declaration"), "typed");
            declaration.Script("001", script);
            var compiled = SqlDeclaredDatabase.Compile(engine.Name, declaration, null, engine.Functions, engine.Types, engine.ParserOptions);

            // Act
            var failure = await Should.ThrowAsync<DatabaseException>(async () => await compiled.ProvisionAsync(engine, guard.Token));

            // Assert: the prefix is durable and the refused CTAS has published no object.
            failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS05067");
            database.IsOffline.ShouldBeFalse();
            database.Catalog.TryGetTable("dbo", "copied", out _).ShouldBeFalse();
            database.Catalog.Scripts.ShouldHaveSingleItem().CompletedStatements.ShouldBe(2);
            await using var session = await database.CreateSessionAsync(guard.Token);
            (await RowsAsync(session, "SELECT Value FROM script_marker", guard.Token)).ShouldHaveSingleItem()[0].ShouldBe(1);
            crash = storage.CaptureDurableImages();
        }
        // The real Build entry refuses the same pending statement at the same budget.
        var refusedBuild = Builder(crash);
        refusedBuild.Options.StatementMemoryBudget = integrationSmallBudget;
        refusedBuild.AddDatabase("typed", declaration => declaration.Script("001", script));
        var buildFailure = await Should.ThrowAsync<DatabaseException>(async () => await refusedBuild.BuildAsync(guard.Token));
        buildFailure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS05067");
        var resumed = Builder(crash.CaptureDurableImages());
        resumed.Options.StatementMemoryBudget = integrationLargeBudget;
        resumed.AddDatabase("typed", declaration => declaration.Script("001", script));
        await using var restarted = await resumed.BuildAsync(guard.Token);
        var recovered = await restarted.OpenDatabaseAsync("typed", guard.Token);
        recovered.Catalog.Scripts.ShouldHaveSingleItem().CompletedStatements.ShouldBe(4);
        recovered.IsOffline.ShouldBeFalse();
        await using var reader = await recovered.CreateSessionAsync(guard.Token);
        (await RowsAsync(reader, "SELECT Value FROM script_marker ORDER BY Value", guard.Token)).Select(row => row[0]).ShouldBe([1, 2]);
        (await RowsAsync(reader, "SELECT COUNT(*) FROM copied", guard.Token)).ShouldHaveSingleItem()[0].ShouldBe((long)integrationRows);
        reader.LastStatementMetrics.ShouldNotBeNull().HashMemoryBytes.ShouldBe(0);
    }
}
