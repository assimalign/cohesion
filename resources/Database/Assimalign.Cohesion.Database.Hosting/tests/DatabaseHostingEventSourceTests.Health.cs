using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Hosting.Internal;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Hosting.Health;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

public sealed partial class DatabaseHostingEventSourceTests
{
    private static readonly string[] _healthPayload = ["hostId", "engineName", "database", "reason", "cause", "operation", "objectName", "position", "stepCount", "rowsProcessed", "totalRows"];

    [Theory(DisplayName = "Cohesion Test [Database.Hosting] - Health tracing: real provisioning transitions survive concurrent polls")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Health_DeferredMigration_ShouldReportEachTransitionOnce(bool fail)
    {
        // Arrange: block an immutable CHECK in the real provisioning worker.
        string path = Path.Combine(Path.GetTempPath(), "cohesion-health-trace", Guid.NewGuid().ToString("N"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        try
        {
            var seed = SqlDatabaseEngine.CreateBuilder("seed");
            seed.Options.RootPath = path;
            await using (var engine = await seed.AddDatabase(HealthSchema(false)).BuildAsync(timeout.Token))
            {
                var database = await engine.OpenDatabaseAsync("health", timeout.Token);
                await using var session = await database.CreateSessionAsync(timeout.Token);
                await session.ExecuteAsync("INSERT INTO items (Id) VALUES (1), (2), (3)", cancellationToken: timeout.Token);
            }
            var builder = new DatabaseApplicationBuilder(new DatabaseApplicationOptions { ReopenOfflineDatabases = false });
            builder.AddSql("sql", sql =>
            {
                sql.Options.RootPath = path;
                sql.Functions.Add(SqlScalarFunction.Create<int, bool>("health_probe", value =>
                {
                    if (Interlocked.Increment(ref calls) == 2)
                    {
                        entered.TrySetResult();
                        release.Wait(timeout.Token);
                    }
                    return !fail && value > 0 || value == 1;
                }, SqlFunctionVolatility.Immutable));
                sql.AddDatabase(HealthSchema(true));
            });
            await using var application = builder.Build(timeout.Token);
            try
            {
                await entered.Task.WaitAsync(timeout.Token);
                using var recorder = new HostingEventRecorder(EventLevel.Informational)
                {
                    // A synchronous listener may reenter without blocking behind the observer.
                    OnWritten = entry =>
                    {
                        if (entry.EventId == 16)
                        {
                            application.Context.CheckAsync(timeout.Token).AsTask().GetAwaiter().GetResult();
                        }
                    },
                };

                // Act: exercise racing polls for at least 150 ms and 32 complete observations.
                int polls = 0;
                var elapsed = Stopwatch.StartNew();
                do
                {
                    await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
                    {
                        (await application.Context.CheckAsync(timeout.Token)).Status.ShouldBe(HealthStatus.Degraded);
                        Interlocked.Increment(ref polls);
                    }, timeout.Token)));
                    elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
                }
                while (elapsed.Elapsed < TimeSpan.FromMilliseconds(150) || polls < 32);

                // Assert: one degraded event, with the actual worker's progress.
                polls.ShouldBeGreaterThanOrEqualTo(32);
                var degraded = Event(recorder, 16, "HealthDegraded", _healthPayload);
                degraded.Level.ShouldBe(EventLevel.Warning);
                degraded.Payload!.Skip(1).ShouldBe(["sql", "health", "Provisioning", "", "AddConstraint", "items", 1, 1, 1L, 3L]);
                var sqlEngine = (SqlDatabaseEngine)application.Context.Engines.Single();
                var database = await sqlEngine.OpenDatabaseAsync("health", timeout.Token);
                // Capture the gated instance before failure can make an open schedule a retry.
                release.Set();
                while (fail ? sqlEngine.OfflineDatabases.Count == 0 : database.Provisioning is not null)
                {
                    await Task.Delay(1, timeout.Token);
                }
                for (int poll = 0; poll < 8; poll++)
                {
                    (await application.Context.CheckAsync(timeout.Token)).Status.ShouldBe(fail ? HealthStatus.Unhealthy : HealthStatus.Healthy);
                }
                calls.ShouldBe(fail ? 2 : 3);
                recorder.Events.ShouldNotContain(entry => entry.EventId == 0);
                if (fail)
                {
                    var changes = recorder.Events.Where(entry => entry.EventId == 16).ToArray();
                    changes.Length.ShouldBe(2);
                    changes[1].PayloadNames.ShouldBe(_healthPayload);
                    changes[1].Payload![3].ShouldBe("Offline");
                    changes[1].Payload![4].ShouldBe("ProvisioningFailure");
                    recorder.Events.ShouldNotContain(entry => entry.EventId == 17);
                }
                else
                {
                    var restored = Event(recorder, 17, "HealthRestored", _healthPayload);
                    restored.Level.ShouldBe(EventLevel.Informational);
                    restored.Payload![3].ShouldBe("Provisioning");
                }
            }
            finally { release.Set(); }
        }
        finally
        {
            release.Set();
            if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Health tracing: offline and reopened database report once")]
    public async Task Health_OfflineReopen_ShouldRestoreOnce()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        FailTwice? worker = null;
        var builder = SqlDatabaseEngine.CreateBuilder("health-offline");
        builder.Options.WorkerFailureWindow = TimeSpan.FromTicks(1);
        builder.Options.WorkerFailureMinimumPasses = 2;
        builder.AddWorker(_ => worker = new FailTwice("health-offline/probe"));
        await using var engine = builder.Build();
        await engine.CreateDatabaseAsync(App, timeout.Token);
        var appBuilder = new DatabaseApplicationBuilder(new DatabaseApplicationOptions { ReopenOfflineDatabases = false });
        appBuilder.AddEngine(engine);
        await using var app = appBuilder.Build();
        using var recorder = new HostingEventRecorder(EventLevel.Informational);

        // Act
        worker.ShouldNotBeNull().RunFailingPasses();
        while (engine.OfflineDatabases.Count == 0) { await Task.Delay(1, timeout.Token); }
        for (int poll = 0; poll < 3; poll++) { (await app.Context.CheckAsync(timeout.Token)).Status.ShouldBe(HealthStatus.Unhealthy); }
        await engine.OpenDatabaseAsync(App, timeout.Token);
        for (int poll = 0; poll < 3; poll++) { (await app.Context.CheckAsync(timeout.Token)).Status.ShouldBe(HealthStatus.Healthy); }

        // Assert
        Event(recorder, 16, "HealthDegraded", _healthPayload).Payload![4].ShouldBe("CheckpointFailures");
        Event(recorder, 17, "HealthRestored", _healthPayload).Payload![4].ShouldBe("CheckpointFailures");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Health tracing: disabled real health retains the core allocation baseline")]
    public async Task CheckAsync_Disabled_ShouldMatchCoreAllocations()
    {
        // Arrange
        DatabaseHostingEventSource.Log.IsEnabled().ShouldBeFalse();
        await using var engine = SqlDatabaseEngine.Create("health-allocation", new SqlDatabaseEngineOptions());
        await engine.CreateDatabaseAsync("healthy", CancellationToken.None);
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine(engine);
        await using var app = builder.Build();
        for (int warm = 0; warm < 100; warm++) { Measure(false); Measure(true); }

        // Act
        long measured = long.MaxValue;
        long baseline = long.MaxValue;
        for (int round = 0; round < 3; round++)
        {
            measured = Math.Min(measured, Measure(false));
            baseline = Math.Min(baseline, Measure(true));
        }

        // Assert
        (measured - baseline).ShouldBe(0);

        long Measure(bool core)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 100; index++)
            {
                var check = core ? app.Context.CheckCoreAsync(CancellationToken.None) : app.Context.CheckAsync(CancellationToken.None);
                check.IsCompletedSuccessfully.ShouldBeTrue();
                check.GetAwaiter().GetResult().Status.ShouldBe(HealthStatus.Healthy);
            }
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }

    private static SqlSchema HealthSchema(bool check) => SqlSchema.Create("health", schema => schema.Table<HealthItem>("items", table =>
    {
        table.Column(row => row.Id);
        if (check) { table.Check("ck_health", "health_probe(Id)"); }
    }));

    private sealed record HealthItem(int Id);
}
