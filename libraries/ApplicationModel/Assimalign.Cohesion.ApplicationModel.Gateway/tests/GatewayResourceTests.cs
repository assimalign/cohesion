using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// The race tests below block a thread per participant for thousands of rounds, which starves the
/// thread pool the lease-timing tests elsewhere run their timers on, so these tests run alone.
/// </summary>
[CollectionDefinition(nameof(GatewayResourceTests), DisableParallelization = true)]
public sealed class GatewayResourceCollection
{
}

/// <summary>
/// One resource's startup gate: which registration or renewal opens it, whichever of the
/// registration, the plan, and the deployment comes first.
/// </summary>
[Collection(nameof(GatewayResourceTests))]
public sealed class GatewayResourceTests
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - GatewayResource: ";

    private const string Revision = "definition:1";

    [Fact(DisplayName = DisplayPrefix + "An instance of the planned deployment registered before the plan does not open the gate at the plan or the deployment")]
    public void Plan_AfterAnInstanceOfItsDeploymentRegistered_ShouldLeaveTheGateShut()
    {
        // Arrange
        GatewayResource resource = CreateResource();
        DeploymentId deployment = DeploymentId.New();
        ApplicationInstanceManifest instance = Instance(deployment);
        resource.Register(instance);
        bool accepted = resource.TryMarkRegistered(instance);

        // Act
        resource.Plan(ApplicationGatewayDeploymentChange.Update, Revision, deployment);
        bool openAfterPlan = resource.Registered.IsCompleted;
        resource.MarkDeployed();

        // Assert: neither shows that the instance survived the deployment.
        accepted.ShouldBeTrue();
        openAfterPlan.ShouldBeFalse();
        resource.Registered.IsCompleted.ShouldBeFalse();
        resource.Aggregate.Instances.ShouldHaveSingleItem().Key.ShouldBe(instance.Key);
    }

    [Fact(DisplayName = DisplayPrefix + "An instance of the planned deployment registered before the plan opens the gate when it renews after the deployment")]
    public void TryRenew_AfterTheDeploymentByAnInstanceOfItsDeploymentRegisteredBeforeThePlan_ShouldOpenTheGate()
    {
        // Arrange: a pod restarted from the same definition while no gateway ran, which the
        // deployment, re-applying an unchanged template, leaves running.
        GatewayResource resource = CreateResource();
        DeploymentId deployment = DeploymentId.New();
        ApplicationInstanceManifest instance = Instance(deployment);
        resource.Register(instance);
        resource.TryMarkRegistered(instance);
        resource.Plan(ApplicationGatewayDeploymentChange.Update, Revision, deployment);
        resource.MarkDeployed();

        // Act
        bool renewed = resource.TryRenew(instance.Key);

        // Assert
        renewed.ShouldBeTrue();
        resource.Registered.IsCompletedSuccessfully.ShouldBeTrue();
        resource.TryClose().ShouldBeFalse();
    }

    [Fact(DisplayName = DisplayPrefix + "An instance of the planned deployment that the deployment removed leaves the gate shut until its replacement publishes")]
    public void TryMarkRegistered_AfterTheDeploymentRemovedAnInstanceOfItsDeployment_ShouldOpenTheGateOnlyForTheReplacement()
    {
        // Arrange: a container in a restart loop publishes before the plan; the deployment removes
        // and recreates it, so it never renews again.
        GatewayResource resource = CreateResource();
        DeploymentId deployment = DeploymentId.New();
        ApplicationInstanceManifest removed = Instance(deployment);
        resource.Register(removed);
        resource.TryMarkRegistered(removed);
        resource.Plan(ApplicationGatewayDeploymentChange.Update, Revision, deployment);
        resource.MarkDeployed();
        bool openBeforeTheReplacement = resource.Registered.IsCompleted;
        ApplicationInstanceManifest replacement = Instance(deployment);

        // Act
        resource.Register(replacement);
        bool accepted = resource.TryMarkRegistered(replacement);

        // Assert
        openBeforeTheReplacement.ShouldBeFalse();
        accepted.ShouldBeTrue();
        resource.Registered.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact(DisplayName = DisplayPrefix + "A renewal between the plan and the deployment's mark does not open the gate")]
    public void TryRenew_BetweenThePlanAndTheDeployment_ShouldLeaveTheGateShut()
    {
        // Arrange
        GatewayResource resource = CreateResource();
        DeploymentId deployment = DeploymentId.New();
        ApplicationInstanceManifest instance = Instance(deployment);
        resource.Register(instance);
        resource.TryMarkRegistered(instance);
        resource.Plan(ApplicationGatewayDeploymentChange.Update, Revision, deployment);

        // Act: the deployment may still remove the instance.
        bool renewed = resource.TryRenew(instance.Key);
        resource.MarkDeployed();

        // Assert
        renewed.ShouldBeTrue();
        resource.Registered.IsCompleted.ShouldBeFalse();
        resource.TryClose().ShouldBeTrue();
    }

    [Fact(DisplayName = DisplayPrefix + "A renewal after the deployment by an instance of another deployment leaves the gate shut")]
    public void TryRenew_AfterTheDeploymentByAnInstanceOfAnotherDeployment_ShouldLeaveTheGateShut()
    {
        // Arrange: a pod of the previous rollout that restarted.
        GatewayResource resource = CreateResource();
        ApplicationInstanceManifest instance = Instance(DeploymentId.New());
        resource.Register(instance);
        resource.TryMarkRegistered(instance);
        resource.Plan(ApplicationGatewayDeploymentChange.Update, Revision, DeploymentId.New());
        resource.MarkDeployed();

        // Act
        bool renewed = resource.TryRenew(instance.Key);

        // Assert
        renewed.ShouldBeTrue();
        resource.Registered.IsCompleted.ShouldBeFalse();
        resource.TryClose().ShouldBeTrue();
        resource.Aggregate.Instances.ShouldHaveSingleItem().Key.ShouldBe(instance.Key);
    }

    [Fact(DisplayName = DisplayPrefix + "A closed window stays closed when an instance of its deployment renews after the deployment")]
    public void TryRenew_AfterTheWindowClosed_ShouldNotReopenIt()
    {
        // Arrange
        GatewayResource resource = CreateResource();
        DeploymentId deployment = DeploymentId.New();
        ApplicationInstanceManifest instance = Instance(deployment);
        resource.Register(instance);
        resource.TryMarkRegistered(instance);
        resource.Plan(ApplicationGatewayDeploymentChange.Update, Revision, deployment);
        resource.MarkDeployed();
        resource.TryClose().ShouldBeTrue();

        // Act
        resource.TryRenew(instance.Key);

        // Assert
        resource.IsClosed.ShouldBeTrue();
        resource.Registered.IsCompleted.ShouldBeFalse();
    }

    [Fact(DisplayName = DisplayPrefix + "A carried instance of the planned deployment that did not renew is dropped and never opens the gate")]
    public void Plan_WithACarriedInstanceOfItsDeploymentThatDidNotRenew_ShouldDropIt()
    {
        // Arrange: the record holds the instance, such as the pod a rollout restart replaced.
        DeploymentId deployment = DeploymentId.New();
        ApplicationInstanceManifest instance = Instance(deployment);
        GatewayResource resource = CreateResource(Record(instance));

        // Act
        resource.Plan(ApplicationGatewayDeploymentChange.Update, Revision, deployment);
        resource.MarkDeployed();
        bool renewed = resource.TryRenew(instance.Key);

        // Assert: the instance is unknown, so it would publish again rather than renew.
        renewed.ShouldBeFalse();
        resource.Registered.IsCompleted.ShouldBeFalse();
        resource.TryClose().ShouldBeTrue();
        resource.Aggregate.Instances.ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "A carried instance of the planned deployment that renewed before the plan stays registered and opens the gate when it renews after the deployment")]
    public void TryRenew_AfterTheDeploymentByACarriedInstanceOfItsDeploymentThatRenewed_ShouldOpenTheGate()
    {
        // Arrange
        DeploymentId deployment = DeploymentId.New();
        ApplicationInstanceManifest instance = Instance(deployment);
        GatewayResource resource = CreateResource(Record(instance));
        resource.TryRenew(instance.Key).ShouldBeTrue();
        resource.Plan(ApplicationGatewayDeploymentChange.Update, Revision, deployment);
        resource.MarkDeployed();
        bool openBeforeTheRenewal = resource.Registered.IsCompleted;

        // Act
        resource.TryRenew(instance.Key).ShouldBeTrue();

        // Assert
        openBeforeTheRenewal.ShouldBeFalse();
        resource.Registered.IsCompletedSuccessfully.ShouldBeTrue();
        resource.Aggregate.Instances.ShouldHaveSingleItem().Key.ShouldBe(instance.Key);
    }

    [Fact(DisplayName = DisplayPrefix + "A registration that races the plan opens the gate by the first renewal after the deployment at the latest")]
    public async Task Plan_RacingARegistrationOfItsDeployment_ShouldOpenTheGateByTheNextRenewal()
    {
        // Arrange
        const int rounds = 2000;
        int shut = 0;

        // Act: each round, one thread publishes as PublishAsync does while another plans; then the
        // deployment returns and the instance renews.
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
            resource.MarkDeployed();
            resource.TryRenew(instance.Key).ShouldBeTrue();
            if (!resource.Registered.IsCompletedSuccessfully)
            {
                shut++;
            }
        }

        // Assert
        shut.ShouldBe(0);
    }

    [Fact(DisplayName = DisplayPrefix + "A renewal that races the deployment's mark and the window's close leaves the gate either open or closed, never both")]
    public async Task TryRenew_RacingTheDeploymentAndTheClose_ShouldDecideTheGateOnce()
    {
        // Arrange
        const int rounds = 2000;
        int inconsistent = 0;
        int lost = 0;

        // Act: each round, an instance that registered before the plan renews while the deployment
        // returns and the window closes; it then renews once more, after both.
        for (int round = 0; round < rounds; round++)
        {
            GatewayResource resource = CreateResource();
            DeploymentId deployment = DeploymentId.New();
            ApplicationInstanceManifest instance = Instance(deployment);
            resource.Register(instance);
            resource.TryMarkRegistered(instance);
            resource.Plan(ApplicationGatewayDeploymentChange.Update, Revision, deployment);
            using var start = new Barrier(3);
            bool closedByTheDeadline = false;
            Task renew = Task.Run(() =>
            {
                start.SignalAndWait();
                resource.TryRenew(instance.Key).ShouldBeTrue();
            });
            Task mark = Task.Run(() =>
            {
                start.SignalAndWait();
                resource.MarkDeployed();
            });
            Task close = Task.Run(() =>
            {
                start.SignalAndWait();
                closedByTheDeadline = resource.TryClose();
            });
            await Task.WhenAll(renew, mark, close).WaitAsync(TimeSpan.FromSeconds(10));
            resource.TryRenew(instance.Key).ShouldBeTrue();

            // The close either won, and the window stays closed, or lost to a renewal after the mark,
            // and the gate is open.
            if (closedByTheDeadline != resource.IsClosed || resource.IsClosed == resource.Registered.IsCompletedSuccessfully)
            {
                inconsistent++;
            }

            if (!closedByTheDeadline && !resource.Registered.IsCompletedSuccessfully)
            {
                lost++;
            }
        }

        // Assert
        inconsistent.ShouldBe(0);
        lost.ShouldBe(0);
    }

    [Fact(DisplayName = DisplayPrefix + "A renewal that races the deployment's mark opens the gate when it comes after it, and the next renewal does otherwise")]
    public async Task TryRenew_RacingTheDeployment_ShouldOpenTheGateByTheNextRenewal()
    {
        // Arrange
        const int rounds = 2000;
        int shut = 0;

        // Act: each round, an instance that registered before the plan renews while the deployment
        // returns, then renews once more.
        for (int round = 0; round < rounds; round++)
        {
            GatewayResource resource = CreateResource();
            DeploymentId deployment = DeploymentId.New();
            ApplicationInstanceManifest instance = Instance(deployment);
            resource.Register(instance);
            resource.TryMarkRegistered(instance);
            resource.Plan(ApplicationGatewayDeploymentChange.Update, Revision, deployment);
            using var start = new Barrier(2);
            Task renew = Task.Run(() =>
            {
                start.SignalAndWait();
                resource.TryRenew(instance.Key).ShouldBeTrue();
            });
            Task mark = Task.Run(() =>
            {
                start.SignalAndWait();
                resource.MarkDeployed();
            });
            await Task.WhenAll(renew, mark).WaitAsync(TimeSpan.FromSeconds(10));
            resource.TryRenew(instance.Key).ShouldBeTrue();
            if (!resource.Registered.IsCompletedSuccessfully)
            {
                shut++;
            }
        }

        // Assert
        shut.ShouldBe(0);
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
