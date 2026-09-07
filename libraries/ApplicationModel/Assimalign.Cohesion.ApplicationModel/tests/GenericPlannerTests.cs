using System;
using System.Collections.Generic;
using System.Text.Json;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

public class GenericPlannerTests
{
    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - CreatePlan: Should map a Web manifest to endpoint, probe, exposure, and contract environment specs")]
    public void CreatePlan_ForWebManifest_ShouldMapEndpointsProbesExposureAndContractEnvironment()
    {
        // Arrange
        ResourceManifest manifest = CreateWebManifest();
        PlanContext context = CreateContext(manifest);

        // Act
        ResourcePlan plan = GenericPlanner.CreatePlan(context);

        // Assert
        plan.Schema.ShouldBe(ResourcePlan.CurrentSchema);
        plan.Resource.ShouldBe(manifest.Name);
        plan.Kind.ShouldBe("Web");
        plan.Workload.Kind.ShouldBe(WorkloadKind.Deployment);
        plan.Workload.Replicas.ShouldBe(2);
        plan.Workload.StableIdentity.ShouldBeFalse();
        plan.Workload.Gate.Terminals.ShouldBe([
            ResourceLifecycle.Running,
            ResourceLifecycle.Failed,
            ResourceLifecycle.Stopped]);
        plan.Workload.Gate.Satisfying.ShouldBe([ResourceLifecycle.Running]);
        plan.Container.Artifact.ShouldBe(ArtifactRef.Self);
        plan.Container.Ports.Count.ShouldBe(2);
        plan.Container.Probes.Count.ShouldBe(2);
        plan.Services.Count.ShouldBe(2);
        plan.Exposures.Count.ShouldBe(1);
        plan.Exposures[0].Protocol.ShouldBe("tcp");
        plan.Hints.ShouldBeEmpty();
        plan.Container.Environment["CUSTOM_VALUE"].ShouldBe("preserved");
        plan.Container.Environment[ResourceEnvironment.Application].ShouldBe("appa");
        plan.Container.Environment[ResourceEnvironment.Resource].ShouldBe("appa-api");
        plan.Container.Environment[ResourceEnvironment.Environment].ShouldBe("Development");
        Should.NotThrow(() => ResourcePlanValidator.Validate(plan, context));
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - CreatePlan: Should map a Database volume to a per-replica claim and governing service")]
    public void CreatePlan_ForDatabaseVolume_ShouldMapPerReplicaClaimAndGoverningService()
    {
        // Arrange
        ResourceManifest manifest = CreateDatabaseManifest();
        PlanContext context = CreateContext(manifest);

        // Act
        ResourcePlan plan = GenericPlanner.CreatePlan(context);

        // Assert
        plan.Workload.Kind.ShouldBe(WorkloadKind.StatefulSet);
        plan.Workload.StableIdentity.ShouldBeTrue();
        plan.Volumes.Count.ShouldBe(1);
        plan.Volumes[0].Name.ShouldBe("data");
        plan.Volumes[0].Kind.ShouldBe(ResourceMountKind.Volume);
        plan.Volumes[0].Size.ShouldBe("10Gi");
        plan.Volumes[0].PerReplicaClaim.ShouldBeTrue();
        plan.Services.Count.ShouldBe(3);
        plan.Services.ShouldContain(service => service.Headless && service.Governing);
        Should.NotThrow(() => ResourcePlanValidator.Validate(plan, context));
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Validate: Should reject Job workloads until roadmap item 26")]
    public void Validate_ForJobPlan_ShouldRejectUntilItem26()
    {
        // Arrange
        ResourceManifest manifest = CreateWebManifest() with
        {
            Lifecycle = CreateWebManifest().Lifecycle with { Workload = WorkloadKind.Job }
        };
        PlanContext context = CreateContext(manifest);
        ResourcePlan plan = GenericPlanner.CreatePlan(context);

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => ResourcePlanValidator.Validate(plan, context));

        // Assert
        exception.Message.ShouldContain("item 26", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - JSON context: Should round-trip ArtifactRef as a string")]
    public void JsonContext_OnRoundTrip_ShouldSerializeArtifactRefAsString()
    {
        // Arrange
        ResourceManifest manifest = CreateWebManifest();
        PlanContext context = CreateContext(manifest);
        ResourcePlan plan = GenericPlanner.CreatePlan(context);

        // Act
        string json = JsonSerializer.Serialize(plan, ResourcePlanJsonContext.Default.ResourcePlan);
        ResourcePlan roundTrip = JsonSerializer.Deserialize(
            json,
            ResourcePlanJsonContext.Default.ResourcePlan)
            ?? throw new InvalidOperationException("The round-tripped plan was null.");

        // Assert
        json.ShouldContain("\"artifact\":\"self\"", Case.Sensitive);
        roundTrip.Container.Artifact.ShouldBe(ArtifactRef.Self);
        roundTrip.Resource.ShouldBe(manifest.Name);
        Should.NotThrow(() => ResourcePlanValidator.Validate(roundTrip, context));
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - CreatePlan: Should prefer a typed storage override to the manifest mount size")]
    public void CreatePlan_WithStorageOverride_ShouldPreferOverrideToManifestSize()
    {
        // Arrange
        ResourceManifest manifest = CreateDatabaseManifest();
        var options = new ResourceOptions();
        options.Storage.Size = "20Gi";
        PlanContext context = CreateContext(manifest, options);

        // Act
        ResourcePlan plan = GenericPlanner.CreatePlan(context);

        // Assert
        plan.Volumes[0].Size.ShouldBe("20Gi");
        Should.NotThrow(() => ResourcePlanValidator.Validate(plan, context));
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Validate: Should reject a workload kind that differs from the manifest")]
    public void Validate_WithMismatchedWorkloadKind_ShouldThrow()
    {
        // Arrange
        ResourceManifest manifest = CreateWebManifest();
        PlanContext context = CreateContext(manifest);
        ResourcePlan original = GenericPlanner.CreatePlan(context);
        var workload = new WorkloadSpec(
            WorkloadKind.DaemonSet,
            original.Workload.Replicas,
            StableIdentity: false,
            ReadinessGate.For(WorkloadKind.DaemonSet),
            original.Workload.StopGraceSeconds);
        ResourcePlan invalid = CopyPlan(original, workload, original.Container);

        // Act
        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => ResourcePlanValidator.Validate(invalid, context));

        // Assert
        error.Message.ShouldContain("does not match manifest workload", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Validate: Should reject artifact references other than Self")]
    public void Validate_WithNonSelfArtifact_ShouldThrow()
    {
        // Arrange
        ResourceManifest manifest = CreateWebManifest();
        PlanContext context = CreateContext(manifest);
        ResourcePlan original = GenericPlanner.CreatePlan(context);
        var container = new ContainerSpec(
            original.Container.Name,
            new ArtifactRef("external"),
            original.Container.Ports,
            original.Container.Mounts,
            original.Container.Environment,
            original.Container.Probes);
        ResourcePlan invalid = CopyPlan(original, original.Workload, container);

        // Act
        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => ResourcePlanValidator.Validate(invalid, context));

        // Assert
        error.Message.ShouldContain("only artifact reference", Case.Sensitive);
    }

    private static PlanContext CreateContext(
        ResourceManifest manifest,
        IResourceOptions? options = null)
        => new(
            manifest,
            options ?? new ResourceOptions(),
            new TestApplicationEnvironment(),
            new Dictionary<string, ResourceManifest>());

    private static ResourcePlan CopyPlan(
        ResourcePlan source,
        WorkloadSpec workload,
        ContainerSpec container)
        => new(
            source.Schema,
            source.Resource,
            source.Kind,
            workload,
            container,
            source.Volumes,
            source.Services,
            source.Exposures,
            source.Hints);

    private static ResourceManifest CreateWebManifest() => new()
    {
        Name = "appa-api",
        Kind = "Web",
        Application = "appa",
        ApplicationModel = "Assimalign.Cohesion.Web.ApplicationModel",
        Artifact = new ResourceManifestArtifact
        {
            Assembly = "Example.AppA.Api",
            Composable = true
        },
        Endpoints =
        [
            new ResourceManifestEndpoint
            {
                Name = "http",
                Scheme = "http",
                Protocol = "tcp",
                ContainerPort = 8080
            },
            new ResourceManifestEndpoint
            {
                Name = "https",
                Scheme = "https",
                Protocol = "tcp",
                ContainerPort = 8443,
                Public = true,
                Certificate = "tls"
            }
        ],
        Probes = new ResourceManifestProbes
        {
            Readiness = new ResourceManifestProbe
            {
                Endpoint = "https",
                Http = "/readyz"
            },
            Liveness = new ResourceManifestProbe
            {
                Endpoint = "http",
                Tcp = true
            }
        },
        ControlPlane = new ResourceManifestControlPlane
        {
            Endpoint = "http",
            Path = "/cohesion/v1"
        },
        EnvironmentVariables = new Dictionary<string, string>
        {
            ["CUSTOM_VALUE"] = "preserved",
            [ResourceEnvironment.Application] = "must-be-overridden"
        },
        Lifecycle = new ResourceManifestLifecycle
        {
            Workload = WorkloadKind.Deployment,
            Replicas = 2,
            MaxReplicas = 4,
            StopGraceSeconds = 30
        },
        Properties = new Dictionary<string, string>
        {
            ["web.kind"] = "Api"
        }
    };

    private static ResourceManifest CreateDatabaseManifest() => new()
    {
        Name = "appa-database",
        Kind = "Database",
        Application = "appa",
        ApplicationModel = "Assimalign.Cohesion.Database.ApplicationModel",
        Artifact = new ResourceManifestArtifact
        {
            Assembly = "Example.AppA.Database",
            Composable = true
        },
        Endpoints =
        [
            new ResourceManifestEndpoint
            {
                Name = "db",
                Scheme = "tcp",
                Protocol = "tcp",
                ContainerPort = 5432
            },
            new ResourceManifestEndpoint
            {
                Name = "admin",
                Scheme = "http",
                Protocol = "tcp",
                ContainerPort = 8080
            }
        ],
        Probes = new ResourceManifestProbes
        {
            Readiness = new ResourceManifestProbe
            {
                Endpoint = "admin",
                Http = "/readyz"
            }
        },
        ControlPlane = new ResourceManifestControlPlane
        {
            Endpoint = "admin",
            Path = "/cohesion/v1"
        },
        Mounts =
        [
            new ResourceManifestMount
            {
                Name = "data",
                Kind = ResourceMountKind.Volume,
                ContainerPath = "/var/lib/database",
                Size = "10Gi"
            }
        ],
        Lifecycle = new ResourceManifestLifecycle
        {
            Workload = WorkloadKind.StatefulSet,
            Replicas = 1,
            MaxReplicas = 1,
            StopGraceSeconds = 30
        },
        Properties = new Dictionary<string, string>
        {
            ["database.engine"] = "PostgreSql"
        }
    };

    private sealed class TestApplicationEnvironment : IApplicationEnvironment
    {
        public EnvironmentName Name => "Development";

        public bool IsDevelopment => true;
    }
}
