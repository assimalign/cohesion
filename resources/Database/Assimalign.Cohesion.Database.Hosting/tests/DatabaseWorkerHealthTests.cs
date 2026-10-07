using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Hosting.Health;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

/// <summary>
/// The application's health while an engine's workers fail and its databases go offline (#1268):
/// a worker that keeps failing makes it degraded and is named with its failure, a worker that
/// completes a pass again makes it healthy, and an offline database makes it unhealthy until the
/// database is reopened.
/// </summary>
/// <remarks>
/// Until phase 4 of the concrete-types plan these tests failed a real SQL engine's checkpointer
/// and header writes through the SQL model's fault-injecting storage strategy, internal since then
/// (D9). The worker half still runs on a real SQL engine: a guided worker registered through the
/// typed <see cref="SqlDatabaseEngineBuilder.AddWorker"/> fails a pass the test runs, and the root
/// engine base folds the engine's state from it. The offline half here drives the engine double,
/// which reports the offline databases the test sets, through the root interface the double
/// implements. A real engine's database goes offline in <c>DatabaseReopenTests</c>: since owner
/// decision 25 such a registered worker takes it offline once its failures reach the engine's limit.
/// </remarks>
public sealed class DatabaseWorkerHealthTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Context health: a failing worker of a real engine is degraded and named, and healthy once it recovers")]
    public async Task CheckAsync_WorkerFaultsOnARealEngine_ShouldFollowTheWorker()
    {
        // Arrange: a SQL engine with a guided worker the test drives pass by pass, beside its
        // built-in workers.
        ReportingWorker? registered = null;
        var builder = SqlDatabaseEngine.CreateBuilder();
        builder.EngineName = "sql";
        builder.AddWorker(_ => registered = new ReportingWorker("sql/probe", DatabaseEngineWorkerKind.Checkpoint));
        await using var engine = builder.Build();
        var worker = registered.ShouldNotBeNull();
        var options = new DatabaseApplicationOptions();
        options.Engines.Add(engine);
        await using var application = new DatabaseApplication(options);
        HealthContribution before = await application.Context.CheckAsync(CancellationToken.None);

        // Act: the worker's pass fails for the database, and the engine base folds it as Faulted.
        worker.Failure = new IOException("Injected page write failure");
        bool failed = !worker.RunIteration(CancellationToken.None);
        var faulted = engine.State;
        HealthContribution degraded = await application.Context.CheckAsync(CancellationToken.None);

        worker.Failure = null;
        Thread.Sleep(DatabaseEngineWorker.FailureBackoff);
        bool recovered = worker.RunIteration(CancellationToken.None) && worker.Fault is null;
        var running = engine.State;
        HealthContribution healthy = await application.Context.CheckAsync(CancellationToken.None);

        // Assert: degraded names the worker and the type of its failure, and the data carries it;
        // the failure's message, which can carry file paths, stays out of the unauthenticated
        // health output.
        engine.Workers.ShouldContain(worker);
        before.Status.ShouldBe(HealthStatus.Healthy);
        failed.ShouldBeTrue();
        faulted.ShouldBe(EngineState.Faulted);
        degraded.Status.ShouldBe(HealthStatus.Degraded);
        degraded.Description.ShouldNotBeNull().ShouldContain("sql/probe");
        degraded.Description.ShouldContain("IOException");
        degraded.Description.ShouldContain("returns to Running");
        degraded.Description.ShouldNotContain("Injected page write failure");
        IReadOnlyDictionary<string, object> data = degraded.Data.ShouldNotBeNull();
        string prefix = data.Single(entry => entry.Value is "sql/probe").Key[..^".name".Length];
        data[$"{prefix}.fault"].ShouldBeOfType<string>().ShouldContain("IOException");
        data.Values.OfType<string>().ShouldNotContain(value => value.Contains("Injected page write failure", StringComparison.Ordinal));
        ((int)data[$"{prefix}.consecutiveFailures"]).ShouldBeGreaterThanOrEqualTo(1);
        ((long)data[$"{prefix}.failureCount"]).ShouldBeGreaterThanOrEqualTo(1);

        recovered.ShouldBeTrue();
        running.ShouldBe(EngineState.Running);
        healthy.Status.ShouldBe(HealthStatus.Healthy);
        healthy.Data.ShouldNotBeNull().ContainsKey($"{prefix}.fault").ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Context health: an offline database is unhealthy until the reopen, while its engine runs")]
    public async Task CheckAsync_OfflineDatabase_ShouldBeUnhealthyUntilTheReopen()
    {
        // Arrange: a running engine whose database the test takes offline, as a failed file
        // header write does.
        var engine = new RecordingEngine("sql");
        var options = new DatabaseApplicationOptions();
        options.Engines.Add(engine);
        await using var application = new DatabaseApplication(options);

        // Act
        engine.Offline = ["app"];
        HealthContribution unhealthy = await application.Context.CheckAsync(CancellationToken.None);

        engine.Offline = [];
        HealthContribution reopened = await application.Context.CheckAsync(CancellationToken.None);

        // Assert
        unhealthy.Status.ShouldBe(HealthStatus.Unhealthy);
        unhealthy.Description.ShouldNotBeNull().ShouldContain("Offline databases: sql/app.");
        unhealthy.Description.ShouldContain("every operation on them is refused until each is reopened");
        unhealthy.Data.ShouldNotBeNull()["engine.0.state"].ShouldBe(nameof(EngineState.Running));
        unhealthy.Data["engine.0.offline.0.name"].ShouldBe("app");

        // An engine of the root interface does not say what took the database offline.
        unhealthy.Data.ContainsKey("engine.0.offline.0.cause").ShouldBeFalse();

        reopened.Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Context health: a registered worker whose loop failed is degraded until disposal, and says so")]
    public async Task CheckAsync_RegisteredWorkerLoopFailed_ShouldSayTheEngineStaysFaultedUntilDisposed()
    {
        // Arrange: a Faulted engine none of whose workers is a guided worker holding a failure, as
        // an engine reports after a registered worker's loop escaped (since phase 4 of the
        // concrete-types plan only an engine of the root interface can register such a worker).
        var engine = new RecordingEngine(
            "escaping-engine",
            EngineState.Faulted,
            [new RecordingEngineWorker("escaping", DatabaseEngineWorkerKind.IndexMaintenance, TimeSpan.FromSeconds(1))]);
        var options = new DatabaseApplicationOptions();
        options.Engines.Add(engine);
        await using var application = new DatabaseApplication(options);

        // Act
        HealthContribution degraded = await application.Context.CheckAsync(CancellationToken.None);

        // Assert: no guided worker holds a failure, so the description does not promise a return
        // to Running; it says the engine stays Faulted until it is disposed.
        degraded.Status.ShouldBe(HealthStatus.Degraded);
        degraded.Description.ShouldNotBeNull().ShouldContain("A registered worker's loop failed on");
        degraded.Description.ShouldContain("until it is disposed");
        degraded.Description.ShouldNotContain("returns to Running");
        degraded.Description.ShouldNotContain("Failing workers");
    }

    /// <summary>
    /// A guided worker whose pass the test runs: each pass reports <see cref="Failure"/> for the
    /// database <c>app</c> while it is set, and finishes that database's work otherwise. Its
    /// one-hour interval keeps the engine's pump from running a pass the test did not ask for
    /// after the first.
    /// </summary>
    private sealed class ReportingWorker : DatabaseEngineWorker
    {
        public ReportingWorker(string name, DatabaseEngineWorkerKind kind)
            : base(name, kind, TimeSpan.FromHours(1))
        {
        }

        public Exception? Failure { get; set; }

        protected override void RunIterationCore(CancellationToken cancellationToken)
        {
            if (!BeginDatabase("app"))
            {
                return;
            }

            if (Failure is { } failure)
            {
                ReportFailure("app", failure);
            }
        }
    }
}
