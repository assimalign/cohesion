using System;
using System.Collections.Generic;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.Rezolvr.ApplicationModel.Tests;

public sealed class RezolvrResourceTests
{
    [Fact(DisplayName = "Cohesion Test [Rezolvr.ApplicationModel] - Planning: preserves SDK defaults")]
    public void CreatePlan_WithDefaultManifest_ShouldPreserveAreaShape()
    {
        ResourceManifest manifest = CreateManifest();
        var options = new RezolvrResourceOptions();
        var resource = new RezolvrResource(manifest, options);
        PlanContext context = CreateContext(resource);

        ResourcePlan plan = resource.CreatePlan(context);

        resource.Manifest.ShouldNotBeSameAs(manifest);
        resource.Options.ShouldBeSameAs(options);
        resource.PlannerName.ShouldBe("Rezolvr planner");
        plan.Workload.Kind.ShouldBe(WorkloadKind.Deployment);
        plan.Workload.StableIdentity.ShouldBe(false);
        plan.Container.Ports.Count.ShouldBe(3);
        plan.Services.ShouldContain(service => service.Endpoint == "admin");
        plan.Volumes.ShouldBeEmpty();
        Should.NotThrow(() => ResourcePlanValidator.Validate(plan, context));
    }

    [Fact(DisplayName = "Cohesion Test [Rezolvr.ApplicationModel] - AddRezolvr: returns typed graph descriptor")]
    public void AddRezolvr_WithManifest_ShouldComposeTypedResourceAndDependencies()
    {
        IApplicationBuilder builder = Application.CreateBuilder();
        IRezolvrResourceDescriptor first = builder.AddRezolvr(CreateManifest());
        IRezolvrResourceDescriptor second = builder.AddRezolvr(CreateManifest() with { Name = "second" });

        second.DependsOn(first).ShouldBeSameAs(second);

        first.Resource.ShouldBeOfType<RezolvrResource>();
        second.Dependencies.ShouldContain(first);
        Should.Throw<ArgumentNullException>(() => builder.AddRezolvr(null!));
    }

    [Fact(DisplayName = "Cohesion Test [Rezolvr.ApplicationModel] - Control plane: isolates empty command sets")]
    public void CreateControlPlane_ShouldReturnIsolatedEmptyPlanes()
    {
        IResourceControlPlane first = RezolvrResourceControlPlane.Create();
        IResourceControlPlane second = RezolvrResourceControlPlane.Create();
        first.ShouldNotBeSameAs(second);
        first.AcceptedCommandKinds.ShouldBeEmpty();
        first.Commands.ShouldBeEmpty();
        first.ObservedEndpoints.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Rezolvr.ApplicationModel] - Planning: rejects incompatible area facts")]
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
        var resource = new RezolvrResource(manifest);

        Should.Throw<InvalidOperationException>(() => resource.CreatePlan(CreateContext(resource)));
    }

    [Fact(DisplayName = "Cohesion Test [Rezolvr.ApplicationModel] - Planning: rejects another resource context")]
    public void CreatePlan_WithOtherResourceContext_ShouldReject()
    {
        var resource = new RezolvrResource(CreateManifest());
        var other = new RezolvrResource(CreateManifest() with { Name = "other" });
        Should.Throw<InvalidOperationException>(() => resource.CreatePlan(CreateContext(other)));
    }

    [Fact(DisplayName = "Cohesion Test [Rezolvr.ApplicationModel] - Planning: preserves extra endpoints and secret mounts")]
    public void CreatePlan_WithAdditionalTraits_ShouldPreserveThem()
    {
        ResourceManifest source = CreateManifest();
        var resource = new RezolvrResource(source with
        {
            Endpoints = [.. source.Endpoints, new ResourceManifestEndpoint { Name = "metrics", Scheme = "http", Protocol = "tcp", ContainerPort = 9090 }],
            Mounts = [.. source.Mounts, new ResourceManifestMount { Name = "credential", Kind = ResourceMountKind.Secret, ContainerPath = "/credentials", Source = "parameter:credential" }],
        });
        PlanContext context = CreateContext(resource);

        ResourcePlan plan = resource.CreatePlan(context);

        plan.Container.Ports.Count.ShouldBe(3 + 1);
        plan.Container.Mounts.Count.ShouldBe(source.Mounts.Count + 1);
        Should.NotThrow(() => ResourcePlanValidator.Validate(plan, context));
    }

    private static PlanContext CreateContext(RezolvrResource resource) => new(
        resource.Manifest, resource.Options, Application.CreateBuilder().Environment,
        new Dictionary<string, ResourceManifest>());

    private static ResourceManifest CreateManifest() => new()
    {
        Name = "rezolvr",
        Kind = "Rezolvr",
        Application = "sample",
        ApplicationModel = "Assimalign.Cohesion.Rezolvr.ApplicationModel",
        Artifact = new ResourceManifestArtifact { Assembly = "Sample.Rezolvr", Composable = true },
        Endpoints =
        [
            new ResourceManifestEndpoint { Name = "dns", Scheme = "dns", Protocol = "udp", ContainerPort = 53 },
            new ResourceManifestEndpoint { Name = "dns-tcp", Scheme = "dns", Protocol = "tcp", ContainerPort = 53 },
            new ResourceManifestEndpoint { Name = "admin", Scheme = "http", Protocol = "tcp", ContainerPort = 8081 },
        ],

        Probes = new ResourceManifestProbes
        {
            Readiness = new ResourceManifestProbe { Endpoint = "admin", Http = "/readyz" },
            Liveness = new ResourceManifestProbe { Endpoint = "admin", Http = "/livez" },
        },
        ControlPlane = new ResourceManifestControlPlane { Endpoint = "admin", Path = "/cohesion/v1" },
        Lifecycle = new ResourceManifestLifecycle { Workload = WorkloadKind.Deployment, Replicas = 1, MaxReplicas = 3, StopGraceSeconds = 30 },
    };
}
