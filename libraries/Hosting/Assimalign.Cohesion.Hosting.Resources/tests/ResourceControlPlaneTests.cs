using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Hosting.Resources.Tests;

public class ResourceControlPlaneTests
{
    private const string DisplayPrefix = "Cohesion Test [Hosting] - ResourceControlPlane: ";

    private static readonly ResourceActionName _logLevel = "log-level";
    private static readonly ResourceActionName _flushCache = "flush-cache";

    [Fact(DisplayName = DisplayPrefix + "Two actions with one name are refused")]
    public void Create_WithDuplicateActionNames_ShouldThrow()
    {
        // Arrange
        var resource = new TestResource(new TestAction(), new TestAction());

        // Act & Assert
        Should.Throw<ArgumentException>(() => ResourceControlPlane.Create(resource, new TestPublisher()));
    }

    [Fact(DisplayName = DisplayPrefix + "State on an action that accepts no writes is refused")]
    public void Create_WithStatefulReadOnlyAction_ShouldThrow()
    {
        // Arrange
        var state = ResourceActionState<LogLevelSetting>.Create(new LogLevelSetting("Information"), TestJsonContext.Default.LogLevelSetting, _ => { });
        var resource = new TestResource(new TestAction { Kind = ResourceActionKind.Read, State = state });

        // Act & Assert
        Should.Throw<ArgumentException>(() => ResourceControlPlane.Create(resource, new TestPublisher()));
    }

    [Theory(DisplayName = DisplayPrefix + "An action must accept Read, Write, or both")]
    [InlineData(ResourceActionKind.None)]
    [InlineData((ResourceActionKind)4)]
    public void Create_WithInvalidActionKind_ShouldThrow(ResourceActionKind kind)
    {
        // Arrange
        var resource = new TestResource(new TestAction { Kind = kind });

        // Act & Assert
        Should.Throw<ArgumentException>(() => ResourceControlPlane.Create(resource, new TestPublisher()));
    }

    [Fact(DisplayName = DisplayPrefix + "An instance without a key is refused")]
    public void Create_WithDefaultInstanceKey_ShouldThrow()
    {
        // Arrange
        var resource = new TestResource { Key = default };

        // Act & Assert
        Should.Throw<ArgumentException>(() => ResourceControlPlane.Create(resource, new TestPublisher()));
    }

    [Fact(DisplayName = DisplayPrefix + "The manifest describes the instance and its actions in name order")]
    public void Manifest_AfterCreate_ShouldDescribeInstanceAndActions()
    {
        // Arrange
        var resource = new TestResource(new LogLevelAction(), new TestAction());

        // Act
        ResourceManifest manifest = ResourceControlPlane.Create(resource, new TestPublisher()).Manifest;

        // Assert
        manifest.Provider.ShouldBe(resource.Provider);
        manifest.Name.ShouldBe(resource.Name);
        manifest.Key.ShouldBe(resource.Key);
        manifest.Actions.Count.ShouldBe(2);
        manifest.Actions[0].ShouldBe(new ResourceActionDescriptor(_flushCache, ResourceActionKind.Write, false));
        manifest.Actions[1].ShouldBe(new ResourceActionDescriptor(_logLevel, ResourceActionKind.ReadWrite, true));
    }

    [Fact(DisplayName = DisplayPrefix + "Start publishes the manifest and restores the state the publisher returns")]
    public async Task StartAsync_WithReturnedSnapshot_ShouldRestoreBeforeCompleting()
    {
        // Arrange
        var action = new LogLevelAction();
        var publisher = new TestPublisher(ResourceStateSnapshot.Create(_logLevel, """{"Level":"Warning"}"""u8.ToArray()));
        ResourceControlPlane controlPlane = ResourceControlPlane.Create(new TestResource(action), publisher);

        // Act
        await controlPlane.StartAsync(CancellationToken.None);

        // Assert
        publisher.Published.ShouldHaveSingleItem().ShouldBeSameAs(controlPlane.Manifest);
        action.Current.Level.ShouldBe("Warning");
        action.Applied.ShouldHaveSingleItem().Level.ShouldBe("Warning");
    }

    [Fact(DisplayName = DisplayPrefix + "A returned snapshot for an unknown action fails the start and leaves it unstarted")]
    public async Task StartAsync_WithSnapshotForUnknownAction_ShouldFailAndStayUnstarted()
    {
        // Arrange
        var publisher = new TestPublisher(ResourceStateSnapshot.Create("cors", """{"Origins":[]}"""u8.ToArray()));
        ResourceControlPlane controlPlane = ResourceControlPlane.Create(new TestResource(new LogLevelAction()), publisher);

        // Act
        await Should.ThrowAsync<InvalidOperationException>(() => controlPlane.StartAsync(CancellationToken.None));

        // Assert
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await controlPlane.ExecuteAsync(_logLevel, ResourceActionContext.Create(ResourceActionKind.Read), CancellationToken.None));
    }

    [Fact(DisplayName = DisplayPrefix + "A control plane starts once")]
    public async Task StartAsync_Twice_ShouldThrow()
    {
        // Arrange
        ResourceControlPlane controlPlane = ResourceControlPlane.Create(new TestResource(), new TestPublisher());
        await controlPlane.StartAsync(CancellationToken.None);

        // Act & Assert
        await Should.ThrowAsync<InvalidOperationException>(() => controlPlane.StartAsync(CancellationToken.None));
    }

    [Fact(DisplayName = DisplayPrefix + "Invocations are refused before start and after stop")]
    public async Task ExecuteAsync_OutsideLifecycle_ShouldThrow()
    {
        // Arrange
        ResourceControlPlane controlPlane = ResourceControlPlane.Create(new TestResource(new TestAction()), new TestPublisher());
        ResourceActionContext context = ResourceActionContext.Create(ResourceActionKind.Write);

        // Act & Assert
        await Should.ThrowAsync<InvalidOperationException>(async () => await controlPlane.ExecuteAsync(_flushCache, context, CancellationToken.None));
        await controlPlane.StartAsync(CancellationToken.None);
        await controlPlane.StopAsync(CancellationToken.None);
        await Should.ThrowAsync<InvalidOperationException>(async () => await controlPlane.ExecuteAsync(_flushCache, context, CancellationToken.None));
    }

    [Fact(DisplayName = DisplayPrefix + "An unknown action is not found")]
    public async Task ExecuteAsync_WithUnknownAction_ShouldReturnNotFound()
    {
        // Arrange
        ResourceControlPlane controlPlane = await StartAsync(new TestResource(new TestAction()));

        // Act
        ResourceActionResult result = await controlPlane.ExecuteAsync("cors", ResourceActionContext.Create(ResourceActionKind.Read), CancellationToken.None);

        // Assert
        result.Status.ShouldBe(ResourceActionStatus.NotFound);
    }

    [Fact(DisplayName = DisplayPrefix + "An operation the action does not accept is not supported, and the action never runs")]
    public async Task ExecuteAsync_WithUnacceptedOperation_ShouldReturnNotSupported()
    {
        // Arrange
        var action = new TestAction();
        ResourceControlPlane controlPlane = await StartAsync(new TestResource(action));

        // Act
        ResourceActionResult result = await controlPlane.ExecuteAsync(_flushCache, ResourceActionContext.Create(ResourceActionKind.Read), CancellationToken.None);

        // Assert
        result.Status.ShouldBe(ResourceActionStatus.NotSupported);
        action.Invocations.ShouldBe(0);
    }

    [Fact(DisplayName = DisplayPrefix + "A stateful write returns the snapshot of the state it left")]
    public async Task ExecuteAsync_WithStatefulWrite_ShouldReturnSnapshot()
    {
        // Arrange
        var action = new LogLevelAction();
        ResourceControlPlane controlPlane = await StartAsync(new TestResource(action));

        // Act
        ResourceActionResult result = await controlPlane.ExecuteAsync(
            _logLevel,
            ResourceActionContext.Create(ResourceActionKind.Write, """{"Level":"Debug"}"""u8.ToArray()),
            CancellationToken.None);

        // Assert
        result.Status.ShouldBe(ResourceActionStatus.Succeeded);
        result.State.ShouldNotBeNull();
        result.State.Action.ShouldBe(_logLevel);
        Encoding.UTF8.GetString(result.State.Value.Span).ShouldBe("""{"Level":"Debug"}""");
        action.Current.Level.ShouldBe("Debug");
    }

    [Fact(DisplayName = DisplayPrefix + "A read and an imperative write return no snapshot")]
    public async Task ExecuteAsync_WithReadOrStatelessWrite_ShouldReturnNoSnapshot()
    {
        // Arrange
        ResourceControlPlane controlPlane = await StartAsync(new TestResource(new LogLevelAction(), new TestAction()));

        // Act
        ResourceActionResult read = await controlPlane.ExecuteAsync(_logLevel, ResourceActionContext.Create(ResourceActionKind.Read), CancellationToken.None);
        ResourceActionResult flush = await controlPlane.ExecuteAsync(_flushCache, ResourceActionContext.Create(ResourceActionKind.Write), CancellationToken.None);

        // Assert
        read.Status.ShouldBe(ResourceActionStatus.Succeeded);
        Encoding.UTF8.GetString(read.Output.Span).ShouldBe("""{"Level":"Information"}""");
        read.State.ShouldBeNull();
        flush.Status.ShouldBe(ResourceActionStatus.Succeeded);
        flush.State.ShouldBeNull();
    }

    [Theory(DisplayName = DisplayPrefix + "Input that does not bind is rejected and changes nothing")]
    [InlineData("not json")]
    [InlineData("""{"Level":5}""")]
    public async Task ExecuteAsync_WithUnboundInput_ShouldReturnRejected(string input)
    {
        // Arrange
        var action = new LogLevelAction();
        ResourceControlPlane controlPlane = await StartAsync(new TestResource(action));

        // Act
        ResourceActionResult result = await controlPlane.ExecuteAsync(
            _logLevel,
            ResourceActionContext.Create(ResourceActionKind.Write, Encoding.UTF8.GetBytes(input)),
            CancellationToken.None);

        // Assert
        result.Status.ShouldBe(ResourceActionStatus.Rejected);
        result.Reason.ShouldNotBeNullOrWhiteSpace();
        result.State.ShouldBeNull();
        action.Applied.ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "An action without state has nothing to restore")]
    public async Task RestoreAsync_WithStatelessAction_ShouldReturnNotSupported()
    {
        // Arrange
        ResourceControlPlane controlPlane = await StartAsync(new TestResource(new TestAction()));

        // Act
        ResourceActionResult result = await controlPlane.RestoreAsync(ResourceStateSnapshot.Create(_flushCache, "{}"u8.ToArray()), CancellationToken.None);

        // Assert
        result.Status.ShouldBe(ResourceActionStatus.NotSupported);
    }

    [Fact(DisplayName = DisplayPrefix + "A write on one instance reaches another through its snapshot")]
    public async Task RestoreAsync_WithSnapshotFromAnotherInstance_ShouldConverge()
    {
        // Arrange: two instances of one resource, as a scaled-out platform runs them.
        var first = new LogLevelAction();
        var second = new LogLevelAction();
        ResourceControlPlane firstInstance = await StartAsync(new TestResource(first));
        ResourceControlPlane secondInstance = await StartAsync(new TestResource(second));
        ResourceActionResult write = await firstInstance.ExecuteAsync(
            _logLevel,
            ResourceActionContext.Create(ResourceActionKind.Write, """{"Level":"Debug"}"""u8.ToArray()),
            CancellationToken.None);

        // Act: the gateway restores the write's snapshot instead of repeating the write.
        ResourceActionResult restore = await secondInstance.RestoreAsync(write.State!, CancellationToken.None);

        // Assert
        restore.Status.ShouldBe(ResourceActionStatus.Succeeded);
        second.Current.ShouldBe(first.Current);
        secondInstance.Resource.Key.ShouldNotBe(firstInstance.Resource.Key);
        secondInstance.Resource.Id.ShouldBe(firstInstance.Resource.Id);
    }

    [Fact(DisplayName = DisplayPrefix + "A snapshot that is not a value of the state's type is rejected")]
    public async Task RestoreAsync_WithInvalidSnapshot_ShouldReturnRejected()
    {
        // Arrange
        var action = new LogLevelAction();
        ResourceControlPlane controlPlane = await StartAsync(new TestResource(action));

        // Act
        ResourceActionResult result = await controlPlane.RestoreAsync(ResourceStateSnapshot.Create(_logLevel, "null"u8.ToArray()), CancellationToken.None);

        // Assert
        result.Status.ShouldBe(ResourceActionStatus.Rejected);
        action.Current.Level.ShouldBe("Information");
    }

    private static async Task<ResourceControlPlane> StartAsync(TestResource resource)
    {
        ResourceControlPlane controlPlane = ResourceControlPlane.Create(resource, new TestPublisher());
        await controlPlane.StartAsync(CancellationToken.None);
        return controlPlane;
    }
}
