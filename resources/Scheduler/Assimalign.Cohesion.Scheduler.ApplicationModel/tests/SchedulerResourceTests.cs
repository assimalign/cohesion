using System;
using System.Collections.Generic;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.Core;
using Assimalign.Cohesion.Hosting.Resources;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Scheduler.ApplicationModel.Tests;

public sealed class SchedulerResourceTests
{
    [Fact(DisplayName = "Cohesion Test [Scheduler.ApplicationModel] - Planning: produces a stateless deployment")]
    public void CreatePlan_WithSchedulerManifest_ShouldProduceStatelessDeployment()
    {
        ResourceManifest manifest = CreateManifest();
        var options = new SchedulerResourceOptions { Replicas = 1 };
        var resource = new SchedulerResource(manifest, options);
        var context = CreateContext(resource);

        ResourcePlan plan = resource.CreatePlan(context);

        resource.PlannerName.ShouldBe("Scheduler planner");
        plan.Workload.Kind.ShouldBe(WorkloadKind.Deployment);
        plan.Workload.StableIdentity.ShouldBeFalse();
        plan.Workload.Replicas.ShouldBe(1);
        plan.Volumes.ShouldBeEmpty();
        plan.Services.Count.ShouldBe(1);
        Should.NotThrow(() => ResourcePlanValidator.Validate(plan, context));
    }

    [Fact(DisplayName = "Cohesion Test [Scheduler.ApplicationModel] - AddScheduler: composes a typed resource")]
    public void AddScheduler_WithManifest_ShouldReturnTypedResource()
    {
        IApplicationBuilder builder = Application.CreateBuilder();
        IApplicationResourceDescriptor descriptor = builder.AddScheduler(CreateManifest());
        descriptor.Resource.ShouldBeOfType<SchedulerResource>();
    }

    [Fact(DisplayName = "Cohesion Test [Scheduler.ApplicationModel] - Control plane: creates isolated defaults")]
    public void CreateControlPlane_ShouldReturnIsolatedDefaultPlanes()
    {
        IResourceControlPlane first = SchedulerResourceControlPlane.Create();
        IResourceControlPlane second = SchedulerResourceControlPlane.Create();
        first.ShouldNotBeSameAs(second);
        first.AcceptedCommandKinds.ShouldBeEmpty();
        first.ObservedEndpoints.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Scheduler.ApplicationModel] - Planning: rejects stateful workloads")]
    public void CreatePlan_WithStatefulManifest_ShouldThrow()
    {
        ResourceManifest source = CreateManifest();
        ResourceManifest manifest = source with
        {
            Lifecycle = source.Lifecycle with { Workload = WorkloadKind.StatefulSet },
        };
        var resource = new SchedulerResource(manifest);

        Should.Throw<InvalidOperationException>(() => resource.CreatePlan(CreateContext(resource)))
            .Message.ShouldContain("singleton stateless Deployment", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Scheduler.ApplicationModel] - Planning: rejects a non-http endpoint shape")]
    public void CreatePlan_WithWrongEndpoint_ShouldThrow()
    {
        ResourceManifest source = CreateManifest();
        ResourceManifest manifest = source with
        {
            Endpoints =
            [
                source.Endpoints[0] with { Name = "admin", Scheme = "https" },
            ],
        };
        var resource = new SchedulerResource(manifest);

        Should.Throw<InvalidOperationException>(() => resource.CreatePlan(CreateContext(resource)))
            .Message.ShouldContain("'http' TCP endpoint", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Scheduler.ApplicationModel] - Planning: preserves generic non-persistent traits")]
    public void CreatePlan_WithExtraEndpointAndSecretMount_ShouldPreserveTraits()
    {
        ResourceManifest source = CreateManifest();
        ResourceManifest manifest = source with
        {
            Endpoints =
            [
                .. source.Endpoints,
                new ResourceManifestEndpoint
                {
                    Name = "metrics",
                    Scheme = "http",
                    Protocol = "tcp",
                    ContainerPort = 9090,
                },
            ],
            Mounts =
            [
                new ResourceManifestMount
                {
                    Name = "credentials",
                    Kind = ResourceMountKind.Secret,
                    ContainerPath = "/cohesion/mounts/credentials",
                    Source = "parameter:scheduler-credentials",
                },
            ],
        };
        var resource = new SchedulerResource(manifest);
        PlanContext context = CreateContext(resource);

        ResourcePlan plan = resource.CreatePlan(context);

        plan.Container.Ports.Count.ShouldBe(2);
        plan.Container.Mounts.Count.ShouldBe(1);
        plan.Services.Count.ShouldBe(2);
        plan.Volumes.ShouldBeEmpty();
        Should.NotThrow(() => ResourcePlanValidator.Validate(plan, context));
    }

    private static PlanContext CreateContext(SchedulerResource resource) => new(
        resource.Manifest,
        resource.Options,
        new TestEnvironment(),
        new Dictionary<string, ResourceManifest>());

    private static ResourceManifest CreateManifest() => new()
    {
        Name = "scheduler",
        Kind = "Scheduler",
        Application = "sample",
        ApplicationModel = "Assimalign.Cohesion.Scheduler.ApplicationModel",
        Artifact = new ResourceManifestArtifact
        {
            Assembly = "Sample.Scheduler",
            Composable = true,
        },
        Endpoints =
        [
            new ResourceManifestEndpoint
            {
                Name = "http",
                Scheme = "http",
                Protocol = "tcp",
                ContainerPort = 8080,
            },
        ],
        Probes = new ResourceManifestProbes
        {
            Readiness = new ResourceManifestProbe { Endpoint = "http", Http = "/readyz" },
            Liveness = new ResourceManifestProbe { Endpoint = "http", Http = "/livez" },
        },
        ControlPlane = new ResourceManifestControlPlane
        {
            Endpoint = "http",
            Path = "/cohesion/v1",
        },
        Lifecycle = new ResourceManifestLifecycle
        {
            Workload = WorkloadKind.Deployment,
            Replicas = 1,
            MaxReplicas = 1,
            StopGraceSeconds = 30,
        },
    };

    private sealed class TestEnvironment : IApplicationEnvironment
    {
        public EnvironmentName Name => AppEnvironment.Keys.Development;

        public bool IsLocal => false;

        public bool IsDevelopment => true;
    }
}
