using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Web.Health;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

public sealed partial class DatabaseHostingEventSourceTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Hosting] - Held schema: reopen, rewrite and explicit refusal preserve health detail and surviving rows")]
    [InlineData("reopen")]
    [InlineData("rewrite")]
    [InlineData("refusal")]
    public async Task Health_HeldSchemaMaintenance_ShouldPreserveHold(string operation)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string path = Path.Combine(Path.GetTempPath(), "cohesion-held-maintenance", Guid.NewGuid().ToString("N"));
        var first = HeldSchema(false); var second = HeldSchema(true);
        try
        {
            var seed = SqlDatabaseEngine.CreateBuilder("sql"); seed.Options.RootPath = path;
            await using (var engine = await seed.BuildAsync(guard.Token))
            {
                var database = await engine.CreateDatabaseAsync("held", guard.Token);
                await database.ApplySchemaAsync(first.Compile(), guard.Token);
                await database.ApplySchemaAsync(second.Compile(), guard.Token);
                await using var writer = await database.CreateSessionAsync(guard.Token);
                await writer.ExecuteAsync("INSERT INTO items VALUES(1,9)", cancellationToken: guard.Token);
            }
            HealthFailureWorker? worker = null;
            var builder = new DatabaseApplicationBuilder(new DatabaseApplicationOptions
            { ReopenInitialDelay = TimeSpan.FromMilliseconds(10), ReopenMaximumDelay = TimeSpan.FromMilliseconds(20) });
            builder.AddSql("sql", sql =>
            {
                sql.Options.RootPath = path;
                sql.Options.WorkerFailureWindow = TimeSpan.FromTicks(1);
                sql.Options.WorkerFailureMinimumPasses = 2;
                sql.AddWorker(_ => worker = new HealthFailureWorker());
                sql.AddDatabase(first);
            });
            await using (var app = builder.Build(guard.Token))
            {
                var engine = (SqlDatabaseEngine)app.Context.Engines.Single();
                var database = await engine.OpenDatabaseAsync("held", guard.Token);
                string detail = database.Provisioning!.Detail!;
                (await app.Context.CheckAsync(guard.Token)).Status.ShouldBe(HealthStatus.Degraded);
                using var recorder = new HostingEventRecorder(EventLevel.Informational);

                // Act
                if (operation == "rewrite") { await database.RewriteTableAsync("dbo", "items", guard.Token); }
                else if (operation == "refusal")
                {
                    var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await database.ApplySchemaAsync(first.Compile(), guard.Token));
                    failure.Diagnostics.ShouldContain(value => value.Code == "CS03105");
                }
                else
                {
                    worker.ShouldNotBeNull().Fail("held");
                    while (engine.OfflineDatabases.Count == 0) { await Task.Delay(1, guard.Token); }
                    await ((IHost)app).StartAsync(guard.Token);
                    var service = app.Context.ReopenService.ShouldNotBeNull();
                    while (engine.OfflineDatabases.Count > 0 || service.GetPendingReopens().Count > 0 ||
                        !engine.TryGetDatabase("held", out var open) || ReferenceEquals(open, database))
                    { await Task.Delay(1, guard.Token); }
                    database = await engine.OpenDatabaseAsync("held", guard.Token);
                    await ((IHost)app).StopAsync(guard.Token);
                    recorder.Events.ShouldContain(entry => entry.EventId == 3);
                }
                var health = await app.Context.CheckAsync(guard.Token);

                // Assert
                health.Status.ShouldBe(HealthStatus.Degraded);
                health.Data!["engine.0.provisioning.0.failureCode"].ShouldBe("CS03405");
                health.Data["engine.0.provisioning.0.detail"].ShouldBe(detail);
                database.Provisioning!.Detail.ShouldBe(detail);
                await using var reader = await database.CreateSessionAsync(guard.Token);
                await using var result = (DatabaseResultSet)await reader.ExecuteAsync("SELECT Added FROM items", cancellationToken: guard.Token);
                int count = 0;
                await foreach (var row in result.GetRowsAsync(guard.Token)) { row.GetValue(0).ShouldBe(9); count++; }
                count.ShouldBe(1);
            }
            var current = DatabaseApplication.CreateBuilder();
            current.AddSql("sql", sql => { sql.Options.RootPath = path; sql.AddDatabase(second); });
            await using var redeployed = current.Build(guard.Token);
            (await redeployed.Context.CheckAsync(guard.Token)).Status.ShouldBe(HealthStatus.Healthy);
        }
        finally { if (Directory.Exists(path)) { Directory.Delete(path, true); } }
    }

    [Theory(DisplayName = "Cohesion Test [Database.Hosting] - Held detail: health and events cap at 4096 UTF-16 code units without splitting surrogate pairs")]
    [InlineData(4092)]
    [InlineData(4093)]
    public async Task Health_LargeHeldDetail_ShouldBoundWithoutSplittingSurrogates(int prefixLength)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = new RecordingEngine();
        await using var database = new DetailDatabase(engine, new string('x', prefixLength) + "\U0001F600" + new string('y', 100));
        engine.Inventory = token => Inventory(database, token);
        var builder = new DatabaseApplicationBuilder(new DatabaseApplicationOptions { ReopenOfflineDatabases = false });
        builder.AddEngine(engine);
        await using var app = builder.Build(guard.Token);
        using var recorder = new HostingEventRecorder(EventLevel.Informational);

        // Act
        var result = await app.Context.CheckAsync(guard.Token);
        string healthDetail = result.Data!["engine.0.provisioning.0.detail"].ShouldBeOfType<string>();
        string eventDetail = recorder.Events.Single(entry => entry.EventId == 16).Payload![12].ShouldBeOfType<string>();

        // Assert
        char.IsHighSurrogate(eventDetail[^4]).ShouldBeFalse();
        healthDetail.Length.ShouldBeLessThanOrEqualTo(4096);
        eventDetail.ShouldBe(healthDetail);
        healthDetail.ShouldEndWith("...");
        char.IsHighSurrogate(healthDetail[^4]).ShouldBeFalse();
        result.Description!.ShouldContain(healthDetail, Case.Sensitive);
    }

    private static async IAsyncEnumerable<DatabaseInstance> Inventory(DatabaseInstance database,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return database;
        await Task.CompletedTask;
    }

    private sealed class DetailDatabase : DatabaseInstance
    {
        public DetailDatabase(DatabaseEngine engine, string detail) : base("detail", engine)
        { SetProvisioningProgress(new DatabaseProvisioningProgress("SchemaHeld", "items", 1, 1, false, failureCode: "CS03405", detail: detail)); }
        protected override ValueTask<DatabaseSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
        protected override void DisposeCore() { }
        protected override ValueTask DisposeAsyncCore() => ValueTask.CompletedTask;
    }
}
