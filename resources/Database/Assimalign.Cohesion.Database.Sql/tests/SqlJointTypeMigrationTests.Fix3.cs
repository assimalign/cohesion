using System;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Every physical owner build reports its lifecycle (#1808)")]
    [InlineData("schema")] [InlineData("truncate")] [InlineData("rewrite")]
    [InlineData("failure")] [InlineData("cancel")]
    public async Task ApplySchemaAsync_Fix3PhysicalBuild_ShouldEmitLifecycle(string mode)
    {
        // Arrange.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(guard.Token);
        await using var engine = await Builder(new CrashCaptureSqlStorageStrategy()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        SqlCompiledSchema Declare(bool target) => Compiled(new CompiledSchemaTable("items", "row",
            [new("v", target ? DatabaseType.Int64 : DatabaseType.Int32, false)], new("pk", ["v"]), [], []));
        if (mode != "schema")
        {
            await database.ApplySchemaAsync(Declare(false), guard.Token);
            await using var writer = await database.CreateSessionAsync(guard.Token);
            await writer.ExecuteAsync("INSERT INTO items VALUES " + string.Join(',', Enumerable.Range(0, 128).Select(i => $"({i})")), cancellationToken: guard.Token);
        }
        database.SchemaProvisioner.JointStage = stage =>
        {
            if (stage != "TablePrepared") { return; }
            if (mode == "failure") { throw new InvalidOperationException("injected rewrite refusal"); }
            if (mode == "cancel") { cancel.Cancel(); cancel.Token.ThrowIfCancellationRequested(); }
        };
        using var events = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Informational);

        // Act.
        if (mode == "truncate")
        {
            await using var session = await database.CreateSessionAsync(guard.Token);
            await session.ExecuteAsync("TRUNCATE TABLE items", cancellationToken: guard.Token);
        }
        else if (mode is "failure" or "cancel")
        { await Should.ThrowAsync<Exception>(async () => await database.ApplySchemaAsync(Declare(true), cancel.Token)); }
        else { await database.ApplySchemaAsync(Declare(mode == "rewrite"), guard.Token); }

        // Assert.
        var began = events.Events.Where(item => item.EventId == 28).ShouldHaveSingleItem();
        var ended = events.Events.Where(item => item.EventId == 29).ShouldHaveSingleItem();
        began.Opcode.ShouldBe(EventOpcode.Info);
        ended.Opcode.ShouldBe(EventOpcode.Info);
        began.PayloadNames.ShouldBe(["engineName", "database", "objectId"]);
        var beganPayload = began.Payload!;
        var endedPayload = ended.Payload!;
        beganPayload.Take(3).ShouldBe(endedPayload.Take(3));
        beganPayload[0].ShouldBe(engine.Name.ToString());
        beganPayload[1].ShouldBe(database.Name.ToString());
        ((bool)endedPayload[3]!).ShouldBe(mode is not ("failure" or "cancel"));
        events.Events.ShouldNotContain(item => item.EventId == 0);
        _output.WriteLine($"PHYSICAL_BUILD|{mode}|owner={beganPayload[2]}|succeeded={endedPayload[3]}");
    }
}
