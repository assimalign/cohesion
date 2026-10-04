using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Hosting.Health;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

/// <summary>
/// The application's health while a real engine's workers meet device faults (#1268): a worker
/// that keeps failing makes it degraded and is named with its failure, a worker that completes a
/// pass again makes it healthy, and a database a failed header write took offline makes it
/// unhealthy until the database is reopened.
/// </summary>
public sealed class DatabaseWorkerHealthTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Context health: a failing worker is degraded and named, healthy once it recovers, and a failed header write is unhealthy until the reopen")]
    public async Task CheckAsync_WorkerFaultsOnARealEngine_ShouldFollowTheWorkerAndTheDatabase()
    {
        // Arrange: a SQL engine whose checkpointer looks every 100 ms, over device faults.
        var strategy = new FaultInjectingJournalSqlStorageStrategy(durable: true);
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions
        {
            EngineName = "sql",
            StorageStrategy = strategy,
            CheckpointInterval = TimeSpan.FromMilliseconds(100),
            PageWriteBackInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
        });
        IDatabase database = await engine.CreateDatabaseAsync("app");
        await using (IDatabaseSession setup = await database.CreateSessionAsync())
        {
            await setup.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, payload VARCHAR(200))");
        }

        var options = new DatabaseApplicationOptions();
        options.Engines.Add(engine);
        await using var application = new DatabaseApplication(options);
        var faults = strategy.Faults("app");
        HealthContribution before = await application.Context.CheckAsync(CancellationToken.None);

        // Act: the checkpointer's page writes fail.
        faults.FailPageWrites = true;
        await InsertAsync(database, 0, 10);
        bool faulted = await Eventually(() => engine.State == EngineState.Faulted);
        HealthContribution degraded = await application.Context.CheckAsync(CancellationToken.None);

        faults.FailPageWrites = false;
        bool recovered = await Eventually(() => engine.State == EngineState.Running);
        HealthContribution healthy = await application.Context.CheckAsync(CancellationToken.None);

        // Then the next checkpoint's header slot write fails.
        faults.FailHeaderWrites = true;
        await InsertAsync(database, 10, 1);
        bool offline = await Eventually(() => engine.OfflineDatabases.Count == 1);
        HealthContribution unhealthy = await application.Context.CheckAsync(CancellationToken.None);

        faults.Clear();
        await engine.OpenDatabaseAsync("app");
        HealthContribution reopened = await application.Context.CheckAsync(CancellationToken.None);

        // Assert: degraded names the worker and the type of its failure, and the data carries it;
        // the failure's message, which can carry file paths, stays out of the unauthenticated
        // health output.
        before.Status.ShouldBe(HealthStatus.Healthy);
        faulted.ShouldBeTrue();
        degraded.Status.ShouldBe(HealthStatus.Degraded);
        degraded.Description.ShouldNotBeNull().ShouldContain("sql/checkpoint");
        degraded.Description.ShouldContain("IOException");
        degraded.Description.ShouldContain("returns to Running");
        degraded.Description.ShouldNotContain("Injected page write failure");
        IReadOnlyDictionary<string, object> data = degraded.Data.ShouldNotBeNull();
        string prefix = data.Single(entry => entry.Value is "sql/checkpoint").Key[..^".name".Length];
        data[$"{prefix}.fault"].ShouldBeOfType<string>().ShouldContain("IOException");
        data.Values.OfType<string>().ShouldNotContain(value => value.Contains("Injected page write failure", StringComparison.Ordinal));
        ((int)data[$"{prefix}.consecutiveFailures"]).ShouldBeGreaterThanOrEqualTo(1);
        ((long)data[$"{prefix}.failureCount"]).ShouldBeGreaterThanOrEqualTo(1);

        recovered.ShouldBeTrue();
        healthy.Status.ShouldBe(HealthStatus.Healthy);
        healthy.Data.ShouldNotBeNull().ContainsKey($"{prefix}.fault").ShouldBeFalse();

        offline.ShouldBeTrue();
        unhealthy.Status.ShouldBe(HealthStatus.Unhealthy);
        unhealthy.Description.ShouldNotBeNull().ShouldContain("sql/app");
        unhealthy.Description.ShouldContain("file header write");
        unhealthy.Data.ShouldNotBeNull()["engine.0.state"].ShouldBe(nameof(EngineState.Running));

        reopened.Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Context health: a registered worker whose loop failed is degraded until disposal, and says so")]
    public async Task CheckAsync_RegisteredWorkerLoopFailed_ShouldSayTheEngineStaysFaultedUntilDisposed()
    {
        // Arrange: a worker without the guided base whose loop throws once.
        var builder = SqlDatabaseEngine.CreateBuilder();
        builder.AddWorker(_ => new EscapingWorker());
        await using var engine = builder.Build();
        var options = new DatabaseApplicationOptions();
        options.Engines.Add(engine);
        await using var application = new DatabaseApplication(options);

        // Act
        bool faulted = await Eventually(() => engine.State == EngineState.Faulted);
        HealthContribution degraded = await application.Context.CheckAsync(CancellationToken.None);

        // Assert: no guided worker holds a failure, so the description does not promise a return
        // to Running; it says the engine stays Faulted until it is disposed.
        faulted.ShouldBeTrue();
        degraded.Status.ShouldBe(HealthStatus.Degraded);
        degraded.Description.ShouldNotBeNull().ShouldContain("A registered worker's loop failed on");
        degraded.Description.ShouldContain("until it is disposed");
        degraded.Description.ShouldNotContain("returns to Running");
        degraded.Description.ShouldNotContain("Failing workers");
    }

    private static async Task InsertAsync(IDatabase database, int first, int count)
    {
        await using IDatabaseSession session = await database.CreateSessionAsync();
        for (int id = first; id < first + count; id++)
        {
            await session.ExecuteAsync($"INSERT INTO t (id, payload) VALUES ({id}, '{new string('x', 150)}')");
        }
    }

    private static async Task<bool> Eventually(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > Timeout)
            {
                return false;
            }

            await Task.Delay(10);
        }

        return true;
    }

    /// <summary>A worker without the guided base whose first loop throws; later loops run until cancelled.</summary>
    private sealed class EscapingWorker : IDatabaseEngineWorker
    {
        private int _runs;

        public string Name => "escaping";

        public DatabaseEngineWorkerKind Kind => DatabaseEngineWorkerKind.IndexMaintenance;

        public TimeSpan Interval => TimeSpan.FromSeconds(1);

        public void Run(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _runs) == 1)
            {
                throw new InvalidOperationException("The worker's loop failed.");
            }

            cancellationToken.WaitHandle.WaitOne();
        }
    }
}
