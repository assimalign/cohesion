using System;

using Shouldly;

namespace Assimalign.Cohesion.Web.Health.Tests;

public class HealthReportTests
{
    [Fact(DisplayName = "Cohesion Test [Health] - HealthReport: empty report aggregates to Healthy")]
    public void Aggregate_WhenNoEntries_ShouldBeHealthy()
    {
        HealthReport report = new(new Dictionary<string, HealthReportEntry>(), TimeSpan.Zero);

        report.Status.ShouldBe(HealthStatus.Healthy);
        report.Entries.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Health] - HealthReport: aggregate is the worst entry status")]
    public void Aggregate_WhenMixedEntries_ShouldBeWorstStatus()
    {
        var entries = new Dictionary<string, HealthReportEntry>
        {
            ["a"] = Entry(HealthStatus.Healthy),
            ["b"] = Entry(HealthStatus.Degraded),
            ["c"] = Entry(HealthStatus.Healthy),
        };

        HealthReport report = new(entries, TimeSpan.FromSeconds(1));

        report.Status.ShouldBe(HealthStatus.Degraded);
    }

    [Fact(DisplayName = "Cohesion Test [Health] - HealthReport: a single unhealthy entry drives the aggregate")]
    public void Aggregate_WhenAnyUnhealthy_ShouldBeUnhealthy()
    {
        var entries = new Dictionary<string, HealthReportEntry>
        {
            ["a"] = Entry(HealthStatus.Degraded),
            ["b"] = Entry(HealthStatus.Unhealthy),
        };

        HealthReport report = new(entries, TimeSpan.Zero);

        report.Status.ShouldBe(HealthStatus.Unhealthy);
    }

    [Fact(DisplayName = "Cohesion Test [Health] - HealthReport: null entries throws")]
    public void Constructor_WhenEntriesNull_ShouldThrow()
    {
        Should.Throw<ArgumentNullException>(() => new HealthReport(null!, TimeSpan.Zero));
    }

    private static HealthReportEntry Entry(HealthStatus status)
        => new(status, description: null, TimeSpan.Zero, exception: null, data: null, tags: null);
}
