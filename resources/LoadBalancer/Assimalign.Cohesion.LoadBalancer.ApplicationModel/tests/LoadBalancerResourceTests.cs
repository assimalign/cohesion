using System;
using System.Collections.Generic;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.LoadBalancer.ApplicationModel.Tests;

public sealed class LoadBalancerResourceTests
{
    [Fact(DisplayName = "Cohesion Test [LoadBalancer.ApplicationModel] - Planning: preserves SDK defaults")]
    public void CreatePlan_WithDefaultManifest_ShouldPreserveAreaShape()
    {
        ResourceManifest manifest = CreateManifest();
        var options = new LoadBalancerResourceOptions();
        var resource = new LoadBalancerResource(manifest, options);
        PlanContext context = CreateContext(resource);

        ResourcePlan plan = resource.CreatePlan(context);

        resource.Manifest.ShouldNotBeSameAs(manifest);
        resource.Options.ShouldBeSameAs(options);
        resource.PlannerName.ShouldBe("LoadBalancer planner");
        plan.Workload.Kind.ShouldBe(WorkloadKind.Deployment);
        plan.Workload.StableIdentity.ShouldBe(false);
        plan.Container.Ports.Count.ShouldBe(1);
        plan.Services.ShouldContain(service => service.Endpoint == "http");
        plan.Volumes.ShouldBeEmpty();
        Should.NotThrow(() => ResourcePlanValidator.Validate(plan, context));
    }

    [Fact(DisplayName = "Cohesion Test [LoadBalancer.ApplicationModel] - AddLoadBalancer: returns typed graph descriptor")]
    public void AddLoadBalancer_WithManifest_ShouldComposeTypedResourceAndDependencies()
    {
        IApplicationBuilder builder = Application.CreateBuilder();
        ILoadBalancerResourceDescriptor first = builder.AddLoadBalancer(CreateManifest());
        ILoadBalancerResourceDescriptor second = builder.AddLoadBalancer(CreateManifest() with { Name = "second" });

        second.DependsOn(first).ShouldBeSameAs(second);

        first.Resource.ShouldBeOfType<LoadBalancerResource>();
        second.Dependencies.ShouldContain(first);
        Should.Throw<ArgumentNullException>(() => builder.AddLoadBalancer(null!));
    }

    [Fact(DisplayName = "Cohesion Test [LoadBalancer.ApplicationModel] - Control plane: isolates empty command sets")]
    public void CreateControlPlane_ShouldReturnIsolatedEmptyPlanes()
    {
        IResourceControlPlane first = LoadBalancerResourceControlPlane.Create();
        IResourceControlPlane second = LoadBalancerResourceControlPlane.Create();
        first.ShouldNotBeSameAs(second);
        first.AcceptedCommandKinds.ShouldBeEmpty();
        first.Commands.ShouldBeEmpty();
        first.ObservedEndpoints.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [LoadBalancer.ApplicationModel] - Planning: rejects incompatible area facts")]
    [InlineData("kind")]
    [InlineData("workload")]
    [InlineData("endpoint")]
    [InlineData("path")]
    public void CreatePlan_WithInvalidFacts_ShouldReject(string invalid)
    {
        ResourceManifest manifest = CreateManifest();
        manifest = invalid switch
        {
            "kind" => manifest with { Kind = "Other" },
            "workload" => manifest with { Lifecycle = manifest.Lifecycle with { Workload = WorkloadKind.Job } },
            "endpoint" => manifest with { Endpoints = [] },
            _ => manifest with { ControlPlane = manifest.ControlPlane with { Path = "/other" } },
        };
        var resource = new LoadBalancerResource(manifest);

        Should.Throw<InvalidOperationException>(() => resource.CreatePlan(CreateContext(resource)));
    }

    [Fact(DisplayName = "Cohesion Test [LoadBalancer.ApplicationModel] - Planning: rejects another resource context")]
    public void CreatePlan_WithOtherResourceContext_ShouldReject()
    {
        var resource = new LoadBalancerResource(CreateManifest());
        var other = new LoadBalancerResource(CreateManifest() with { Name = "other" });
        Should.Throw<InvalidOperationException>(() => resource.CreatePlan(CreateContext(other)));
    }

    [Fact(DisplayName = "Cohesion Test [LoadBalancer.ApplicationModel] - Planning: preserves extra endpoints and secret mounts")]
    public void CreatePlan_WithAdditionalTraits_ShouldPreserveThem()
    {
        ResourceManifest source = CreateManifest();
        var resource = new LoadBalancerResource(source with
        {
            Endpoints = [.. source.Endpoints, new ResourceManifestEndpoint { Name = "metrics", Scheme = "http", Protocol = "tcp", ContainerPort = 9090 }],
            Mounts = [.. source.Mounts, new ResourceManifestMount { Name = "credential", Kind = ResourceMountKind.Secret, ContainerPath = "/credentials", Source = "parameter:credential" }],
        });
        PlanContext context = CreateContext(resource);

        ResourcePlan plan = resource.CreatePlan(context);

        plan.Container.Ports.Count.ShouldBe(1 + 1);
        plan.Container.Mounts.Count.ShouldBe(source.Mounts.Count + 1);
        Should.NotThrow(() => ResourcePlanValidator.Validate(plan, context));
    }

    private static PlanContext CreateContext(LoadBalancerResource resource) => new(
        resource.Manifest, resource.Options, Application.CreateBuilder().Environment,
        new Dictionary<string, ResourceManifest>());

    private static ResourceManifest CreateManifest() => new()
    {
        Name = "load-balancer",
        Kind = "LoadBalancer",
        Application = "sample",
        ApplicationModel = "Assimalign.Cohesion.LoadBalancer.ApplicationModel",
        Artifact = new ResourceManifestArtifact { Assembly = "Sample.LoadBalancer", Composable = false },
        Endpoints =
        [
            new ResourceManifestEndpoint { Name = "http", Scheme = "http", Protocol = "tcp", ContainerPort = 8080 },
        ],

        Probes = new ResourceManifestProbes
        {
            Readiness = new ResourceManifestProbe { Endpoint = "http", Http = "/readyz" },
            Liveness = new ResourceManifestProbe { Endpoint = "http", Http = "/livez" },
        },
        ControlPlane = new ResourceManifestControlPlane { Endpoint = "http", Path = "/cohesion/v1" },
        Lifecycle = new ResourceManifestLifecycle { Workload = WorkloadKind.Deployment, Replicas = 1, MaxReplicas = 3, StopGraceSeconds = 30 },
    };
}
