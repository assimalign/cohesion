using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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
/// Until phase 4 of the concrete-types plan these tests drove a real SQL engine through the SQL
/// model's fault-injecting storage strategy. The strategies are internal since then (D9), and a
/// registered worker derives from <see cref="DatabaseEngineWorker"/>, whose loop lets nothing
/// escape, so the tests drive the health mapping through the engine double instead: a guided
/// worker whose pass the test runs and fails, and an engine that reports the state and the
/// offline databases the test sets. The real engines' fault paths are their models' own suites'
/// (the SQL model's <c>SqlWorkerResilienceTests</c> and <c>SqlStorageOperationsTests</c>).
/// </remarks>
public sealed class DatabaseWorkerHealthTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Context health: a failing worker is degraded and named, healthy once it recovers, and an offline database is unhealthy until the reopen")]
    public async Task CheckAsync_WorkerFaultsAndAnOfflineDatabase_ShouldFollowTheWorkerAndTheDatabase()
    {
        // Arrange: an engine whose checkpointer the test drives pass by pass.
        var worker = new ReportingWorker("sql/checkpoint", DatabaseEngineWorkerKind.Checkpoint);
        var engine = new RecordingEngine("sql", workers: [worker]);
        var options = new DatabaseApplicationOptions();
        options.Engines.Add(engine);
        await using var application = new DatabaseApplication(options);
        HealthContribution before = await application.Context.CheckAsync(CancellationToken.None);

        // Act: the checkpointer's pass fails for the database, and the engine folds it as Faulted.
        worker.Failure = new IOException("Injected page write failure");
        bool failed = !worker.RunIteration(CancellationToken.None);
        engine.Report(EngineState.Faulted);
        HealthContribution degraded = await application.Context.CheckAsync(CancellationToken.None);

        worker.Failure = null;
        Thread.Sleep(DatabaseEngineWorker.FailureBackoff);
        bool recovered = worker.RunIteration(CancellationToken.None) && worker.Fault is null;
        engine.Report(EngineState.Running);
        HealthContribution healthy = await application.Context.CheckAsync(CancellationToken.None);

        // Then a file header write takes the database offline, which the engine lists.
        engine.Offline = ["app"];
        HealthContribution unhealthy = await application.Context.CheckAsync(CancellationToken.None);

        engine.Offline = [];
        HealthContribution reopened = await application.Context.CheckAsync(CancellationToken.None);

        // Assert: degraded names the worker and the type of its failure, and the data carries it;
        // the failure's message, which can carry file paths, stays out of the unauthenticated
        // health output.
        before.Status.ShouldBe(HealthStatus.Healthy);
        failed.ShouldBeTrue();
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

        unhealthy.Status.ShouldBe(HealthStatus.Unhealthy);
        unhealthy.Description.ShouldNotBeNull().ShouldContain("sql/app");
        unhealthy.Description.ShouldContain("file header write");
        unhealthy.Data.ShouldNotBeNull()["engine.0.state"].ShouldBe(nameof(EngineState.Running));

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
    /// database <c>app</c> while it is set, and finishes that database's work otherwise.
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
