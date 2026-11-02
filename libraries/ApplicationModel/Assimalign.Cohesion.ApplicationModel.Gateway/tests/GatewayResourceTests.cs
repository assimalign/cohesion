using System;
using System.Collections.Generic;
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

    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);

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
        resource.Plan(ApplicationGatewayDeploymentChange.Update, Revision, deployment, Lease);

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
        resource.Plan(ApplicationGatewayDeploymentChange.Update, Revision, DeploymentId.New(), Lease);

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
        resource.Plan(ApplicationGatewayDeploymentChange.Update, Revision, deployment, Lease);

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
                resource.Plan(ApplicationGatewayDeploymentChange.Update, Revision, deployment, Lease);
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

    [Fact(DisplayName = DisplayPrefix + "An instance of the planned deployment whose lease lapsed before the plan leaves the gate shut")]
    public void Plan_AfterTheLeaseOfAnInstanceOfItsDeploymentLapsed_ShouldLeaveTheGateShut()
    {
        // Arrange: no lease expires during startup, so the instance stays registered after it stops renewing.
        TimeSpan lease = TimeSpan.FromMilliseconds(10);
        GatewayResource resource = CreateResource();
        DeploymentId deployment = DeploymentId.New();
        ApplicationInstanceManifest instance = Instance(deployment);
        resource.Register(instance);
        resource.TryMarkRegistered(instance);
        Thread.Sleep(lease * 5);

        // Act
        resource.Plan(ApplicationGatewayDeploymentChange.Update, Revision, deployment, lease);

        // Assert
        resource.Registered.IsCompleted.ShouldBeFalse();
        resource.TryClose().ShouldBeTrue();
    }

    [Fact(DisplayName = DisplayPrefix + "An instance of the planned deployment that registered a lease ago but renewed since opens the gate")]
    public void Plan_AfterAnInstanceOfItsDeploymentRenewedWithinItsLease_ShouldOpenTheGate()
    {
        // Arrange: the registration is older than a lease, the renewal is not.
        TimeSpan lease = TimeSpan.FromMilliseconds(250);
        GatewayResource resource = CreateResource();
        DeploymentId deployment = DeploymentId.New();
        ApplicationInstanceManifest instance = Instance(deployment);
        resource.Register(instance);
        resource.TryMarkRegistered(instance);
        Thread.Sleep(lease * 2);
        resource.TryRenew(instance.Key).ShouldBeTrue();

        // Act
        resource.Plan(ApplicationGatewayDeploymentChange.Update, Revision, deployment, lease);

        // Assert
        resource.Registered.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact(DisplayName = DisplayPrefix + "A carried instance of the planned deployment that did not renew is dropped and leaves the gate shut")]
    public void Plan_WithACarriedInstanceOfItsDeploymentThatDidNotRenew_ShouldLeaveTheGateShut()
    {
        // Arrange: the record holds the instance, such as the pod a rollout restart replaced.
        DeploymentId deployment = DeploymentId.New();
        ApplicationInstanceManifest instance = Instance(deployment);
        GatewayResource resource = CreateResource(Record(instance));

        // Act
        resource.Plan(ApplicationGatewayDeploymentChange.Update, Revision, deployment, Lease);

        // Assert
        resource.Registered.IsCompleted.ShouldBeFalse();
        resource.TryClose().ShouldBeTrue();
        resource.Aggregate.Instances.ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "A carried instance of the planned deployment that renewed before the plan stays registered and opens the gate")]
    public void Plan_WithACarriedInstanceOfItsDeploymentThatRenewed_ShouldOpenTheGate()
    {
        // Arrange
        DeploymentId deployment = DeploymentId.New();
        ApplicationInstanceManifest instance = Instance(deployment);
        GatewayResource resource = CreateResource(Record(instance));
        resource.TryRenew(instance.Key).ShouldBeTrue();

        // Act
        resource.Plan(ApplicationGatewayDeploymentChange.Update, Revision, deployment, Lease);

        // Assert
        resource.Registered.IsCompletedSuccessfully.ShouldBeTrue();
        resource.Aggregate.Instances.ShouldHaveSingleItem().Key.ShouldBe(instance.Key);
    }

    private static GatewayResource CreateResource(ApplicationGatewayDeploymentRecord? previous = null)
    {
        ApplicationGatewayDeployment aggregate = ApplicationGatewayDeployment.Create(TestManifests.OrdersApi, previous?.State, null, previous?.Outputs);
        return new GatewayResource(aggregate, TestManifests.OrdersApi, previous);
    }

    // A record entry of the plan's revision, so the plan keeps the definition and drops only the
    // carried instances that did not renew.
    private static ApplicationGatewayDeploymentRecord Record(ApplicationInstanceManifest instance)
    {
        return new ApplicationGatewayDeploymentRecord("Web", "orders-api", Revision, [], [instance], new Dictionary<OutputName, string>());
    }

    private static ApplicationInstanceManifest Instance(DeploymentId deployment)
    {
        return TestManifests.Instance() with { Deployment = deployment };
    }
}
