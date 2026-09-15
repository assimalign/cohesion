using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Hosting.Health;
using Assimalign.Cohesion.Web.Health;
using WebHealthStatus = Assimalign.Cohesion.Web.Health.HealthStatus;

namespace Assimalign.Cohesion.Web.Hosting.Health.Tests;

public class HealthChecksBuilderTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Hosting.Health] - Builder: maps Hosting contributor results and applies probe tags")]
    [InlineData(Assimalign.Cohesion.Hosting.Health.HealthStatus.Healthy, WebHealthStatus.Healthy)]
    [InlineData(Assimalign.Cohesion.Hosting.Health.HealthStatus.Degraded, WebHealthStatus.Degraded)]
    [InlineData(Assimalign.Cohesion.Hosting.Health.HealthStatus.Unhealthy, WebHealthStatus.Unhealthy)]
    public async Task AddContributor_WhenEvaluated_ShouldMapResultAndApplyDefaultProbeTags(
        Assimalign.Cohesion.Hosting.Health.HealthStatus contributorStatus,
        WebHealthStatus expectedStatus)
    {
        var data = new Dictionary<string, object>
        {
            ["connections"] = 3
        };
        var contributor = new StubHealthContributor(
            "database",
            new HealthContribution(contributorStatus, "database status", data));
        IHealthChecksBuilder builder = HealthChecks.CreateBuilder()
            .AddContributor(contributor);

        HealthReport report = await builder.Build().CheckHealthAsync();

        HealthCheckRegistration registration = builder.Registrations.ShouldHaveSingleItem();
        registration.Name.ShouldBe("database");
        registration.HasTag(HealthTags.Ready).ShouldBeTrue();
        registration.HasTag(HealthTags.Live).ShouldBeTrue();

        HealthReportEntry entry = report.Entries["database"];
        entry.Status.ShouldBe(expectedStatus);
        entry.Description.ShouldBe("database status");
        entry.Data.ShouldBeSameAs(data);
        contributor.Invocations.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting.Health] - Builder: contributor options override defaults and failures honor policy")]
    public async Task AddContributor_WhenOptionsProvided_ShouldApplyThemAndHonorFailureStatus()
    {
        var contributor = new StubHealthContributor(
            "cache",
            _ => ValueTask.FromException<HealthContribution>(new InvalidOperationException("cache offline")));
        TimeSpan timeout = TimeSpan.FromSeconds(2);
        IHealthChecksBuilder builder = HealthChecks.CreateBuilder()
            .AddContributor(
                contributor,
                failureStatus: WebHealthStatus.Degraded,
                tags: new[] { "dependency" },
                timeout: timeout);

        HealthReport report = await builder.Build().CheckHealthAsync();

        HealthCheckRegistration registration = builder.Registrations.ShouldHaveSingleItem();
        registration.FailureStatus.ShouldBe(WebHealthStatus.Degraded);
        registration.Timeout.ShouldBe(timeout);
        registration.HasTag("dependency").ShouldBeTrue();
        registration.HasTag(HealthTags.Ready).ShouldBeFalse();
        registration.HasTag(HealthTags.Live).ShouldBeFalse();
        report.Entries["cache"].Status.ShouldBe(WebHealthStatus.Degraded);
        report.Entries["cache"].Description.ShouldBe("cache offline");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting.Health] - Builder: contributor receives caller cancellation")]
    public async Task AddContributor_WhenCallerCancels_ShouldPropagateCancellationToken()
    {
        using var cancellation = new CancellationTokenSource();
        var contributor = new StubHealthContributor("queue", cancellationToken =>
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(HealthContribution.Healthy());
        });
        IHealthCheckService service = HealthChecks.CreateBuilder()
            .AddContributor(contributor)
            .Build();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await service.CheckHealthAsync(predicate: null, cancellation.Token));

        contributor.LastCancellationToken.ShouldBe(cancellation.Token);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting.Health] - Builder: contributor names use normal duplicate validation")]
    public void AddContributor_WhenNameAlreadyRegistered_ShouldThrow()
    {
        IHealthChecksBuilder builder = HealthChecks.CreateBuilder()
            .AddContributor(new StubHealthContributor("database", HealthContribution.Healthy()));

        Should.Throw<InvalidOperationException>(() =>
            builder.AddContributor(new StubHealthContributor("DATABASE", HealthContribution.Healthy())));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting.Health] - Builder: rejects a null contributor")]
    public void AddContributor_WhenContributorNull_ShouldThrow()
    {
        IHealthChecksBuilder builder = HealthChecks.CreateBuilder();

        Should.Throw<ArgumentNullException>(() => builder.AddContributor(null!));
    }
}
