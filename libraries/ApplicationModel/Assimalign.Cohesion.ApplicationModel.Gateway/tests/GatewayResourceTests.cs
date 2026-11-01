using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// One resource's startup gate: which registration opens it, whichever of the registration and the
/// plan comes first.
/// </summary>
public sealed class GatewayResourceTests
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - GatewayResource: ";

    private const string Revision = "definition:1";

    [Fact(DisplayName = DisplayPrefix + "An instance of the planned deployment registered before the plan opens the gate when the plan runs")]
    public void Plan_AfterAnInstanceOfItsDeploymentRegistered_ShouldOpenTheGate()
    {
        // Arrange
        GatewayResource resource = CreateResource();
        DeploymentId deployment = DeploymentId.New();
        ApplicationInstanceManifest instance = Instance(deployment);
        resource.Register(instance);
        bool accepted = resource.TryMarkRegistered(instance);
        bool openBeforePlan = resource.Registered.IsCompleted;

        // Act
        resource.Plan(ApplicationGatewayDeploymentChange.Update, Revision, deployment);

        // Assert
        accepted.ShouldBeTrue();
        openBeforePlan.ShouldBeFalse();
        resource.Registered.IsCompletedSuccessfully.ShouldBeTrue();
        resource.TryClose().ShouldBeFalse();
    }

    [Fact(DisplayName = DisplayPrefix + "An instance of another deployment registered before the plan leaves the gate shut")]
    public void Plan_AfterAnInstanceOfAnotherDeploymentRegistered_ShouldLeaveTheGateShut()
    {
        // Arrange
        GatewayResource resource = CreateResource();
        ApplicationInstanceManifest instance = Instance(DeploymentId.New());
        resource.Register(instance);
        resource.TryMarkRegistered(instance);

        // Act
        resource.Plan(ApplicationGatewayDeploymentChange.Update, Revision, DeploymentId.New());

        // Assert
        resource.Registered.IsCompleted.ShouldBeFalse();
        resource.TryClose().ShouldBeTrue();
        resource.Aggregate.Instances.ShouldHaveSingleItem().Key.ShouldBe(instance.Key);
    }

    [Fact(DisplayName = DisplayPrefix + "A closed window stays closed when the plan finds an instance of its deployment")]
    public void Plan_AfterTheWindowClosed_ShouldNotReopenIt()
    {
        // Arrange
        GatewayResource resource = CreateResource();
        DeploymentId deployment = DeploymentId.New();
        ApplicationInstanceManifest instance = Instance(deployment);
        resource.Register(instance);
        resource.TryMarkRegistered(instance);
        resource.TryClose().ShouldBeTrue();

        // Act
        resource.Plan(ApplicationGatewayDeploymentChange.Update, Revision, deployment);

        // Assert
        resource.IsClosed.ShouldBeTrue();
        resource.Registered.IsCompleted.ShouldBeFalse();
    }

    [Fact(DisplayName = DisplayPrefix + "A registration that races the plan always opens the gate when it repeats the plan's deployment")]
    public async Task Plan_RacingARegistrationOfItsDeployment_ShouldAlwaysOpenTheGate()
    {
        // Arrange
        const int rounds = 2000;
        int shut = 0;

        // Act: each round, one thread publishes as PublishAsync does while another plans.
        for (int round = 0; round < rounds; round++)
        {
            GatewayResource resource = CreateResource();
            DeploymentId deployment = DeploymentId.New();
            ApplicationInstanceManifest instance = Instance(deployment);
            using var start = new Barrier(2);
            Task publish = Task.Run(() =>
            {
                start.SignalAndWait();
                resource.Register(instance);
                resource.TryMarkRegistered(instance).ShouldBeTrue();
            });
            Task plan = Task.Run(() =>
            {
                start.SignalAndWait();
                resource.Plan(ApplicationGatewayDeploymentChange.Update, Revision, deployment);
            });
            await Task.WhenAll(publish, plan).WaitAsync(TimeSpan.FromSeconds(10));
            if (!resource.Registered.IsCompletedSuccessfully)
            {
                shut++;
            }
        }

        // Assert
        shut.ShouldBe(0);
    }

    private static GatewayResource CreateResource()
    {
        return new GatewayResource(ApplicationGatewayDeployment.Create(TestManifests.OrdersApi), TestManifests.OrdersApi, null);
    }

    private static ApplicationInstanceManifest Instance(DeploymentId deployment)
    {
        return TestManifests.Instance() with { Deployment = deployment };
    }
}
