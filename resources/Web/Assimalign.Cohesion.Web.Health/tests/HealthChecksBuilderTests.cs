using System;

using Shouldly;

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
}
