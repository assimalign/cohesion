using System;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Hosting.Health;
using Assimalign.Cohesion.Testing;
using Assimalign.Cohesion.Web.Health;

using Assimalign.Cohesion.Web.Hosting.Health;
using Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

[Collection(nameof(HealthContributorTelemetryCollection))]
public sealed class HealthContributorEventSourceTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Contributor: repeated unhealthy results report one Web.Health transition")]
    public async Task AddContributor_WhenRepeatedlyUnhealthy_ShouldReportOneWorsenedTransition()
    {
        // Arrange
        EventSource source = await GetHealthEventSourceAsync();
        using var recorder = new EventSourceRecorder(source, EventLevel.Verbose);
        string checkName = "reported-contributor-" + Guid.NewGuid().ToString("N");
        var contributor = new StubHealthContributor(checkName, HealthContribution.Unhealthy());
        IHealthCheckService service = HealthChecks.CreateBuilder().AddContributor(contributor).Build();

        // Act
        await service.CheckHealthAsync(predicate: null, CancellationToken.None);
        await service.CheckHealthAsync(predicate: null, CancellationToken.None);

        // Assert
        EventWrittenEventArgs worsened = recorder.Events
            .Where(entry => entry.Payload?.FirstOrDefault() as string == checkName)
            .ShouldHaveSingleItem();
        worsened.EventId.ShouldBe(1);
        worsened.EventName.ShouldBe("HealthCheckWorsened");
        worsened.Level.ShouldBe(EventLevel.Warning);
        worsened.PayloadNames.ShouldNotBeNull().ShouldBe(
            new[] { "checkName", "previousStatus", "status", "cause", "exceptionType", "exceptionMessage" });
        worsened.Payload.ShouldNotBeNull().ToArray().ShouldBe(
            new object?[] { checkName, "Unknown", "Unhealthy", "Reported", string.Empty, string.Empty });
        contributor.Invocations.ShouldBe(2);
        recorder.Events.ShouldNotContain(entry => entry.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Contributor: each thrown probe reports once through Web.Health")]
    public async Task AddContributor_WhenRepeatedlyThrowing_ShouldReportOneFailurePerProbe()
    {
        // Arrange
        EventSource source = await GetHealthEventSourceAsync();
        using var recorder = new EventSourceRecorder(source, EventLevel.Verbose);
        string checkName = "throwing-contributor-" + Guid.NewGuid().ToString("N");
        var contributor = new StubHealthContributor(
            checkName,
            _ => ValueTask.FromException<HealthContribution>(new InvalidOperationException("dependency offline")));
        IHealthCheckService service = HealthChecks.CreateBuilder().AddContributor(contributor).Build();

        // Act
        await service.CheckHealthAsync(predicate: null, CancellationToken.None);
        await service.CheckHealthAsync(predicate: null, CancellationToken.None);

        // Assert
        EventWrittenEventArgs[] events = recorder.Events
            .Where(entry => entry.Payload?.FirstOrDefault() as string == checkName)
            .ToArray();
        events.Length.ShouldBe(3);
        events.Where(entry => entry.EventId == 1).ShouldHaveSingleItem()
            .Payload.ShouldNotBeNull()[3].ShouldBe("Exception");
        EventWrittenEventArgs[] failures = events.Where(entry => entry.EventId == 3).ToArray();
        failures.Length.ShouldBe(2);
        foreach (EventWrittenEventArgs failure in failures)
        {
            failure.EventName.ShouldBe("HealthCheckFailed");
            failure.Level.ShouldBe(EventLevel.Verbose);
            failure.PayloadNames.ShouldNotBeNull().ShouldBe(
                new[] { "checkName", "failureStatus", "cause", "exceptionType", "exceptionMessage", "timeoutMilliseconds" });
            failure.Payload.ShouldNotBeNull().Take(5).ShouldBe(
                new object?[] { checkName, "Unhealthy", "Exception", typeof(InvalidOperationException).FullName, "dependency offline" });
        }
        contributor.Invocations.ShouldBe(2);
        recorder.Events.ShouldNotContain(entry => entry.EventId == 0);
    }

    private static async Task<EventSource> GetHealthEventSourceAsync()
    {
        IHealthCheckService warmup = HealthChecks.CreateBuilder()
            .AddCheck("event-source-warmup", () => HealthCheckResult.Healthy())
            .Build();
        await warmup.CheckHealthAsync(predicate: null, CancellationToken.None);

        return EventSource.GetSources().Single(source => source.Name == "Assimalign.Cohesion.Web.Health");
    }
}
