using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

public class ApplicationResourceDeploymentTests
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - ApplicationResourceDeployment: ";

    [Fact(DisplayName = DisplayPrefix + "An instance of a new deployment restores nothing")]
    public void Register_BeforeAnyWrite_ShouldReturnNoState()
    {
        // Arrange
        ApplicationResourceDeployment resource = ApplicationResourceDeployment.Create(TestManifests.OrdersApi);

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
        ApplicationResourceDeployment resource = ApplicationResourceDeployment.Create(TestManifests.OrdersApi);
        var instance = new ApplicationInstanceManifest("Web", "customers-api", InstanceKey.New(), null, []);

        // Act & Assert
        Should.Throw<ArgumentException>(() => resource.Register(instance));
        resource.Instances.ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "An instance that starts after a write restores the write's snapshot")]
    public void Register_AfterRecord_ShouldReturnRecordedSnapshot()
    {
        // Arrange
        ApplicationResourceDeployment resource = ApplicationResourceDeployment.Create(TestManifests.OrdersApi);
        ApplicationInstanceManifest first = TestManifests.Instance();
        resource.Register(first);
        ApplicationActionSnapshot snapshot = TestManifests.LogLevelSnapshot("Debug");
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
        ApplicationResourceDeployment resource = ApplicationResourceDeployment.Create(TestManifests.OrdersApi, [TestManifests.LogLevelSnapshot("Debug")]);
        ApplicationInstanceManifest stateless = TestManifests.Instance(new ApplicationActionDescriptor(TestManifests.LogLevel, ApplicationActionKind.Read, false));

        // Act
        var restore = resource.Register(stateless);

        // Assert
        restore.ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "Registering an instance again replaces its manifest")]
    public void Register_WithSameKeyTwice_ShouldKeepOneInstance()
    {
        // Arrange
        ApplicationResourceDeployment resource = ApplicationResourceDeployment.Create(TestManifests.OrdersApi);
        ApplicationInstanceManifest instance = TestManifests.Instance();

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
        ApplicationActionSnapshot persisted = TestManifests.LogLevelSnapshot("Warning");
        ApplicationResourceDeployment resource = ApplicationResourceDeployment.Create(TestManifests.OrdersApi, [persisted]);

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
        Should.Throw<ArgumentException>(() => ApplicationResourceDeployment.Create(
            TestManifests.OrdersApi,
            [TestManifests.LogLevelSnapshot("Debug"), TestManifests.LogLevelSnapshot("Warning")]));
    }

    [Fact(DisplayName = DisplayPrefix + "A recorded write names every other instance that must restore it")]
    public void Record_WithRunningInstances_ShouldReturnTheOthers()
    {
        // Arrange
        ApplicationResourceDeployment resource = ApplicationResourceDeployment.Create(TestManifests.OrdersApi);
        ApplicationInstanceManifest source = TestManifests.Instance();
        ApplicationInstanceManifest second = TestManifests.Instance();
        ApplicationInstanceManifest third = TestManifests.Instance();
        ApplicationInstanceManifest stateless = TestManifests.Instance(new ApplicationActionDescriptor(TestManifests.FlushCache, ApplicationActionKind.Write, false));
        foreach (ApplicationInstanceManifest instance in new[] { source, second, third, stateless })
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
        ApplicationResourceDeployment resource = ApplicationResourceDeployment.Create(TestManifests.OrdersApi);
        ApplicationInstanceManifest source = TestManifests.Instance();
        ApplicationInstanceManifest stopped = TestManifests.Instance();
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
        ApplicationResourceDeployment resource = ApplicationResourceDeployment.Create(TestManifests.OrdersApi);
        ApplicationInstanceManifest instance = TestManifests.Instance();
        resource.Register(instance);

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => resource.Record(InstanceKey.New(), TestManifests.LogLevelSnapshot("Debug")));
        Should.Throw<ArgumentException>(() => resource.Record(instance.Key, ApplicationActionSnapshot.Create(TestManifests.FlushCache, "{}"u8.ToArray())));
        resource.State.ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "Two infrastructure actions with one name are refused")]
    public void Create_WithDuplicateActionNames_ShouldThrow()
    {
        // Arrange
        IApplicationResourceAction[] actions = [new TestInfrastructureAction("scale"), new TestInfrastructureAction("scale")];

        // Act & Assert
        Should.Throw<ArgumentException>(() => ApplicationResourceDeployment.Create(TestManifests.OrdersApi, actions: actions));
    }

    [Fact(DisplayName = DisplayPrefix + "Infrastructure actions are dispatched by name and operation")]
    public async Task ExecuteAsync_WithInfrastructureActions_ShouldDispatch()
    {
        // Arrange
        var restart = new TestInfrastructureAction("restart");
        ApplicationResourceDeployment resource = ApplicationResourceDeployment.Create(TestManifests.OrdersApi, actions: [new TestInfrastructureAction("scale"), restart]);

        // Act
        ApplicationActionResult executed = await resource.ExecuteAsync("restart", ApplicationActionContext.Create(ApplicationActionKind.Write, TestSerializer.Json));
        ApplicationActionResult unsupported = await resource.ExecuteAsync("restart", ApplicationActionContext.Create(ApplicationActionKind.Read, TestSerializer.Json));
        ApplicationActionResult missing = await resource.ExecuteAsync("delete", ApplicationActionContext.Create(ApplicationActionKind.Write, TestSerializer.Json));

        // Assert
        resource.Actions.Select(action => action.Name.Value).ShouldBe(["restart", "scale"]);
        executed.Status.ShouldBe(ApplicationActionStatus.Succeeded);
        restart.Invocations.ShouldBe(1);
        unsupported.Status.ShouldBe(ApplicationActionStatus.NotSupported);
        missing.Status.ShouldBe(ApplicationActionStatus.NotFound);
    }

    [Fact(DisplayName = DisplayPrefix + "Instances are ordered by key, and so by start time")]
    public void Instances_AfterRegistrations_ShouldBeOrderedByKey()
    {
        // Arrange
        ApplicationResourceDeployment resource = ApplicationResourceDeployment.Create(TestManifests.OrdersApi);
        ApplicationInstanceManifest[] instances = [TestManifests.Instance(), TestManifests.Instance(), TestManifests.Instance()];

        // Act
        foreach (ApplicationInstanceManifest instance in instances.Reverse())
        {
            resource.Register(instance);
        }

        // Assert
        resource.Instances.Select(instance => instance.Key).ShouldBe(instances.Select(instance => instance.Key).Order());
    }

    [Fact(DisplayName = DisplayPrefix + "Setting outputs reports whether any was added, changed, or removed")]
    public void SetOutputs_ShouldReportChanges()
    {
        // Arrange
        ApplicationResourceDeployment resource = ApplicationResourceDeployment.Create(TestManifests.OrdersApi);

        // Act
        bool added = resource.SetOutputs(Endpoint("http://orders-api.root.svc"));
        bool same = resource.SetOutputs(Endpoint("http://orders-api.root.svc"));
        bool moved = resource.SetOutputs(Endpoint("http://10.0.0.7:8080"));
        string current = resource.Outputs[OutputName.Endpoint];
        bool removed = resource.SetOutputs(new Dictionary<OutputName, string>());

        // Assert
        added.ShouldBeTrue();
        same.ShouldBeFalse();
        moved.ShouldBeTrue();
        current.ShouldBe("http://10.0.0.7:8080");
        removed.ShouldBeTrue();
        resource.Outputs.ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "An output without a value is refused and changes nothing")]
    public void SetOutputs_WithoutValue_ShouldThrow()
    {
        // Arrange
        ApplicationResourceDeployment resource = ApplicationResourceDeployment.Create(TestManifests.OrdersApi, outputs: Endpoint("http://orders-api.root.svc"));

        // Act & Assert
        Should.Throw<ArgumentException>(() => resource.SetOutputs(Endpoint(null!)));
        resource.Outputs[OutputName.Endpoint].ShouldBe("http://orders-api.root.svc");
    }

    [Fact(DisplayName = DisplayPrefix + "Clearing a resource's state drops its snapshots, so a new instance restores none")]
    public void ClearState_ShouldDropRecordedSnapshots()
    {
        // Arrange
        ApplicationResourceDeployment resource = ApplicationResourceDeployment.Create(TestManifests.OrdersApi, [TestManifests.LogLevelSnapshot("Debug")]);

        // Act
        resource.ClearState();

        // Assert
        resource.State.ShouldBeEmpty();
        resource.Register(TestManifests.Instance()).ShouldBeEmpty();
    }

    private static Dictionary<OutputName, string> Endpoint(string value)
    {
        return new Dictionary<OutputName, string> { [OutputName.Endpoint] = value };
    }
}
