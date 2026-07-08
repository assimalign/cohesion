using System;

using Shouldly;

namespace Assimalign.Cohesion.Health.Tests;

public class HealthCheckResultTests
{
    [Fact(DisplayName = "Cohesion Test [Health] - HealthCheckResult: Healthy factory sets status and empty data")]
    public void Healthy_WhenCreated_ShouldReportHealthyWithEmptyData()
    {
        HealthCheckResult result = HealthCheckResult.Healthy("all good");

        result.Status.ShouldBe(HealthStatus.Healthy);
        result.Description.ShouldBe("all good");
        result.Exception.ShouldBeNull();
        result.Data.ShouldNotBeNull();
        result.Data.Count.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Health] - HealthCheckResult: Unhealthy factory captures exception")]
    public void Unhealthy_WhenGivenException_ShouldCaptureIt()
    {
        var exception = new InvalidOperationException("boom");

        HealthCheckResult result = HealthCheckResult.Unhealthy("down", exception);

        result.Status.ShouldBe(HealthStatus.Unhealthy);
        result.Exception.ShouldBeSameAs(exception);
    }

    [Fact(DisplayName = "Cohesion Test [Health] - HealthCheckResult: Degraded factory carries data")]
    public void Degraded_WhenGivenData_ShouldExposeIt()
    {
        var data = new Dictionary<string, object> { ["latencyMs"] = 250 };

        HealthCheckResult result = HealthCheckResult.Degraded("slow", data: data);

        result.Status.ShouldBe(HealthStatus.Degraded);
        result.Data["latencyMs"].ShouldBe(250);
    }
}
