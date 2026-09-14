using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

public class KindMatrixTests
{
    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - CreatePlan: Should match the Web golden fixture")]
    public void CreatePlan_ForWebManifest_ShouldMatchGoldenFixture()
    {
        // Arrange
        ResourceManifest manifest = CreateWebManifest();
        PlanContext context = CreateContext(manifest);

        // Act
        ResourcePlan plan = GenericPlanner.CreatePlan(context);

        // Assert
        ShouldMatchFixture(plan, "web.json");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - CreatePlan: Should match the Database golden fixture")]
    public void CreatePlan_ForDatabaseManifest_ShouldMatchGoldenFixture()
    {
        // Arrange
        ResourceManifest manifest = CreateDatabaseManifest();
        PlanContext context = CreateContext(manifest);

        // Act
        ResourcePlan plan = GenericPlanner.CreatePlan(context);

        // Assert
        ShouldMatchFixture(plan, "database.json");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - CreatePlan: Should match the volume-mounted generic golden fixture")]
    public void CreatePlan_ForVolumeMountedGenericManifest_ShouldMatchGoldenFixture()
    {
        // Arrange
        ResourceManifest manifest = CreateVolumeMountedGenericManifest();
        PlanContext context = CreateContext(manifest);

        // Act
        ResourcePlan plan = GenericPlanner.CreatePlan(context);

        // Assert
        ShouldMatchFixture(plan, "generic-volume.json");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - CreatePlan: Should match the DaemonSet golden fixture")]
    public void CreatePlan_ForDaemonSetManifest_ShouldMatchGoldenFixture()
    {
        // Arrange
        ResourceManifest manifest = CreateDaemonSetManifest();
        PlanContext context = CreateContext(manifest);

        // Act
        ResourcePlan plan = GenericPlanner.CreatePlan(context);

        // Assert
        ShouldMatchFixture(plan, "daemon-set.json");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - CreatePlan: Should match the Job golden fixture")]
    public void CreatePlan_ForJobManifest_ShouldMatchGoldenFixture()
    {
        // Arrange
        ResourceManifest manifest = CreateJobManifest();
        PlanContext context = CreateContext(manifest);

        // Act
        ResourcePlan plan = GenericPlanner.CreatePlan(context);

        // Assert
        ShouldMatchFixture(plan, "job.json");
    }

    private static PlanContext CreateContext(ResourceManifest manifest)
        => new(
            manifest,
            new ResourceOptions(),
            new TestApplicationEnvironment(),
            new Dictionary<string, ResourceManifest>());

    private static void ShouldMatchFixture(ResourcePlan plan, string fixtureFileName)
    {
        string fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "plans",
            fixtureFileName);
        string fixtureJson = File.ReadAllText(fixturePath);
        string actualJson = JsonSerializer.Serialize(
            plan,
            ResourcePlanJsonContext.Default.ResourcePlan);
        _ = JsonSerializer.Deserialize(
            fixtureJson,
            ResourcePlanJsonContext.Default.ResourcePlan)
            ?? throw new InvalidOperationException(
                $"Golden plan fixture '{fixtureFileName}' deserialized to null.");
        using JsonDocument fixtureDocument = JsonDocument.Parse(fixtureJson);
        using JsonDocument actualDocument = JsonDocument.Parse(actualJson);

        JsonElement.DeepEquals(
            actualDocument.RootElement,
            fixtureDocument.RootElement)
            .ShouldBeTrue($"Plan output did not match golden fixture '{fixtureFileName}'.");
    }

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
        Mounts = [new ResourceManifestMount { Name = "tls", Kind = ResourceMountKind.Secret, ContainerPath = "/cohesion/mounts/tls" }],
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
            MaxReplicas = 4
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
            MaxReplicas = 1
        },
        Properties = new Dictionary<string, string>
        {
            ["database.engine"] = "PostgreSql"
        }
    };

    private static ResourceManifest CreateVolumeMountedGenericManifest()
    {
        ResourceManifest manifest = TestManifestFactory.Create(
            name: "volume-worker",
            replicas: 2,
            maxReplicas: 2);

        return manifest with
        {
            Mounts =
            [
                new ResourceManifestMount
                {
                    Name = "work",
                    Kind = ResourceMountKind.Volume,
                    ContainerPath = "/var/lib/worker",
                    Size = "5Gi"
                }
            ],
            Lifecycle = manifest.Lifecycle with
            {
                Workload = WorkloadKind.StatefulSet
            }
        };
    }

    private static ResourceManifest CreateDaemonSetManifest()
    {
        ResourceManifest manifest = TestManifestFactory.Create(
            name: "node-agent",
            maxReplicas: null);

        return manifest with
        {
            Lifecycle = manifest.Lifecycle with
            {
                Workload = WorkloadKind.DaemonSet
            }
        };
    }

    private static ResourceManifest CreateJobManifest()
    {
        ResourceManifest manifest = TestManifestFactory.Create(
            name: "schema-migrate",
            maxReplicas: 1);

        return manifest with
        {
            Kind = "Job",
            ApplicationModel = "Example.Job.ApplicationModel",
            Artifact = new ResourceManifestArtifact
            {
                Assembly = "Example.SchemaMigrate"
            },
            Lifecycle = manifest.Lifecycle with
            {
                Workload = WorkloadKind.Job
            }
        };
    }

    private sealed class TestApplicationEnvironment : IApplicationEnvironment
    {
        public EnvironmentName Name => AppEnvironment.Keys.Development;

        public bool IsLocal => false;

        public bool IsDevelopment => true;
    }
}
