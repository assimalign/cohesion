using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Core;
using Assimalign.Cohesion.Hosting.Health;
using Assimalign.Cohesion.Hosting.Resources.Internal;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Hosting.Resources.Tests;

public class ResourceControlPlaneTests
{
    private const string DisplayPrefix = "Cohesion Test [Hosting] - Resource control plane: ";

    [Fact(DisplayName = DisplayPrefix + "Assembly registration creates isolated control planes and carries stop grace")]
    public void RegisterControlPlane_ForAssembly_CreatesNewInstancesAndCarriesStopGrace()
    {
        // Arrange
        Assembly assembly = typeof(ResourceControlPlaneTests).Assembly;
        ResourceRuntime.RegisterControlPlane(
            assembly,
            static () => ResourceControlPlane.Create(),
            stopGraceSeconds: 42);
        using IDisposable scope = ResourceRuntime.CreateScope(new ResourceContext());

        // Act
        ResourceRuntime.TryCreateControlPlane(assembly, out IResourceControlPlane? first).ShouldBeTrue();
        ResourceRuntime.TryCreateControlPlane(assembly, out IResourceControlPlane? second).ShouldBeTrue();

        // Assert
        first.ShouldNotBeNull();
        second.ShouldNotBeNull();
        second.ShouldNotBeSameAs(first);

        var host = new TestHost(new TestHostOptions());
        ResourceRuntime.HostBuilt(host, first);
        ResourceHostRunner runner = host.Context.Runner.ShouldBeOfType<ResourceHostRunner>();
        runner.Options.StopGraceSeconds.ShouldBe(42);
    }

    [Fact(DisplayName = DisplayPrefix + "Health aggregation fails to the least healthy contribution")]
    public async Task CheckHealthAsync_WithContributors_AggregatesStatus()
    {
        // Arrange
        IResourceControlPlane controlPlane = ResourceControlPlane.Create();
        controlPlane.AddHealthContributor(new TestContributor(
            "ready",
            HealthContribution.Healthy("accepting work")));
        controlPlane.AddHealthContributor(new TestContributor(
            "dependency",
            HealthContribution.Degraded("slow")));
        controlPlane.ObserveEndpoint("http", new Uri("http://localhost:5080"));

        // Act
        ResourceHealthReport report = await controlPlane.CheckHealthAsync(CancellationToken.None);

        // Assert
        report.Status.ShouldBe(HealthStatus.Degraded);
        report.Contributions.Count.ShouldBe(2);
        controlPlane.ObservedEndpoints["http"].Port.ShouldBe(5080);
    }

    [Fact(DisplayName = DisplayPrefix + "Endpoint observation rejects a null address")]
    public void ObserveEndpoint_WithNullAddress_ShouldThrowArgumentNullException()
    {
        // Arrange
        IResourceControlPlane controlPlane = ResourceControlPlane.Create();

        // Act
        Action action = () => controlPlane.ObserveEndpoint("http", null!);

        // Assert
        Should.Throw<ArgumentNullException>(action).ParamName.ShouldBe("address");
    }

    [Fact(DisplayName = DisplayPrefix + "Endpoint observation rejects a relative address")]
    public void ObserveEndpoint_WithRelativeAddress_ShouldThrowArgumentException()
    {
        // Arrange
        IResourceControlPlane controlPlane = ResourceControlPlane.Create();

        // Act
        Action action = () => controlPlane.ObserveEndpoint("http", new Uri("relative", UriKind.Relative));

        // Assert
        Should.Throw<ArgumentException>(action).ParamName.ShouldBe("address");
    }

    [Fact(DisplayName = DisplayPrefix + "A contributor failure becomes a named unhealthy result without stopping aggregation")]
    public async Task CheckHealthAsync_WhenContributorThrows_RecordsFailureAndContinues()
    {
        // Arrange
        int followingInvocations = 0;
        IResourceControlPlane controlPlane = ResourceControlPlane.Create();
        controlPlane.AddHealthContributor(new CallbackContributor(
            "database",
            _ => ValueTask.FromException<HealthContribution>(
                new InvalidOperationException("database unavailable"))));
        controlPlane.AddHealthContributor(new CallbackContributor(
            "worker",
            _ =>
            {
                followingInvocations++;
                return ValueTask.FromResult(HealthContribution.Healthy("running"));
            }));

        // Act
        ResourceHealthReport report = await controlPlane.CheckHealthAsync(CancellationToken.None);

        // Assert
        report.Status.ShouldBe(HealthStatus.Unhealthy);
        report.Contributions.Count.ShouldBe(2);
        report.Contributions["database"].ShouldBe(
            HealthContribution.Unhealthy("database unavailable"));
        report.Contributions["worker"].Status.ShouldBe(HealthStatus.Healthy);
        followingInvocations.ShouldBe(1);
    }

    [Fact(DisplayName = DisplayPrefix + "An unrelated contributor cancellation becomes an unhealthy result")]
    public async Task CheckHealthAsync_WhenContributorCancelsIndependently_RecordsFailure()
    {
        // Arrange
        IResourceControlPlane controlPlane = ResourceControlPlane.Create();
        controlPlane.AddHealthContributor(new CallbackContributor(
            "queue",
            _ => ValueTask.FromException<HealthContribution>(
                new OperationCanceledException("queue probe canceled"))));

        // Act
        ResourceHealthReport report = await controlPlane.CheckHealthAsync(CancellationToken.None);

        // Assert
        report.Status.ShouldBe(HealthStatus.Unhealthy);
        report.Contributions["queue"].ShouldBe(
            HealthContribution.Unhealthy("queue probe canceled"));
    }

    [Fact(DisplayName = DisplayPrefix + "Caller cancellation propagates and stops aggregation")]
    public async Task CheckHealthAsync_WhenCallerCancels_PropagatesCancellation()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        int followingInvocations = 0;
        IResourceControlPlane controlPlane = ResourceControlPlane.Create();
        controlPlane.AddHealthContributor(new CallbackContributor(
            "blocking",
            cancellationToken =>
            {
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(HealthContribution.Healthy());
            }));
        controlPlane.AddHealthContributor(new CallbackContributor(
            "following",
            _ =>
            {
                followingInvocations++;
                return ValueTask.FromResult(HealthContribution.Healthy());
            }));

        // Act & Assert
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await controlPlane.CheckHealthAsync(cancellation.Token));
        followingInvocations.ShouldBe(0);
    }

    [Fact(DisplayName = DisplayPrefix + "HostBuilt attaches lifecycle options and observed endpoints")]
    public void HostBuilt_WithAmbientContext_ConfiguresOnlyTheBuiltHost()
    {
        // Arrange
        string contentRoot = Path.GetFullPath("resource-content");
        var endpoint = new Uri("http://127.0.0.1:5081");
        ResourceContext context = ResourceContext.FromEnvironment(
            new Dictionary<string, string?>
            {
                [ResourceEnvironment.ContentRoot] = contentRoot,
                [ResourceEnvironment.StopEvent] = "cohesion-stop-test",
                [ResourceEnvironment.Endpoint("http", "HOST")] = endpoint.Host,
                [ResourceEnvironment.Endpoint("http", "PORT")] = endpoint.Port.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                [ResourceEnvironment.Endpoint("http", "SCHEME")] = endpoint.Scheme,
            });
        using IDisposable scope = ResourceRuntime.CreateScope(context);
        IResourceControlPlane controlPlane = ResourceControlPlane.Create();
        var host = new TestHost(new TestHostOptions());

        // Act
        ResourceRuntime.HostBuilt(host, controlPlane);

        // Assert
        ResourceHostRunner runner = host.Context.Runner.ShouldBeOfType<ResourceHostRunner>();
        ResourceHostOptions options = runner.Options;
        options.ContentRootPath.ShouldBe(FileSystemPath.Parse(contentRoot));
        options.StopEventName.ShouldBe("cohesion-stop-test");
        options.RunMode.ShouldBe(ResourceHostRunMode.Process);
        controlPlane.ObservedEndpoints["http"].ShouldBe(endpoint);
    }

    private sealed class TestContributor : IHealthContributor
    {
        private readonly HealthContribution _contribution;

        internal TestContributor(string name, HealthContribution contribution)
        {
            Name = name;
            _contribution = contribution;
        }

        public string Name { get; }

        public ValueTask<HealthContribution> CheckAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_contribution);
        }
    }

    private sealed class CallbackContributor : IHealthContributor
    {
        private readonly Func<CancellationToken, ValueTask<HealthContribution>> _callback;

        internal CallbackContributor(
            string name,
            Func<CancellationToken, ValueTask<HealthContribution>> callback)
        {
            Name = name;
            _callback = callback;
        }

        public string Name { get; }

        public ValueTask<HealthContribution> CheckAsync(
            CancellationToken cancellationToken = default)
        {
            return _callback.Invoke(cancellationToken);
        }
    }
}
