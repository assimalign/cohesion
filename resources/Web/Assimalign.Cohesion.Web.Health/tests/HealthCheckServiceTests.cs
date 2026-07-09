using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

namespace Assimalign.Cohesion.Web.Health.Tests;

public class HealthCheckServiceTests
{
    [Fact(DisplayName = "Cohesion Test [Health] - Service: runs all checks and aggregates the worst status")]
    public async Task CheckHealthAsync_WhenChecksRegistered_ShouldRunAllAndAggregate()
    {
        IHealthCheckService service = HealthChecks.CreateBuilder()
            .AddCheck("ok", () => HealthCheckResult.Healthy())
            .AddCheck("slow", () => HealthCheckResult.Degraded())
            .Build();

        HealthReport report = await service.CheckHealthAsync();

        report.Entries.Count.ShouldBe(2);
        report.Status.ShouldBe(HealthStatus.Degraded);
        report.Entries["ok"].Status.ShouldBe(HealthStatus.Healthy);
        report.Entries["slow"].Status.ShouldBe(HealthStatus.Degraded);
    }

    [Fact(DisplayName = "Cohesion Test [Health] - Service: a thrown check reports its failure status and captures the exception")]
    public async Task CheckHealthAsync_WhenCheckThrows_ShouldReportFailureStatus()
    {
        var exception = new InvalidOperationException("db unreachable");
        IHealthCheckService service = HealthChecks.CreateBuilder()
            .AddCheck("db", StubCheck.Throwing(exception), failureStatus: HealthStatus.Unhealthy)
            .Build();

        HealthReport report = await service.CheckHealthAsync();

        HealthReportEntry entry = report.Entries["db"];
        entry.Status.ShouldBe(HealthStatus.Unhealthy);
        entry.Exception.ShouldBeSameAs(exception);
        entry.Description.ShouldBe("db unreachable");
    }

    [Fact(DisplayName = "Cohesion Test [Health] - Service: a thrown check honors a custom failure status")]
    public async Task CheckHealthAsync_WhenCheckThrowsWithDegradedPolicy_ShouldReportDegraded()
    {
        IHealthCheckService service = HealthChecks.CreateBuilder()
            .AddCheck("cache", StubCheck.Throwing(new Exception("miss")), failureStatus: HealthStatus.Degraded)
            .Build();

        HealthReport report = await service.CheckHealthAsync();

        report.Entries["cache"].Status.ShouldBe(HealthStatus.Degraded);
        report.Status.ShouldBe(HealthStatus.Degraded);
    }

    [Fact(DisplayName = "Cohesion Test [Health] - Service: a check exceeding its timeout is folded into a failure entry")]
    public async Task CheckHealthAsync_WhenCheckExceedsTimeout_ShouldReportFailureWithoutThrowing()
    {
        IHealthCheckService service = HealthChecks.CreateBuilder()
            .AddCheck("hang", StubCheck.Blocking(), failureStatus: HealthStatus.Unhealthy, timeout: TimeSpan.FromMilliseconds(50))
            .Build();

        HealthReport report = await service.CheckHealthAsync();

        report.Entries["hang"].Status.ShouldBe(HealthStatus.Unhealthy);
        report.Entries["hang"].Description.ShouldNotBeNull();
        report.Entries["hang"].Description!.ShouldContain("timed out");
    }

    [Fact(DisplayName = "Cohesion Test [Health] - Service: caller cancellation propagates out")]
    public async Task CheckHealthAsync_WhenCallerCancels_ShouldThrowOperationCanceled()
    {
        IHealthCheckService service = HealthChecks.CreateBuilder()
            .AddCheck("hang", StubCheck.Blocking())
            .Build();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await service.CheckHealthAsync(predicate: null, cts.Token));
    }

    [Fact(DisplayName = "Cohesion Test [Health] - Service: predicate selects the readiness slice only")]
    public async Task CheckHealthAsync_WhenPredicateGiven_ShouldRunOnlyMatchingChecks()
    {
        IHealthCheckService service = HealthChecks.CreateBuilder()
            .AddCheck("ready-db", () => HealthCheckResult.Healthy(), tags: new[] { HealthTags.Ready })
            .AddCheck("live-self", () => HealthCheckResult.Unhealthy(), tags: new[] { HealthTags.Live })
            .Build();

        HealthReport report = await service.CheckHealthAsync(HealthCheckPredicates.Ready);

        report.Entries.Keys.ShouldBe(new[] { "ready-db" });
        report.Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact(DisplayName = "Cohesion Test [Health] - Service: a predicate matching nothing yields a healthy empty report")]
    public async Task CheckHealthAsync_WhenPredicateMatchesNothing_ShouldBeHealthyAndEmpty()
    {
        IHealthCheckService service = HealthChecks.CreateBuilder()
            .AddCheck("live-self", () => HealthCheckResult.Unhealthy(), tags: new[] { HealthTags.Live })
            .Build();

        HealthReport report = await service.CheckHealthAsync(HealthCheckPredicates.Ready);

        report.Entries.ShouldBeEmpty();
        report.Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact(DisplayName = "Cohesion Test [Health] - Service: the report entry carries the registration tags")]
    public async Task CheckHealthAsync_WhenCheckTagged_ShouldSurfaceTagsOnEntry()
    {
        IHealthCheckService service = HealthChecks.CreateBuilder()
            .AddCheck("db", () => HealthCheckResult.Healthy(), tags: new[] { HealthTags.Ready, "sql" })
            .Build();

        HealthReport report = await service.CheckHealthAsync();

        report.Entries["db"].Tags.ShouldContain(HealthTags.Ready);
        report.Entries["db"].Tags.ShouldContain("sql");
    }
}
