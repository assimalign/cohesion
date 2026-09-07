using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Health;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Web.Health.Tests;

public class HealthChecksBuilderTests
{
    [Fact(DisplayName = "Cohesion Test [Health] - Builder: rejects duplicate check names")]
    public void Add_WhenNameAlreadyRegistered_ShouldThrow()
    {
        IHealthChecksBuilder builder = HealthChecks.CreateBuilder();
        builder.AddCheck("db", () => HealthCheckResult.Healthy());

        Should.Throw<InvalidOperationException>(() => builder.AddCheck("db", () => HealthCheckResult.Healthy()));
    }

    [Fact(DisplayName = "Cohesion Test [Health] - Builder: duplicate name check is case-insensitive")]
    public void Add_WhenNameDiffersOnlyByCase_ShouldThrow()
    {
        IHealthChecksBuilder builder = HealthChecks.CreateBuilder();
        builder.AddCheck("DB", () => HealthCheckResult.Healthy());

        Should.Throw<InvalidOperationException>(() => builder.AddCheck("db", () => HealthCheckResult.Healthy()));
    }

    [Fact(DisplayName = "Cohesion Test [Health] - Builder: Build snapshots the current registrations")]
    public async Task Build_WhenCalled_ShouldSnapshotRegistrationsAtThatMoment()
    {
        IHealthChecksBuilder builder = HealthChecks.CreateBuilder();
        builder.AddCheck("first", () => HealthCheckResult.Healthy());

        IHealthCheckService service = builder.Build();

        // A registration added after Build must not appear in the already-built service.
        builder.AddCheck("second", () => HealthCheckResult.Unhealthy());

        HealthReport report = await service.CheckHealthAsync();

        report.Entries.Keys.ShouldBe(new[] { "first" });
    }

    [Fact(DisplayName = "Cohesion Test [Health] - Builder: Registrations exposes what was added")]
    public void Registrations_WhenChecksAdded_ShouldExposeThem()
    {
        IHealthChecksBuilder builder = HealthChecks.CreateBuilder();
        builder.AddCheck("a", () => HealthCheckResult.Healthy(), tags: new[] { HealthTags.Ready });

        builder.Registrations.Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Health] - Registration: rejects a non-positive timeout")]
    public void Registration_WhenTimeoutNonPositive_ShouldThrow()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new HealthCheckRegistration("x", new StubCheck(HealthStatus.Healthy), timeout: TimeSpan.Zero));
    }

    [Theory(DisplayName = "Cohesion Test [Health] - Builder: maps Hosting contributor results and applies probe tags")]
    [InlineData(Assimalign.Cohesion.Hosting.Health.HealthStatus.Healthy, HealthStatus.Healthy)]
    [InlineData(Assimalign.Cohesion.Hosting.Health.HealthStatus.Degraded, HealthStatus.Degraded)]
    [InlineData(Assimalign.Cohesion.Hosting.Health.HealthStatus.Unhealthy, HealthStatus.Unhealthy)]
    public async Task AddContributor_WhenEvaluated_ShouldMapResultAndApplyDefaultProbeTags(
        Assimalign.Cohesion.Hosting.Health.HealthStatus contributorStatus,
        HealthStatus expectedStatus)
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

    [Fact(DisplayName = "Cohesion Test [Health] - Builder: contributor options override defaults and failures honor policy")]
    public async Task AddContributor_WhenOptionsProvided_ShouldApplyThemAndHonorFailureStatus()
    {
        var contributor = new StubHealthContributor(
            "cache",
            _ => ValueTask.FromException<HealthContribution>(new InvalidOperationException("cache offline")));
        TimeSpan timeout = TimeSpan.FromSeconds(2);
        IHealthChecksBuilder builder = HealthChecks.CreateBuilder()
            .AddContributor(
                contributor,
                failureStatus: HealthStatus.Degraded,
                tags: new[] { "dependency" },
                timeout: timeout);

        HealthReport report = await builder.Build().CheckHealthAsync();

        HealthCheckRegistration registration = builder.Registrations.ShouldHaveSingleItem();
        registration.FailureStatus.ShouldBe(HealthStatus.Degraded);
        registration.Timeout.ShouldBe(timeout);
        registration.HasTag("dependency").ShouldBeTrue();
        registration.HasTag(HealthTags.Ready).ShouldBeFalse();
        registration.HasTag(HealthTags.Live).ShouldBeFalse();
        report.Entries["cache"].Status.ShouldBe(HealthStatus.Degraded);
        report.Entries["cache"].Description.ShouldBe("cache offline");
    }

    [Fact(DisplayName = "Cohesion Test [Health] - Builder: contributor receives caller cancellation")]
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

    [Fact(DisplayName = "Cohesion Test [Health] - Builder: contributor names use normal duplicate validation")]
    public void AddContributor_WhenNameAlreadyRegistered_ShouldThrow()
    {
        IHealthChecksBuilder builder = HealthChecks.CreateBuilder()
            .AddContributor(new StubHealthContributor("database", HealthContribution.Healthy()));

        Should.Throw<InvalidOperationException>(() =>
            builder.AddContributor(new StubHealthContributor("DATABASE", HealthContribution.Healthy())));
    }

    [Fact(DisplayName = "Cohesion Test [Health] - Builder: rejects a null contributor")]
    public void AddContributor_WhenContributorNull_ShouldThrow()
    {
        IHealthChecksBuilder builder = HealthChecks.CreateBuilder();

        Should.Throw<ArgumentNullException>(() => builder.AddContributor(null!));
    }
}
