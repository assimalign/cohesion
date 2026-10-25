using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// Drives the generic platform-surface flow and the record-change hook of the guided base through a test gateway
/// with a file record store: what a platform declares, what an operator overrides, and how long the override lasts.
/// </summary>
public sealed class PlatformSurfaceManagementTests
{
    private const string SurfaceSchema = """
        {"type":"object","properties":{"minimum":{"type":"integer"},"maximum":{"type":"integer"}},"required":["maximum"]}
        """;

    private const string RuntimeSurface = """
        {"schema":"cohesion/runtime/v1","configuration":[
        {"path":"Web:Cors","mutability":"live","scope":"process","claimable":false,"schema":{"type":["object","null"],"properties":{"origin":{"type":["string","null"]}}}},
        {"path":"Orders:Limits","mutability":"restart","scope":"process","claimable":false,"schema":{"type":["object","null"],"properties":{"maxItems":{"type":"integer"}}}}],
        "actions":[],"tools":[]}
        """;

    private static readonly ApplicationName _application = ApplicationName.Parse("appa");
    private static readonly ResourceName _resource = "worker";
    private static readonly ResourceExtensionKey _surface = "test/scale";
    private static readonly ApplicationCaller _operator = new(
        null, "operator@tests", ApplicationCallerKind.Operator, [], [ResourceManagementOperations.All]);

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Platform surface: Should serve the declared value before any override")]
    public async Task GetPlatformSurfaceAsync_WithoutOverride_ShouldReturnDeclaredValue()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var directory = new TemporaryTestDirectory();
        (TestGateway gateway, IApplicationModel model) = Create(directory.Path);
        IApplicationGateway control = gateway;
        await control.StartAsync(model, cancellation.Token);
        try
        {
            // Act
            ResourceManagementResult result = await gateway.GetPlatformSurfaceAsync(_application, _resource, _surface, cancellation.Token);

            // Assert
            result.Status.ShouldBe(ResourceManagementStatus.Ok);
            result.Version.ShouldNotBeNull();
            using JsonDocument document = JsonDocument.Parse(result.Document);
            document.RootElement.GetProperty("surface").GetString().ShouldBe("test/scale");
            document.RootElement.GetProperty("declared").GetProperty("maximum").GetInt32().ShouldBe(3);
            document.RootElement.GetProperty("override").ValueKind.ShouldBe(JsonValueKind.Null);
            document.RootElement.GetProperty("value").GetProperty("maximum").GetInt32().ShouldBe(3);
            document.RootElement.GetProperty("observed").GetProperty("replicas").GetInt32().ShouldBe(2);
        }
        finally
        {
            await control.StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Platform surface: Should store an override, apply it to the platform and keep it for the generation")]
    public async Task ChangePlatformSurfaceAsync_ValidPatch_ShouldStoreApplyAndKeepForTheGeneration()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var directory = new TemporaryTestDirectory();
        var inputs = new List<ResourceInputs>();
        (TestGateway gateway, IApplicationModel model) = Create(directory.Path, inputs);
        IApplicationGateway control = gateway;
        await control.StartAsync(model, cancellation.Token);
        string before = (await gateway.GetPlatformSurfaceAsync(_application, _resource, _surface, cancellation.Token)).Version!;
        ResourceManagementResult changed;
        try
        {
            // Act
            changed = await gateway.ChangePlatformSurfaceAsync(
                _application, _resource, _surface, Encoding.UTF8.GetBytes("""{"maximum":5}"""), before, _operator, cancellation.Token);
        }
        finally
        {
            await control.StopAsync(CancellationToken.None);
        }

        // A second session of the same model is the same generation, so the override reaches the compiler again.
        await control.StartAsync(model, cancellation.Token);
        await control.StopAsync(CancellationToken.None);

        // Assert
        changed.Status.ShouldBe(ResourceManagementStatus.Ok);
        changed.Version.ShouldNotBe(before);
        using (JsonDocument document = JsonDocument.Parse(changed.Document))
        {
            document.RootElement.GetProperty("override").GetProperty("maximum").GetInt32().ShouldBe(5);
            document.RootElement.GetProperty("value").GetProperty("maximum").GetInt32().ShouldBe(5);
        }

        gateway.AppliedSurfaces.ShouldHaveSingleItem().Surface.ShouldBe(_surface);
        JsonDocument.Parse(gateway.AppliedSurfaces[0].Value).RootElement.GetProperty("maximum").GetInt32().ShouldBe(5);
        inputs.Count.ShouldBe(2);
        inputs[0].PlatformOverrides.ShouldBeEmpty();
        inputs[1].DeploymentGeneration.ShouldBe(inputs[0].DeploymentGeneration);
        inputs[1].PlatformOverrides[_surface].GetProperty("maximum").GetInt32().ShouldBe(5);
        File.ReadAllText(Path.Combine(directory.Path, "appa", "worker", ".state", "record.json"))
            .ShouldContain("\"test/scale\"", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Platform surface: Should refuse a patch the schema rejects")]
    public async Task ChangePlatformSurfaceAsync_UnknownMember_ShouldRefuseAsInvalidConfiguration()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var directory = new TemporaryTestDirectory();
        (TestGateway gateway, IApplicationModel model) = Create(directory.Path);
        IApplicationGateway control = gateway;
        await control.StartAsync(model, cancellation.Token);
        try
        {
            // Act
            ResourceManagementResult result = await gateway.ChangePlatformSurfaceAsync(
                _application, _resource, _surface, Encoding.UTF8.GetBytes("""{"maximun":5}"""), null, _operator, cancellation.Token);

            // Assert
            result.Status.ShouldBe(ResourceManagementStatus.InvalidConfiguration);
            gateway.AppliedSurfaces.ShouldBeEmpty();
        }
        finally
        {
            await control.StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Platform surface: Should drop the override for a JSON null patch")]
    public async Task ChangePlatformSurfaceAsync_NullPatch_ShouldDropOverride()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var directory = new TemporaryTestDirectory();
        (TestGateway gateway, IApplicationModel model) = Create(directory.Path);
        IApplicationGateway control = gateway;
        await control.StartAsync(model, cancellation.Token);
        try
        {
            await gateway.ChangePlatformSurfaceAsync(
                _application, _resource, _surface, Encoding.UTF8.GetBytes("""{"maximum":5}"""), null, _operator, cancellation.Token);

            // Act
            ResourceManagementResult reset = await gateway.ChangePlatformSurfaceAsync(
                _application, _resource, _surface, "null"u8.ToArray(), null, _operator, cancellation.Token);

            // Assert
            reset.Status.ShouldBe(ResourceManagementStatus.Ok);
            using JsonDocument document = JsonDocument.Parse(reset.Document);
            document.RootElement.GetProperty("override").ValueKind.ShouldBe(JsonValueKind.Null);
            document.RootElement.GetProperty("value").GetProperty("maximum").GetInt32().ShouldBe(3);
            JsonDocument.Parse(gateway.AppliedSurfaces[^1].Value).RootElement.GetProperty("maximum").GetInt32().ShouldBe(3);
        }
        finally
        {
            await control.StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Platform surface: Should keep a stored override when the platform does not take it")]
    public async Task ChangePlatformSurfaceAsync_ApplyFails_ShouldStillStoreTheOverride()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var directory = new TemporaryTestDirectory();
        (TestGateway gateway, IApplicationModel model) = Create(directory.Path);
        gateway.ApplyFailure = new InvalidOperationException("platform unavailable");
        IApplicationGateway control = gateway;
        await control.StartAsync(model, cancellation.Token);
        try
        {
            // Act
            ResourceManagementResult result = await gateway.ChangePlatformSurfaceAsync(
                _application, _resource, _surface, Encoding.UTF8.GetBytes("""{"maximum":4}"""), null, _operator, cancellation.Token);

            // Assert
            result.Status.ShouldBe(ResourceManagementStatus.Ok);
            ResourceManagementResult read = await gateway.GetPlatformSurfaceAsync(_application, _resource, _surface, cancellation.Token);
            using JsonDocument document = JsonDocument.Parse(read.Document);
            document.RootElement.GetProperty("override").GetProperty("maximum").GetInt32().ShouldBe(4);
        }
        finally
        {
            await control.StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Platform surface: Should refuse a key the platform does not publish")]
    public async Task GetPlatformSurfaceAsync_UnknownKey_ShouldRefuse()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var directory = new TemporaryTestDirectory();
        (TestGateway gateway, IApplicationModel model) = Create(directory.Path);
        IApplicationGateway control = gateway;
        await control.StartAsync(model, cancellation.Token);
        try
        {
            // Act
            ResourceManagementResult result = await gateway.GetPlatformSurfaceAsync(
                _application, _resource, "test/missing", cancellation.Token);

            // Assert
            result.Status.ShouldBe(ResourceManagementStatus.UnknownPlatformSurface);
        }
        finally
        {
            await control.StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Platform surface: Should drop and report a platform override when the deployment changes")]
    public async Task StartAsync_NewGeneration_ShouldDropPlatformOverrides()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var directory = new TemporaryTestDirectory();
        var inputs = new List<ResourceInputs>();
        (TestGateway gateway, IApplicationModel model) = Create(directory.Path, inputs);
        IApplicationGateway control = gateway;
        await control.StartAsync(model, cancellation.Token);
        await gateway.ChangePlatformSurfaceAsync(
            _application, _resource, _surface, Encoding.UTF8.GetBytes("""{"maximum":5}"""), null, _operator, cancellation.Token);
        await control.StopAsync(CancellationToken.None);
        (TestGateway redeployed, IApplicationModel changed) = Create(directory.Path, inputs, configure: true);

        // Act
        IApplicationGateway next = redeployed;
        await next.StartAsync(changed, cancellation.Token);
        ResourceManagementResult read;
        try
        {
            read = await redeployed.GetPlatformSurfaceAsync(_application, _resource, _surface, cancellation.Token);
        }
        finally
        {
            await next.StopAsync(CancellationToken.None);
        }

        // Assert
        inputs[^1].DeploymentGeneration.ShouldNotBe(inputs[0].DeploymentGeneration);
        inputs[^1].PlatformOverrides.ShouldBeEmpty();
        using JsonDocument document = JsonDocument.Parse(read.Document);
        document.RootElement.GetProperty("override").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Records: Should hand an override write to the platform with both records")]
    public async Task ChangeOverrideAsync_Write_ShouldNotifyTheRecordChange()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var directory = new TemporaryTestDirectory();
        (TestGateway gateway, IApplicationModel model) = Create(directory.Path);
        IApplicationGateway control = gateway;
        await control.StartAsync(model, cancellation.Token);
        try
        {
            // Act
            ResourceManagementResult result = await gateway.ChangeOverrideAsync(
                _application, _resource, "Orders:Limits", ResourceOverrideChangeKind.Merge,
                Encoding.UTF8.GetBytes("""{"maxItems":9}"""), null, _operator, cancellation.Token);

            // Assert
            result.Status.ShouldBe(ResourceManagementStatus.Ok);
            (ResourceRecord previous, ResourceRecord current) = gateway.RecordChanges.ShouldHaveSingleItem();
            previous.Override.IsEmpty.ShouldBeTrue();
            current.Version.ShouldBe(result.Version);
            ResourceSurface? surface = TestGateway.RuntimeSurfaceOf(model, model.Descriptors[0]);
            surface.ShouldNotBeNull();
            previous.GetRestartDigest(surface).ShouldBeNull();
            current.GetRestartDigest(surface).ShouldNotBeNull();
        }
        finally
        {
            await control.StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Records: Should hand the platform the restart digest of the record it delivers")]
    public async Task StartAsync_RestartClassOverride_ShouldDeliverTheRestartDigest()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var directory = new TemporaryTestDirectory();
        var inputs = new List<ResourceInputs>();
        (TestGateway gateway, IApplicationModel model) = Create(directory.Path, inputs);
        IApplicationGateway control = gateway;
        await control.StartAsync(model, cancellation.Token);
        ResourceManagementResult changed;
        try
        {
            changed = await gateway.ChangeOverrideAsync(
                _application, _resource, "Orders:Limits", ResourceOverrideChangeKind.Merge,
                Encoding.UTF8.GetBytes("""{"maxItems":9}"""), null, _operator, cancellation.Token);
        }
        finally
        {
            await control.StopAsync(CancellationToken.None);
        }

        // Act
        await control.StartAsync(model, cancellation.Token);
        await control.StopAsync(CancellationToken.None);

        // Assert
        changed.Status.ShouldBe(ResourceManagementStatus.Ok);
        ResourceRecord? record = await gateway.Records!.ReadAsync(_application, _resource, cancellation.Token);
        record.ShouldNotBeNull();
        string? expected = record.GetRestartDigest(TestGateway.RuntimeSurfaceOf(model, model.Descriptors[0]));
        expected.ShouldNotBeNull();
        inputs.Count.ShouldBe(2);
        inputs[0].RestartDigest.ShouldBeNull();
        inputs[1].RestartDigest.ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Records: Should delete a resource's record at teardown")]
    public async Task UninstallAsync_WithRecordStore_ShouldDeleteTheRecord()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var directory = new TemporaryTestDirectory();
        (TestGateway gateway, IApplicationModel model) = Create(directory.Path);
        IApplicationGateway control = gateway;
        await control.StartAsync(model, cancellation.Token);
        await control.StopAsync(CancellationToken.None);
        (await gateway.Records!.ReadAsync(_application, _resource, cancellation.Token)).ShouldNotBeNull();

        // Act
        await control.UninstallAsync(model, cancellation.Token);

        // Assert
        (await gateway.Records.ReadAsync(_application, _resource, cancellation.Token)).ShouldBeNull();
    }

    private static (TestGateway Gateway, IApplicationModel Model) Create(
        string stateDirectory,
        List<ResourceInputs>? inputs = null,
        bool configure = false)
    {
        var reconciled = new List<string>();
        var controller = new RecordingController(reconciled, new List<string>())
        {
            AfterReconcile = context => inputs?.Add(context.Inputs),
        };
        var gateway = new TestGateway(new InMemoryResourceStateManager(), [controller])
        {
            Records = FileResourceRecordStore.Create(stateDirectory),
            Surfaces = static (_, _) =>
            [
                ResourcePlatformSurface.Create(_surface, SurfaceSchema, JsonDocument.Parse("""{"maximum":3}""").RootElement),
            ],
            ObservedSurface = static _ => JsonDocument.Parse("""{"replicas":2}""").RootElement,
        };
        IApplicationBuilder builder = Application.CreateBuilder(_application, []).UseGateway(gateway);
        IApplicationResourceDescriptor worker = builder.AddResource(CreateManifest());
        if (configure)
        {
            worker.Configure("Web:Cors", """{"origin":"https://portal.example.com"}""");
        }

        return (gateway, builder.Build().Model);
    }

    private static ResourceManifest CreateManifest() =>
        new ResourceManifest
        {
            Name = "worker",
            Application = "appa",
            Kind = "test",
            ApplicationModel = "Assimalign.Cohesion.Test.ApplicationModel",
            Artifact = new ResourceManifestArtifact
            {
                Assembly = "test.dll",
                AppHost = "test",
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
            ControlPlane = new ResourceManifestControlPlane
            {
                Endpoint = "http",
                Path = "/cohesion/v1",
            },
            Lifecycle = new ResourceManifestLifecycle
            {
                Workload = WorkloadKind.Deployment,
                Replicas = 1,
                RestartPolicy = "Never",
            },
        } with { Surface = ResourceSurface.Parse(RuntimeSurface) };
}
