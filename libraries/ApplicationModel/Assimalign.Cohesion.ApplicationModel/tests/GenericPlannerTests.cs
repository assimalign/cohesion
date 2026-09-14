using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

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
        plan.Workload.RestartPolicy.ShouldBe(manifest.Lifecycle.RestartPolicy);
        plan.Container.Artifact.ShouldBe(ArtifactRef.Self);
        plan.Container.Ports.Count.ShouldBe(2);
        plan.Container.Ports[0].Scheme.ShouldBe("http");
        plan.Container.Ports[1].Scheme.ShouldBe("https");
        plan.Container.Probes.Count.ShouldBe(2);
        plan.ControlPlane.Endpoint.ShouldBe(manifest.ControlPlane.Endpoint);
        plan.ControlPlane.Path.ShouldBe(manifest.ControlPlane.Path);
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

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Validate: Should accept the plan-derived Job completion gate")]
    public void Validate_ForJobPlan_ShouldAcceptStoppedAsSatisfying()
    {
        // Arrange
        ResourceManifest manifest = CreateWebManifest() with
        {
            Lifecycle = CreateWebManifest().Lifecycle with { Workload = WorkloadKind.Job }
        };
        PlanContext context = CreateContext(manifest);
        ResourcePlan plan = GenericPlanner.CreatePlan(context);

        // Act
        Should.NotThrow(() => ResourcePlanValidator.Validate(plan, context));

        // Assert
        plan.Workload.Gate.Terminals.ShouldBe(
            new[] { ResourceLifecycle.Stopped, ResourceLifecycle.Failed },
            ignoreOrder: true);
        plan.Workload.Gate.Satisfying.ShouldBe(new[] { ResourceLifecycle.Stopped });
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

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - JSON context: Should accept legacy v1 plans that omit additive realization facts")]
    public void JsonContext_WithLegacyOmissions_ShouldUseCompatibilityDefaults()
    {
        // Arrange
        ResourceManifest manifest = CreateWebManifest() with
        {
            Lifecycle = CreateWebManifest().Lifecycle with { RestartPolicy = "Always" }
        };
        PlanContext context = CreateContext(manifest);
        ResourcePlan plan = GenericPlanner.CreatePlan(context);
        string json = JsonSerializer.Serialize(plan, ResourcePlanJsonContext.Default.ResourcePlan);
        JsonObject document = JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidOperationException("The serialized plan did not contain a JSON object.");
        document.Remove("controlPlane");
        document["workload"]?.AsObject().Remove("restartPolicy");
        foreach (JsonNode? port in document["container"]?["ports"]?.AsArray() ?? [])
        {
            port?.AsObject().Remove("scheme");
        }

        // Act
        ResourcePlan legacy = JsonSerializer.Deserialize(
            document.ToJsonString(),
            ResourcePlanJsonContext.Default.ResourcePlan)
            ?? throw new InvalidOperationException("The legacy plan was null.");

        // Assert
        legacy.ControlPlane.Endpoint.ShouldBeEmpty();
        legacy.ControlPlane.Path.ShouldBeEmpty();
        legacy.Workload.RestartPolicy.ShouldBeEmpty();
        legacy.Container.Ports.ShouldAllBe(binding => binding.Scheme.Length == 0);
        Should.NotThrow(() => ResourcePlanValidator.Validate(legacy, context));
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - JSON context: Should reject an explicitly null control plane")]
    public void JsonContext_WithNullControlPlane_ShouldThrow()
    {
        // Arrange
        ResourcePlan plan = GenericPlanner.CreatePlan(CreateContext(CreateWebManifest()));
        string json = JsonSerializer.Serialize(plan, ResourcePlanJsonContext.Default.ResourcePlan);
        JsonObject document = JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidOperationException("The serialized plan did not contain a JSON object.");
        document["controlPlane"] = null;
        JsonObject nullEndpoint = JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidOperationException("The serialized plan did not contain a JSON object.");
        nullEndpoint["controlPlane"]!.AsObject()["endpoint"] = null;

        // Act
        JsonException error = Should.Throw<JsonException>(() => JsonSerializer.Deserialize(
            document.ToJsonString(),
            ResourcePlanJsonContext.Default.ResourcePlan));
        JsonException endpointError = Should.Throw<JsonException>(() => JsonSerializer.Deserialize(
            nullEndpoint.ToJsonString(),
            ResourcePlanJsonContext.Default.ResourcePlan));

        // Assert
        error.Message.ShouldContain("controlPlane must not be null", Case.Sensitive);
        endpointError.Message.ShouldContain("property 'endpoint' must be a string", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Validate: Should reject explicitly null additive string facts")]
    public void Validate_WithNullAdditiveStringFacts_ShouldThrow()
    {
        // Arrange
        ResourceManifest manifest = CreateWebManifest();
        PlanContext context = CreateContext(manifest);
        ResourcePlan original = GenericPlanner.CreatePlan(context);
        ResourcePlan nullRestartPolicy = CopyPlan(
            original,
            original.Workload with { RestartPolicy = null! },
            original.Container);
        var ports = new List<PortBinding>(original.Container.Ports)
        {
            [0] = original.Container.Ports[0] with { Scheme = null! }
        };
        var nullSchemeContainer = new ContainerSpec(
            original.Container.Name,
            original.Container.Artifact,
            ports,
            original.Container.Mounts,
            original.Container.Environment,
            original.Container.Probes);
        ResourcePlan nullScheme = CopyPlan(original, original.Workload, nullSchemeContainer);
        ResourcePlan nullControlPlaneField = CopyPlan(
            original,
            original.Workload,
            original.Container,
            new ControlPlaneSpec(null!, original.ControlPlane.Path));

        // Act
        InvalidOperationException restartError = Should.Throw<InvalidOperationException>(
            () => ResourcePlanValidator.Validate(nullRestartPolicy, context));
        InvalidOperationException schemeError = Should.Throw<InvalidOperationException>(
            () => ResourcePlanValidator.Validate(nullScheme, context));
        InvalidOperationException controlPlaneError = Should.Throw<InvalidOperationException>(
            () => ResourcePlanValidator.Validate(nullControlPlaneField, context));

        // Assert
        restartError.Message.ShouldContain("restart policy must not be null", Case.Sensitive);
        schemeError.Message.ShouldContain("scheme must not be null", Case.Sensitive);
        controlPlaneError.Message.ShouldContain("endpoint must not be null", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Plan records: Should preserve legacy constructors and deconstruction")]
    public void PlanRecords_WithLegacyShape_ShouldPreserveSourceCompatibility()
    {
        // Arrange
        var binding = new PortBinding("http", 8080, "tcp");
        var workload = new WorkloadSpec(
            WorkloadKind.Deployment,
            1,
            StableIdentity: false,
            ReadinessGate.For(WorkloadKind.Deployment),
            StopGraceSeconds: 30);

        // Act
        (string endpoint, int port, string protocol) = binding;
        (WorkloadKind kind, int replicas, bool stableIdentity, ReadinessGate gate, int stopGrace) = workload;

        // Assert
        endpoint.ShouldBe("http");
        port.ShouldBe(8080);
        protocol.ShouldBe("tcp");
        kind.ShouldBe(WorkloadKind.Deployment);
        replicas.ShouldBe(1);
        stableIdentity.ShouldBeFalse();
        gate.ShouldBeSameAs(workload.Gate);
        stopGrace.ShouldBe(30);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Validate: Should reject partially populated control-plane facts")]
    public void Validate_WithPartialControlPlane_ShouldThrow()
    {
        // Arrange
        ResourceManifest manifest = CreateWebManifest();
        PlanContext context = CreateContext(manifest);
        ResourcePlan original = GenericPlanner.CreatePlan(context);
        ResourcePlan invalid = CopyPlan(
            original,
            original.Workload,
            original.Container,
            new ControlPlaneSpec(original.ControlPlane.Endpoint));

        // Act
        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => ResourcePlanValidator.Validate(invalid, context));

        // Assert
        error.Message.ShouldContain("must either both be declared or both be omitted", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Validate: Should reject endpoint schemes that differ from the manifest")]
    public void Validate_WithMismatchedEndpointScheme_ShouldThrow()
    {
        // Arrange
        ResourceManifest manifest = CreateWebManifest();
        PlanContext context = CreateContext(manifest);
        ResourcePlan original = GenericPlanner.CreatePlan(context);
        var ports = new List<PortBinding>(original.Container.Ports)
        {
            [0] = original.Container.Ports[0] with { Scheme = "https" }
        };
        var container = new ContainerSpec(
            original.Container.Name,
            original.Container.Artifact,
            ports,
            original.Container.Mounts,
            original.Container.Environment,
            original.Container.Probes);
        ResourcePlan invalid = CopyPlan(original, original.Workload, container);

        // Act
        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => ResourcePlanValidator.Validate(invalid, context));

        // Assert
        error.Message.ShouldContain("does not match manifest scheme", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Validate: Should reject partially omitted endpoint schemes")]
    public void Validate_WithPartiallyOmittedEndpointSchemes_ShouldThrow()
    {
        // Arrange
        ResourceManifest manifest = CreateWebManifest();
        PlanContext context = CreateContext(manifest);
        ResourcePlan original = GenericPlanner.CreatePlan(context);
        var ports = new List<PortBinding>(original.Container.Ports)
        {
            [0] = original.Container.Ports[0] with { Scheme = string.Empty }
        };
        var container = new ContainerSpec(
            original.Container.Name,
            original.Container.Artifact,
            ports,
            original.Container.Mounts,
            original.Container.Environment,
            original.Container.Probes);
        ResourcePlan invalid = CopyPlan(original, original.Workload, container);

        // Act
        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => ResourcePlanValidator.Validate(invalid, context));

        // Assert
        error.Message.ShouldContain("must either all be declared or all be omitted", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Validate: Should reject restart policies that differ from the manifest")]
    public void Validate_WithMismatchedRestartPolicy_ShouldThrow()
    {
        // Arrange
        ResourceManifest manifest = CreateWebManifest();
        PlanContext context = CreateContext(manifest);
        ResourcePlan original = GenericPlanner.CreatePlan(context);
        WorkloadSpec workload = original.Workload with { RestartPolicy = "Never" };
        ResourcePlan invalid = CopyPlan(original, workload, original.Container);

        // Act
        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => ResourcePlanValidator.Validate(invalid, context));

        // Assert
        error.Message.ShouldContain("does not match manifest restart policy", Case.Sensitive);
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
            original.Workload.StopGraceSeconds,
            original.Workload.RestartPolicy);
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

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Certificate plan: Validates each endpoint independently")]
    [InlineData("tls", ResourceMountKind.Secret, true)]
    [InlineData("wrong", ResourceMountKind.Secret, false)]
    [InlineData("tls", ResourceMountKind.Configuration, false)]
    [InlineData(null, ResourceMountKind.Secret, false)]
    public void Validate_CertificateBindings_ShouldEnforceManifestAndMount(string? certificate, ResourceMountKind kind, bool valid)
    {
        ResourceManifest manifest = CreateWebManifest();
        PlanContext context = CreateContext(manifest);
        ResourcePlan plan = GenericPlanner.CreatePlan(context);
        PortBinding[] ports = [plan.Container.Ports[0], plan.Container.Ports[1] with { Certificate = certificate! }];
        ResourcePlan changed = CopyPlan(plan, plan.Workload, new ContainerSpec(
            plan.Container.Name, plan.Container.Artifact, ports,
            [plan.Container.Mounts[0] with { Kind = kind }],
            plan.Container.Environment, plan.Container.Probes));
        if (valid)
        {
            Should.NotThrow(() => ResourcePlanValidator.Validate(changed, context));
            changed.Container.Ports[0].Certificate.ShouldBeEmpty();
        }
        else
        {
            Should.Throw<InvalidOperationException>(() => ResourcePlanValidator.Validate(changed, context));
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Certificate plan: Reserved public requires no mount")]
    public void Validate_PublicCertificate_ShouldAcceptWithoutMount()
    {
        ResourceManifest source = CreateWebManifest();
        ResourceManifest manifest = source with
        {
            Endpoints = [source.Endpoints[0], source.Endpoints[1] with { Certificate = "public" }],
            Mounts = [],
        };
        PlanContext context = CreateContext(manifest);
        ResourcePlan plan = GenericPlanner.CreatePlan(context);
        ResourcePlan copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(plan, ResourcePlanJsonContext.Default.ResourcePlan), ResourcePlanJsonContext.Default.ResourcePlan).ShouldNotBeNull();
        copy.Container.Ports[1].Certificate.ShouldBe("public");
        Should.NotThrow(() => ResourcePlanValidator.Validate(copy, context));
        new PortBinding("http", 8080, "tcp").Certificate.ShouldBeEmpty();
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
        ContainerSpec container,
        ControlPlaneSpec? controlPlane = null)
        => new(
            source.Schema,
            source.Resource,
            source.Kind,
            workload,
            container,
            source.Volumes,
            source.Services,
            source.Exposures,
            source.Hints,
            controlPlane ?? source.ControlPlane);

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
