using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Hosting.Tests;

public class ApplicationInstanceServiceTests
{
    private const string DisplayPrefix = "Cohesion Test [Hosting] - ApplicationInstanceService: ";

    private static readonly ActionName _logLevel = "log-level";

    [Fact(DisplayName = DisplayPrefix + "A service needs a control plane and a gateway client")]
    public void Create_WithNullArgument_ShouldThrow()
    {
        // Arrange
        ApplicationInstanceControlPlane controlPlane = ApplicationInstanceControlPlane.Create(new TestResource(), TestSerializer.Json);

        // Act & Assert
        Should.Throw<ArgumentNullException>(() => ApplicationInstanceService.Create(null!, new TestGatewayClient()));
        Should.Throw<ArgumentNullException>(() => ApplicationInstanceService.Create(controlPlane, null!));
    }

    [Fact(DisplayName = DisplayPrefix + "Starting publishes the manifest and restores the state the gateway answers with")]
    public async Task StartingAsync_WithReturnedSnapshot_ShouldRestoreBeforeCompleting()
    {
        // Arrange
        var action = new LogLevelAction();
        var client = new TestGatewayClient(ApplicationActionSnapshot.Create(_logLevel, """{"Level":"Warning"}"""u8.ToArray()));
        ApplicationInstanceService service = Create(new TestResource(action), client);

        // Act
        await service.StartingAsync(CancellationToken.None);

        // Assert
        client.Published.ShouldHaveSingleItem().ShouldBeSameAs(service.ControlPlane.Manifest);
        action.Current.Level.ShouldBe("Warning");
        action.Applied.ShouldHaveSingleItem().Level.ShouldBe("Warning");
    }

    [Fact(DisplayName = DisplayPrefix + "A returned snapshot for an unknown action fails the start and leaves the service stopped")]
    public async Task StartingAsync_WithSnapshotForUnknownAction_ShouldFailAndStayStopped()
    {
        // Arrange
        var client = new TestGatewayClient(ApplicationActionSnapshot.Create("cors", """{"Origins":[]}"""u8.ToArray()));
        ApplicationInstanceService service = Create(new TestResource(new LogLevelAction()), client);

        // Act
        await Should.ThrowAsync<InvalidOperationException>(() => service.StartingAsync(CancellationToken.None));
        await Should.ThrowAsync<InvalidOperationException>(() => service.StartingAsync(CancellationToken.None));

        // Assert: the second start was not refused as a second start; it published again.
        client.Published.Count.ShouldBe(2);
    }

    [Fact(DisplayName = DisplayPrefix + "A started service does not start again")]
    public async Task StartingAsync_Twice_ShouldThrow()
    {
        // Arrange
        var client = new TestGatewayClient();
        ApplicationInstanceService service = Create(new TestResource(), client);
        await service.StartingAsync(CancellationToken.None);

        // Act & Assert
        await Should.ThrowAsync<InvalidOperationException>(() => service.StartingAsync(CancellationToken.None));
        client.Published.ShouldHaveSingleItem();
    }

    [Fact(DisplayName = DisplayPrefix + "A service started again after it stopped publishes again")]
    public async Task StartingAsync_AfterStop_ShouldPublishAgain()
    {
        // Arrange
        var client = new TestGatewayClient();
        ApplicationInstanceService service = Create(new TestResource(), client);
        await service.StartingAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        // Act
        await service.StartingAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        // Assert
        client.Published.Count.ShouldBe(2);
    }

    [Fact(DisplayName = DisplayPrefix + "A publication with a lease is renewed until the service stops")]
    public async Task StartingAsync_WithLease_ShouldRenewUntilStopped()
    {
        // Arrange
        var client = new TestGatewayClient { Lease = TimeSpan.FromMilliseconds(150) };
        ApplicationInstanceService service = Create(new TestResource(), client);

        // Act
        await service.StartingAsync(CancellationToken.None);
        await Eventually.WaitUntilAsync(() => client.Renewals >= 2);
        await service.StopAsync(CancellationToken.None);
        int renewals = client.Renewals;
        await Task.Delay(300);

        // Assert
        client.Renewals.ShouldBe(renewals);
        client.Published.ShouldHaveSingleItem();
    }

    [Fact(DisplayName = DisplayPrefix + "A stop that arrives while a start is publishing ends that start without renewing")]
    public async Task StopAsync_WhileStarting_ShouldEndTheStartWithoutRenewing()
    {
        // Arrange
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new TestGatewayClient { Lease = TimeSpan.FromMilliseconds(150), PublishGate = gate.Task };
        ApplicationInstanceService service = Create(new TestResource(), client);
        Task starting = service.StartingAsync(CancellationToken.None);
        await Eventually.WaitUntilAsync(() => !client.Published.IsEmpty);

        // Act
        await service.StopAsync(CancellationToken.None);
        gate.SetResult();
        await starting;
        await Task.Delay(300);

        // Assert: no renewal ran, and the service can start again.
        client.Renewals.ShouldBe(0);
        await service.StartingAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        client.Published.Count.ShouldBe(2);
    }

    [Fact(DisplayName = DisplayPrefix + "An instance its gateway no longer knows publishes again and restores the answer")]
    public async Task Renewal_WhenGatewayDoesNotKnowTheInstance_ShouldPublishAgainAndRestore()
    {
        // Arrange
        var action = new LogLevelAction();
        var client = new TestGatewayClient(ApplicationActionSnapshot.Create(_logLevel, """{"Level":"Warning"}"""u8.ToArray()))
        {
            Lease = TimeSpan.FromMilliseconds(150),
            Known = false,
        };
        ApplicationInstanceService service = Create(new TestResource(action), client);

        // Act
        await service.StartingAsync(CancellationToken.None);
        await Eventually.WaitUntilAsync(() => client.Published.Count >= 2 && action.Applied.Count >= 2);
        await service.StopAsync(CancellationToken.None);

        // Assert
        action.Current.Level.ShouldBe("Warning");
    }

    [Fact(DisplayName = DisplayPrefix + "An unreachable gateway does not stop the instance: renewals are retried")]
    public async Task Renewal_WhenGatewayIsUnreachable_ShouldKeepServingAndRetry()
    {
        // Arrange
        var client = new TestGatewayClient
        {
            Lease = TimeSpan.FromMilliseconds(150),
            RenewalFailure = new TimeoutException("The gateway did not answer."),
        };
        ApplicationInstanceService service = Create(new TestResource(new LogLevelAction()), client);
        await service.StartingAsync(CancellationToken.None);

        // Act
        await Eventually.WaitUntilAsync(() => client.Renewals >= 3);
        ApplicationActionResult read = await service.ControlPlane.ExecuteAsync(_logLevel, ApplicationActionContext.Create(ApplicationActionKind.Read, TestSerializer.Json), CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        // Assert
        read.Status.ShouldBe(ApplicationActionStatus.Succeeded);
    }

    [Fact(DisplayName = DisplayPrefix + "A lease that is not positive fails the start")]
    public async Task StartingAsync_WithNonPositiveLease_ShouldFailAndStayStopped()
    {
        // Arrange
        var client = new TestGatewayClient { Lease = TimeSpan.Zero };
        ApplicationInstanceService service = Create(new TestResource(new TestAction()), client);

        // Act
        await Should.ThrowAsync<InvalidOperationException>(() => service.StartingAsync(CancellationToken.None));
        await Task.Delay(100);

        // Assert
        client.Renewals.ShouldBe(0);
    }

    [Fact(DisplayName = DisplayPrefix + "Starting opens the control plane to the gateway's calls only once the restored state is in effect")]
    public async Task StartAsync_AfterStarting_ShouldStartTheClientWithTheRestoredState()
    {
        // Arrange
        var action = new LogLevelAction();
        var client = new TestGatewayClient(ApplicationActionSnapshot.Create(_logLevel, """{"Level":"Warning"}"""u8.ToArray()))
        {
            OnStart = _ => action.Current.Level,
        };
        ApplicationInstanceService service = Create(new TestResource(action), client);

        // Act
        await service.StartingAsync(CancellationToken.None);
        IApplicationInstanceControlPlane? beforeStart = client.Serving;
        await service.StartAsync(CancellationToken.None);

        // Assert
        beforeStart.ShouldBeNull();
        client.Serving.ShouldBeSameAs(service.ControlPlane);
        client.ReadAtStart.ShouldBe("Warning");
    }

    [Fact(DisplayName = DisplayPrefix + "Stopping closes the control plane to the gateway's calls")]
    public async Task StopAsync_AfterStart_ShouldStopTheClient()
    {
        // Arrange
        var client = new TestGatewayClient { Lease = TimeSpan.FromMilliseconds(150) };
        ApplicationInstanceService service = Create(new TestResource(new LogLevelAction()), client);
        await service.StartingAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);

        // Act
        await service.StopAsync(CancellationToken.None);

        // Assert
        client.Serving.ShouldBeNull();
    }

    [Fact(DisplayName = DisplayPrefix + "A service whose start ended stopped opens nothing to the gateway's calls")]
    public async Task StartAsync_WithoutPublication_ShouldNotStartTheClient()
    {
        // Arrange
        var client = new TestGatewayClient();
        ApplicationInstanceService service = Create(new TestResource(new LogLevelAction()), client);

        // Act
        await service.StartAsync(CancellationToken.None);

        // Assert
        client.Serving.ShouldBeNull();
        client.Published.ShouldBeEmpty();
    }

    private static ApplicationInstanceService Create(TestResource resource, TestGatewayClient client)
    {
        return ApplicationInstanceService.Create(ApplicationInstanceControlPlane.Create(resource, TestSerializer.Json), client);
    }
}
