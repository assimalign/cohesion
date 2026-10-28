using System;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

public class ApplicationResourceTests
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - ApplicationResource: ";

    [Fact(DisplayName = DisplayPrefix + "An instance of a new deployment restores nothing")]
    public void Register_BeforeAnyWrite_ShouldReturnNoState()
    {
        // Arrange
        ApplicationResource resource = ApplicationResource.Create(TestManifests.OrdersApi);

        // Act
        var restore = resource.Register(TestManifests.Instance());

        // Assert
        restore.ShouldBeEmpty();
        resource.Instances.Count.ShouldBe(1);
    }

    [Fact(DisplayName = DisplayPrefix + "An instance of another resource is refused")]
    public void Register_WithInstanceOfAnotherResource_ShouldThrow()
    {
        // Arrange
        ApplicationResource resource = ApplicationResource.Create(TestManifests.OrdersApi);
        var instance = new ResourceManifest("Web", "customers-api", ResourceInstanceKey.New(), []);

        // Act & Assert
        Should.Throw<ArgumentException>(() => resource.Register(instance));
        resource.Instances.ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "An instance that starts after a write restores the write's snapshot")]
    public void Register_AfterRecord_ShouldReturnRecordedSnapshot()
    {
        // Arrange
        ApplicationResource resource = ApplicationResource.Create(TestManifests.OrdersApi);
        ResourceManifest first = TestManifests.Instance();
        resource.Register(first);
        ResourceStateSnapshot snapshot = TestManifests.LogLevelSnapshot("Debug");
        resource.Record(first.Key, snapshot);

        // Act
        var restore = resource.Register(TestManifests.Instance());

        // Assert
        restore.ShouldHaveSingleItem().ShouldBeSameAs(snapshot);
    }

    [Fact(DisplayName = DisplayPrefix + "An instance whose manifest lacks the stateful action receives no snapshot for it")]
    public void Register_WithInstanceWithoutStatefulAction_ShouldFilterSnapshots()
    {
        // Arrange
        ApplicationResource resource = ApplicationResource.Create(TestManifests.OrdersApi, [TestManifests.LogLevelSnapshot("Debug")]);
        ResourceManifest stateless = TestManifests.Instance(new ResourceActionDescriptor(TestManifests.LogLevel, ResourceActionKind.Read, false));

        // Act
        var restore = resource.Register(stateless);

        // Assert
        restore.ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "Registering an instance again replaces its manifest")]
    public void Register_WithSameKeyTwice_ShouldKeepOneInstance()
    {
        // Arrange
        ApplicationResource resource = ApplicationResource.Create(TestManifests.OrdersApi);
        ResourceManifest instance = TestManifests.Instance();

        // Act
        resource.Register(instance);
        resource.Register(instance);

        // Assert
        resource.Instances.ShouldHaveSingleItem().ShouldBeSameAs(instance);
    }

    [Fact(DisplayName = DisplayPrefix + "Persisted state answers the registrations of a restarted gateway")]
    public void Create_WithPersistedState_ShouldAnswerRegistrationWithIt()
    {
        // Arrange
        ResourceStateSnapshot persisted = TestManifests.LogLevelSnapshot("Warning");
        ApplicationResource resource = ApplicationResource.Create(TestManifests.OrdersApi, [persisted]);

        // Act
        var restore = resource.Register(TestManifests.Instance());

        // Assert
        restore.ShouldHaveSingleItem().ShouldBeSameAs(persisted);
        resource.State.ShouldHaveSingleItem().ShouldBeSameAs(persisted);
    }

    [Fact(DisplayName = DisplayPrefix + "Two persisted snapshots for one action are refused")]
    public void Create_WithDuplicateState_ShouldThrow()
    {
        // Arrange, Act & Assert
        Should.Throw<ArgumentException>(() => ApplicationResource.Create(
            TestManifests.OrdersApi,
            [TestManifests.LogLevelSnapshot("Debug"), TestManifests.LogLevelSnapshot("Warning")]));
    }

    [Fact(DisplayName = DisplayPrefix + "A recorded write names every other instance that must restore it")]
    public void Record_WithRunningInstances_ShouldReturnTheOthers()
    {
        // Arrange
        ApplicationResource resource = ApplicationResource.Create(TestManifests.OrdersApi);
        ResourceManifest source = TestManifests.Instance();
        ResourceManifest second = TestManifests.Instance();
        ResourceManifest third = TestManifests.Instance();
        ResourceManifest stateless = TestManifests.Instance(new ResourceActionDescriptor(TestManifests.FlushCache, ResourceActionKind.Write, false));
        foreach (ResourceManifest instance in new[] { source, second, third, stateless })
        {
            resource.Register(instance);
        }

        // Act
        var targets = resource.Record(source.Key, TestManifests.LogLevelSnapshot("Debug"));

        // Assert
        targets.ShouldBe([second.Key, third.Key], ignoreOrder: true);
        resource.State.ShouldHaveSingleItem().Action.ShouldBe(TestManifests.LogLevel);
    }

    [Fact(DisplayName = DisplayPrefix + "A stopped instance no longer receives restores")]
    public void Unregister_WithRegisteredInstance_ShouldRemoveItFromTargets()
    {
        // Arrange
        ApplicationResource resource = ApplicationResource.Create(TestManifests.OrdersApi);
        ResourceManifest source = TestManifests.Instance();
        ResourceManifest stopped = TestManifests.Instance();
        resource.Register(source);
        resource.Register(stopped);

        // Act
        bool removed = resource.Unregister(stopped.Key);
        var targets = resource.Record(source.Key, TestManifests.LogLevelSnapshot("Debug"));

        // Assert
        removed.ShouldBeTrue();
        targets.ShouldBeEmpty();
        resource.Unregister(stopped.Key).ShouldBeFalse();
    }

    [Fact(DisplayName = DisplayPrefix + "A write is recorded only from a registered instance that declares the action stateful")]
    public void Record_WithUnknownSourceOrStatelessAction_ShouldThrow()
    {
        // Arrange
        ApplicationResource resource = ApplicationResource.Create(TestManifests.OrdersApi);
        ResourceManifest instance = TestManifests.Instance();
        resource.Register(instance);

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => resource.Record(ResourceInstanceKey.New(), TestManifests.LogLevelSnapshot("Debug")));
        Should.Throw<ArgumentException>(() => resource.Record(instance.Key, ResourceStateSnapshot.Create(TestManifests.FlushCache, "{}"u8.ToArray())));
        resource.State.ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "Two infrastructure actions with one name are refused")]
    public void Create_WithDuplicateActionNames_ShouldThrow()
    {
        // Arrange
        IApplicationResourceAction[] actions = [new TestInfrastructureAction("scale"), new TestInfrastructureAction("scale")];

        // Act & Assert
        Should.Throw<ArgumentException>(() => ApplicationResource.Create(TestManifests.OrdersApi, actions: actions));
    }

    [Fact(DisplayName = DisplayPrefix + "Infrastructure actions are dispatched by name and operation")]
    public async Task ExecuteAsync_WithInfrastructureActions_ShouldDispatch()
    {
        // Arrange
        var restart = new TestInfrastructureAction("restart");
        ApplicationResource resource = ApplicationResource.Create(TestManifests.OrdersApi, actions: [new TestInfrastructureAction("scale"), restart]);

        // Act
        ResourceActionResult executed = await resource.ExecuteAsync("restart", ResourceActionContext.Create(ResourceActionKind.Write, TestSerializer.Json));
        ResourceActionResult unsupported = await resource.ExecuteAsync("restart", ResourceActionContext.Create(ResourceActionKind.Read, TestSerializer.Json));
        ResourceActionResult missing = await resource.ExecuteAsync("delete", ResourceActionContext.Create(ResourceActionKind.Write, TestSerializer.Json));

        // Assert
        resource.Actions.Select(action => action.Name.Value).ShouldBe(["restart", "scale"]);
        executed.Status.ShouldBe(ResourceActionStatus.Succeeded);
        restart.Invocations.ShouldBe(1);
        unsupported.Status.ShouldBe(ResourceActionStatus.NotSupported);
        missing.Status.ShouldBe(ResourceActionStatus.NotFound);
    }

    [Fact(DisplayName = DisplayPrefix + "Instances are ordered by key, and so by start time")]
    public void Instances_AfterRegistrations_ShouldBeOrderedByKey()
    {
        // Arrange
        ApplicationResource resource = ApplicationResource.Create(TestManifests.OrdersApi);
        ResourceManifest[] instances = [TestManifests.Instance(), TestManifests.Instance(), TestManifests.Instance()];

        // Act
        foreach (ResourceManifest instance in instances.Reverse())
        {
            resource.Register(instance);
        }

        // Assert
        resource.Instances.Select(instance => instance.Key).ShouldBe(instances.Select(instance => instance.Key).Order());
    }
}
