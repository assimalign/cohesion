using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// A gateway applying a resource's platform extensions only on its own platform: its provider sees
/// only those, and only those are part of the resource's definition.
/// </summary>
public sealed class ApplicationGatewayExtensionTests : IDisposable
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - ApplicationGateway extensions: ";

    private readonly TemporaryDirectory _directory = new();
    private readonly TestInfrastructure _infrastructure = new();
    private readonly JsonFileApplicationGatewayStore _store;

    public ApplicationGatewayExtensionTests()
    {
        _store = JsonFileApplicationGatewayStore.Create(_directory.FullPath);
    }

    public void Dispose()
    {
        _directory.Dispose();
    }

    [Fact(DisplayName = DisplayPrefix + "The provider is handed only the extensions for its own platform")]
    public async Task RunAsync_WithExtensionsForSeveralPlatforms_ShouldHandTheProviderOnlyItsOwn()
    {
        // Arrange: api has an autoscaler for the test platform and one for Kubernetes.
        IApplicationModel model = CreateModel(testReplicas: 3, kubernetesReplicas: 5);

        // Act
        await using RunningGateway running = await StartAsync(model);

        // Assert: the platform saw only its own; the gateway's aggregate keeps what was declared.
        ApplicationResourceExtension extension = _infrastructure.Seen["api"].Extensions.ShouldHaveSingleItem();
        extension.Platform.Value.ShouldBe("test");
        extension.Value.GetProperty("replicas").GetInt32().ShouldBe(3);
        running.Gateway.Resources.Single().Manifest.Extensions.Count.ShouldBe(2);
    }

    [Fact(DisplayName = DisplayPrefix + "Another platform's extension changing leaves the resource as it runs, operator changes included")]
    public async Task RunAsync_WhenAnotherPlatformsExtensionChanges_ShouldLeaveTheResourceAsItIs()
    {
        // Arrange
        await using (RunningGateway first = await StartAsync(CreateModel(testReplicas: 3, kubernetesReplicas: 5)))
        {
            await WriteLogLevelAsync(first.Gateway, "Debug");
        }

        // Act: only the Kubernetes autoscaler changed.
        await using RunningGateway second = await StartAsync(CreateModel(testReplicas: 3, kubernetesReplicas: 10));

        // Assert
        _infrastructure.Deployed.ShouldHaveSingleItem();
        second.Gateway.Resources.Single().State.ShouldHaveSingleItem().Action.Value.ShouldBe("log-level");
    }

    [Fact(DisplayName = DisplayPrefix + "The provider's own extension changing updates the resource as a new definition")]
    public async Task RunAsync_WhenItsPlatformsExtensionChanges_ShouldUpdateTheResourceWithoutItsState()
    {
        // Arrange
        await using (RunningGateway first = await StartAsync(CreateModel(testReplicas: 3, kubernetesReplicas: 5)))
        {
            await WriteLogLevelAsync(first.Gateway, "Debug");
        }

        // Act: the test platform's autoscaler changed.
        await using RunningGateway second = await StartAsync(CreateModel(testReplicas: 4, kubernetesReplicas: 5));

        // Assert
        _infrastructure.Deployed.Count.ShouldBe(2);
        _infrastructure.Seen["api"].Extensions.ShouldHaveSingleItem().Value.GetProperty("replicas").GetInt32().ShouldBe(4);
        second.Gateway.Resources.Single().State.ShouldBeEmpty();
    }

    private Task<RunningGateway> StartAsync(IApplicationModel model)
    {
        return RunningGateway.StartAsync(_infrastructure, _store, model);
    }

    // api, with an autoscaler for the test platform and one for Kubernetes, as platform packages'
    // verbs would declare them.
    private static IApplicationModel CreateModel(int testReplicas, int kubernetesReplicas)
    {
        IApplicationModelBuilder builder = ApplicationModel.CreateBuilder("root");
        builder.AddResource("Web", "api")
            .WithExtension("test", "autoscaler", Json($$"""{"replicas":{{testReplicas}}}"""))
            .WithExtension("kubernetes", "autoscaler", Json($$"""{"maxReplicas":{{kubernetesReplicas}}}"""));
        return builder.Build();
    }

    // Writes the level on api's instance and records it through the gateway.
    private async Task WriteLogLevelAsync(ApplicationGateway gateway, string level)
    {
        (TestInstance instance, ApplicationInstanceService service) = _infrastructure.Instances.Values.Single();
        ApplicationActionResult write = await service.ControlPlane.ExecuteAsync(
            "log-level",
            ApplicationActionContext.Create(ApplicationActionKind.Write, TestSerializer.Json, Encoding.UTF8.GetBytes($$"""{"Level":"{{level}}"}""")));

        await gateway.RecordAsync(instance.Provider, instance.Name, instance.Key, write.State!);
    }

    private static JsonElement Json(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
