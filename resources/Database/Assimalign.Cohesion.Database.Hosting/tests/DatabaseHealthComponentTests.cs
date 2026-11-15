using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Database.Hosting.Internal;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Web.Health;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

[Collection(DatabaseProvisioningHealthCollection.Name)]
public sealed class DatabaseHealthComponentTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Component health contains an inventory failure to its engine (#1790)")]
    public async Task GetHealthAsync_WithThrowingInventory_ShouldFailClosedForOnlyItsEngine()
    {
        // Arrange
        await using var failed = new RecordingEngine("failed") { InventoryException = new IOException("private path") };
        await using var other = new RecordingEngine("other");
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine(failed);
        builder.AddEngine(other);
        await using var application = builder.Build();
        await ((IHost)application).StartAsync(CancellationToken.None);

        // Act
        var observations = await application.Context.GetHealthAsync(CancellationToken.None);

        // Assert
        var failure = observations.Single(value => value.Name == "engine/failed");
        failure.Status.ShouldBe(DatabaseHealthStatus.Unhealthy);
        failure.Description.ShouldNotBeNull().ShouldContain("IOException", Case.Sensitive);
        failure.Description.ShouldNotContain("private path", Case.Sensitive);
        observations.Single(value => value.Name == "engine/other").Status.ShouldBe(DatabaseHealthStatus.Healthy);
        observations.Single(value => value.Name == "database.accepting").Status.ShouldBe(DatabaseHealthStatus.Healthy);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Discovery is non-evaluating, ordered and survives a broken inventory (#1790)")]
    public async Task HealthComponentNames_WithTwoEngines_ShouldDiscoverLiveOrderedMembership()
    {
        // Arrange
        await using var first = SqlDatabaseEngine.CreateBuilder("z/first").Build();
        await first.CreateDatabaseAsync("Zebra");
        await first.CreateDatabaseAsync("alpha");
        await using var second = SqlDatabaseEngine.CreateBuilder("a-second").Build();
        await second.CreateDatabaseAsync("Other");
        await using var broken = new RecordingEngine("broken") { InventoryException = new IOException("private path") };
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine(first);
        builder.AddEngine(second);
        builder.AddEngine(broken);
        await using var application = builder.Build();

        // Act
        var membership = application.Context.HealthComponentNames;
        var observations = await application.Context.GetHealthAsync(CancellationToken.None);
        await first.DropDatabaseAsync("alpha");
        var vanished = await application.Context.GetHealthAsync("database/z%2Ffirst/alpha", CancellationToken.None);

        // Assert
        membership.ShouldBe(["engine/z%2Ffirst", "database/z%2Ffirst/alpha", "database/z%2Ffirst/zebra",
            "engine/a-second", "database/a-second/other", "engine/broken", "database.accepting"]);
        observations.Select(value => value.Name).ShouldBe(membership);
        broken.OfflineReads.ShouldBe(0);
        application.Context.HealthComponentNames.ShouldNotContain("database/z%2Ffirst/alpha");
        vanished.Status.ShouldBe(DatabaseHealthStatus.Unhealthy);
        vanished.Description.ShouldNotBeNull().ShouldContain("not open", Case.Sensitive);
        var engine = observations.Single(value => value.Name == "engine/z%2Ffirst");
        engine.Data["name"].ShouldBe("z/first");
        engine.Data.Values.ShouldNotContain("a-second");
        observations.Single(value => value.Name == "engine/a-second").Data["name"].ShouldBe("a-second");
        engine.Data.Keys.ShouldNotContain(key => key.StartsWith("provisioning.", StringComparison.Ordinal) || key.StartsWith("offline.", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Single component evaluation reads only its engine and accepting reads no inventory (#1790)")]
    public async Task GetHealthAsync_WithOneComponent_ShouldIsolateEvaluationAndFailures()
    {
        // Arrange
        int failedReads = 0;
        int otherReads = 0;
        await using var failed = new RecordingEngine("failed")
        {
            OpenInventory = () => { failedReads++; throw new IOException("private path", new InvalidOperationException("secret")); },
        };
        await using var other = new RecordingEngine("other") { OpenInventory = () => { otherReads++; return []; } };
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine(failed);
        builder.AddEngine(other);
        await using var application = builder.Build();
        await ((IHost)application).StartAsync(CancellationToken.None);

        // Act
        var accepting = await application.Context.GetHealthAsync("database.accepting", CancellationToken.None);
        int readsAfterAccepting = failedReads + otherReads;
        var healthy = await application.Context.GetHealthAsync("engine/other", CancellationToken.None);
        int failedReadsAfterOther = failedReads;
        var failure = await application.Context.GetHealthAsync("database/failed/retained", CancellationToken.None);
        failed.OpenInventory = () => throw new OperationCanceledException(new CancellationToken(true));
        var unrelatedCancellation = await application.Context.GetHealthAsync("engine/failed", CancellationToken.None);

        // Assert
        accepting.Status.ShouldBe(DatabaseHealthStatus.Healthy);
        readsAfterAccepting.ShouldBe(0);
        healthy.Status.ShouldBe(DatabaseHealthStatus.Healthy);
        failedReadsAfterOther.ShouldBe(0);
        otherReads.ShouldBe(1);
        failure.Status.ShouldBe(DatabaseHealthStatus.Unhealthy);
        failure.Description.ShouldBe("Database engine 'failed' health evaluation failed: IOException (InvalidOperationException).");
        unrelatedCancellation.Status.ShouldBe(DatabaseHealthStatus.Unhealthy);
        unrelatedCancellation.Description.ShouldNotBeNull().ShouldContain("OperationCanceledException", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Hosting] - Disposal during inventory fails closed for components and HTTP without a timing race (#1790)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetHealthAsync_DisposedDuringInventory_ShouldFailClosed(bool aggregate)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = new RecordingEngine("racing")
        {
            OpenInventory = () => { entered.SetResult(); release.Wait(guard.Token); return []; },
        };
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine(engine);
        await using var application = builder.Build();

        // Act
        IHealthChecksBuilder healthBuilder = new HealthChecksBuilder();
        healthBuilder.AddCheck("database", (_, token) => application.Context.CheckAsync(token));
        var health = healthBuilder.Build();
        var check = Task.Run(async () =>
        {
            if (aggregate)
            {
                var report = await health.CheckHealthAsync(cancellationToken: guard.Token);
                var entry = report.Entries["database"];
                return (Status: (int)report.Status, entry.Description, Failure: entry.Exception);
            }
            var component = await application.Context.GetHealthAsync("engine/racing", guard.Token);
            return (Status: (int)component.Status, component.Description, Failure: (Exception?)null);
        }, guard.Token);
        try
        {
            await entered.Task.WaitAsync(guard.Token);
            await engine.DisposeAsync();
        }
        finally { release.Set(); }
        var result = await check.WaitAsync(guard.Token);

        // Assert
        result.Status.ShouldBe((int)DatabaseHealthStatus.Unhealthy);
        if (aggregate) { result.Failure.ShouldBeOfType<ObjectDisposedException>(); }
        else { result.Description.ShouldNotBeNull().ShouldContain("ObjectDisposedException", Case.Sensitive); }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Empty component evaluation observes caller cancellation (#1790)")]
    public async Task GetHealthAsync_WithZeroEnginesAndCancelledToken_ShouldCancelBothForms()
    {
        // Arrange
        await using var application = DatabaseApplication.CreateBuilder().Build();
        var token = new CancellationToken(true);

        // Act / Assert
        await Should.ThrowAsync<OperationCanceledException>(() => application.Context.GetHealthAsync(token).AsTask());
        await Should.ThrowAsync<OperationCanceledException>(() => application.Context.GetHealthAsync("database.accepting", token).AsTask());
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - The application context resolves as its concrete and root interface identities (#1790)")]
    public async Task Build_WithContextRegistration_ShouldResolveTheSameApplicationOwnedInstance()
    {
        // Arrange
        var builder = DatabaseApplication.CreateBuilder();
        await using var application = builder.Build();

        // Act
        var concrete = application.Context.Services.GetRequiredService<DatabaseApplicationContext>();
        var contract = application.Context.Services.GetRequiredService<IDatabaseApplicationContext>();

        // Assert
        concrete.ShouldBeSameAs(application.Context);
        contract.ShouldBeSameAs(concrete);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Unknown engine state is classified Unhealthy (#1790)")]
    public void Engine_WithUnknownState_ShouldFailClosed()
    {
        // Arrange: DatabaseEngine.State can currently produce only Running, Faulted or Disposed.
        var observations = new DatabaseHealthObservations();

        // Act
        observations.Engine("unknown", (EngineState)byte.MaxValue, [], "engine.0.");
        var result = observations.Complete(HealthCheckResult.Healthy());

        // Assert
        result.Single(value => value.Name == "engine/unknown").Status.ShouldBe(DatabaseHealthStatus.Unhealthy);
        // The aggregate uses this same classifier; the base cannot currently produce an unknown state.
        DatabaseHealthObservations.EngineStatus((EngineState)byte.MaxValue).ShouldBe(HealthStatus.Unhealthy);
    }
}
