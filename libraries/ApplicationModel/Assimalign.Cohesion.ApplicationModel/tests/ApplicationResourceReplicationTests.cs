using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

public class ApplicationResourceReplicationTests
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - Replication: ";

    [Fact(DisplayName = DisplayPrefix + "A write on one instance reaches running instances by restore and later ones by registration")]
    public async Task ExecuteAsync_WithRecordedStatefulWrite_ShouldReachEveryInstance()
    {
        // Arrange: a deployment declares orders-api, and the gateway builds its aggregate from the manifest.
        ApplicationBuilder root = Application.CreateBuilder("root", []);
        root.AddResource("Web", "orders-api");
        IApplication application = root.Build();
        ApplicationResource resource = ApplicationResource.Create(application.Model.Manifest.Resources[0]);
        var publisher = new ApplicationResourcePublisher(resource);
        var instances = new Dictionary<ResourceInstanceKey, (TestResource Resource, ResourceControlPlane ControlPlane)>();

        async Task<TestResource> StartInstanceAsync()
        {
            var instance = new TestResource();
            ResourceControlPlane controlPlane = ResourceControlPlane.Create(instance, publisher);
            await controlPlane.StartAsync(CancellationToken.None);
            instances.Add(instance.Key, (instance, controlPlane));
            return instance;
        }

        TestResource first = await StartInstanceAsync();
        TestResource second = await StartInstanceAsync();

        // Act: an operator's write runs once, on the first instance; the gateway records its
        // snapshot and restores it on the instances the aggregate names.
        ResourceActionResult write = await instances[first.Key].ControlPlane.ExecuteAsync(
            "log-level",
            ResourceActionContext.Create(ResourceActionKind.Write, """{"Level":"Debug"}"""u8.ToArray()),
            CancellationToken.None);
        foreach (ResourceInstanceKey target in resource.Record(first.Key, write.State!))
        {
            ResourceActionResult restore = await instances[target].ControlPlane.RestoreAsync(write.State!, CancellationToken.None);
            restore.Status.ShouldBe(ResourceActionStatus.Succeeded);
        }

        TestResource scaledOut = await StartInstanceAsync();

        // Assert
        first.LogLevel.Current.Level.ShouldBe("Debug");
        second.LogLevel.Current.Level.ShouldBe("Debug");
        scaledOut.LogLevel.Current.Level.ShouldBe("Debug");
        resource.Instances.Count.ShouldBe(3);
        resource.State.ShouldHaveSingleItem().Action.ShouldBe(new ResourceActionName("log-level"));
    }
}
