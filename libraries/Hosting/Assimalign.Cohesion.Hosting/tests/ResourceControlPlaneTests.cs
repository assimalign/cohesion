using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Core;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Hosting.Tests;

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
        host.Context.ResourceHostOptions.ShouldNotBeNull().StopGraceSeconds.ShouldBe(42);
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
        controlPlane.ObserveEndpoint("http", new EndpointAddress("http", "localhost", 5080));

        // Act
        ResourceHealthReport report = await controlPlane.CheckHealthAsync(CancellationToken.None);

        // Assert
        report.Status.ShouldBe(HealthStatus.Degraded);
        report.Contributions.Count.ShouldBe(2);
        controlPlane.ObservedEndpoints["http"].Port.ShouldBe(5080);
    }

    [Fact(DisplayName = DisplayPrefix + "HostBuilt attaches lifecycle options and observed endpoints")]
    public void HostBuilt_WithAmbientContext_ConfiguresOnlyTheBuiltHost()
    {
        // Arrange
        string contentRoot = Path.GetFullPath("resource-content");
        var endpoint = new EndpointAddress("http", "127.0.0.1", 5081);
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
        ResourceHostOptions options = host.Context.ResourceHostOptions.ShouldNotBeNull();
        options.ContentRootPath.ShouldBe(FileSystemPath.Parse(contentRoot));
        options.StopEventName.ShouldBe("cohesion-stop-test");
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
}
