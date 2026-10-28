using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Hosting.Resources.Tests;

public class ResourceServiceTests
{
    private const string DisplayPrefix = "Cohesion Test [Hosting] - ResourceService: ";

    private static readonly ResourceActionName _logLevel = "log-level";

    [Fact(DisplayName = DisplayPrefix + "A service needs a control plane and a publisher")]
    public void Create_WithNullArgument_ShouldThrow()
    {
        // Arrange
        ResourceControlPlane controlPlane = ResourceControlPlane.Create(new TestResource(), TestSerializer.Json);

        // Act & Assert
        Should.Throw<ArgumentNullException>(() => ResourceService.Create(null!, new TestPublisher()));
        Should.Throw<ArgumentNullException>(() => ResourceService.Create(controlPlane, null!));
    }

    [Fact(DisplayName = DisplayPrefix + "Starting publishes the manifest and restores the state the publisher returns")]
    public async Task StartingAsync_WithReturnedSnapshot_ShouldRestoreBeforeCompleting()
    {
        // Arrange
        var action = new LogLevelAction();
        var publisher = new TestPublisher(ResourceStateSnapshot.Create(_logLevel, """{"Level":"Warning"}"""u8.ToArray()));
        ResourceService service = Create(new TestResource(action), publisher);

        // Act
        await service.StartingAsync(CancellationToken.None);

        // Assert
        publisher.Published.ShouldHaveSingleItem().ShouldBeSameAs(service.ControlPlane.Manifest);
        action.Current.Level.ShouldBe("Warning");
        action.Applied.ShouldHaveSingleItem().Level.ShouldBe("Warning");
    }

    [Fact(DisplayName = DisplayPrefix + "A returned snapshot for an unknown action fails the start and leaves the service stopped")]
    public async Task StartingAsync_WithSnapshotForUnknownAction_ShouldFailAndStayStopped()
    {
        // Arrange
        var publisher = new TestPublisher(ResourceStateSnapshot.Create("cors", """{"Origins":[]}"""u8.ToArray()));
        ResourceService service = Create(new TestResource(new LogLevelAction()), publisher);

        // Act
        await Should.ThrowAsync<InvalidOperationException>(() => service.StartingAsync(CancellationToken.None));
        await Should.ThrowAsync<InvalidOperationException>(() => service.StartingAsync(CancellationToken.None));

        // Assert: the second start was not refused as a second start; it published again.
        publisher.Published.Count.ShouldBe(2);
    }

    [Fact(DisplayName = DisplayPrefix + "A started service does not start again")]
    public async Task StartingAsync_Twice_ShouldThrow()
    {
        // Arrange
        var publisher = new TestPublisher();
        ResourceService service = Create(new TestResource(), publisher);
        await service.StartingAsync(CancellationToken.None);

        // Act & Assert
        await Should.ThrowAsync<InvalidOperationException>(() => service.StartingAsync(CancellationToken.None));
        publisher.Published.ShouldHaveSingleItem();
    }

    [Fact(DisplayName = DisplayPrefix + "A service started again after it stopped publishes again")]
    public async Task StartingAsync_AfterStop_ShouldPublishAgain()
    {
        // Arrange
        var publisher = new TestPublisher();
        ResourceService service = Create(new TestResource(), publisher);
        await service.StartingAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        // Act
        await service.StartingAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        // Assert
        publisher.Published.Count.ShouldBe(2);
    }

    [Fact(DisplayName = DisplayPrefix + "A publication with a lease is renewed until the service stops")]
    public async Task StartingAsync_WithLease_ShouldRenewUntilStopped()
    {
        // Arrange
        var publisher = new TestPublisher { Lease = TimeSpan.FromMilliseconds(150) };
        ResourceService service = Create(new TestResource(), publisher);

        // Act
        await service.StartingAsync(CancellationToken.None);
        await Eventually.WaitUntilAsync(() => publisher.Renewals >= 2);
        await service.StopAsync(CancellationToken.None);
        int renewals = publisher.Renewals;
        await Task.Delay(300);

        // Assert
        publisher.Renewals.ShouldBe(renewals);
        publisher.Published.ShouldHaveSingleItem();
    }

    [Fact(DisplayName = DisplayPrefix + "A stop that arrives while a start is publishing ends that start without renewing")]
    public async Task StopAsync_WhileStarting_ShouldEndTheStartWithoutRenewing()
    {
        // Arrange
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher = new TestPublisher { Lease = TimeSpan.FromMilliseconds(150), PublishGate = gate.Task };
        ResourceService service = Create(new TestResource(), publisher);
        Task starting = service.StartingAsync(CancellationToken.None);
        await Eventually.WaitUntilAsync(() => !publisher.Published.IsEmpty);

        // Act
        await service.StopAsync(CancellationToken.None);
        gate.SetResult();
        await starting;
        await Task.Delay(300);

        // Assert: no renewal ran, and the service can start again.
        publisher.Renewals.ShouldBe(0);
        await service.StartingAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        publisher.Published.Count.ShouldBe(2);
    }

    [Fact(DisplayName = DisplayPrefix + "An instance its gateway no longer knows publishes again and restores the answer")]
    public async Task Renewal_WhenGatewayDoesNotKnowTheInstance_ShouldPublishAgainAndRestore()
    {
        // Arrange
        var action = new LogLevelAction();
        var publisher = new TestPublisher(ResourceStateSnapshot.Create(_logLevel, """{"Level":"Warning"}"""u8.ToArray()))
        {
            Lease = TimeSpan.FromMilliseconds(150),
            Known = false,
        };
        ResourceService service = Create(new TestResource(action), publisher);

        // Act
        await service.StartingAsync(CancellationToken.None);
        await Eventually.WaitUntilAsync(() => publisher.Published.Count >= 2 && action.Applied.Count >= 2);
        await service.StopAsync(CancellationToken.None);

        // Assert
        action.Current.Level.ShouldBe("Warning");
    }

    [Fact(DisplayName = DisplayPrefix + "An unreachable gateway does not stop the instance: renewals are retried")]
    public async Task Renewal_WhenGatewayIsUnreachable_ShouldKeepServingAndRetry()
    {
        // Arrange
        var publisher = new TestPublisher
        {
            Lease = TimeSpan.FromMilliseconds(150),
            RenewalFailure = new TimeoutException("The gateway did not answer."),
        };
        ResourceService service = Create(new TestResource(new LogLevelAction()), publisher);
        await service.StartingAsync(CancellationToken.None);

        // Act
        await Eventually.WaitUntilAsync(() => publisher.Renewals >= 3);
        ResourceActionResult read = await service.ControlPlane.ExecuteAsync(_logLevel, ResourceActionContext.Create(ResourceActionKind.Read, TestSerializer.Json), CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        // Assert
        read.Status.ShouldBe(ResourceActionStatus.Succeeded);
    }

    [Fact(DisplayName = DisplayPrefix + "A lease that is not positive fails the start")]
    public async Task StartingAsync_WithNonPositiveLease_ShouldFailAndStayStopped()
    {
        // Arrange
        var publisher = new TestPublisher { Lease = TimeSpan.Zero };
        ResourceService service = Create(new TestResource(new TestAction()), publisher);

        // Act
        await Should.ThrowAsync<InvalidOperationException>(() => service.StartingAsync(CancellationToken.None));
        await Task.Delay(100);

        // Assert
        publisher.Renewals.ShouldBe(0);
    }

    private static ResourceService Create(TestResource resource, TestPublisher publisher)
    {
        return ResourceService.Create(ResourceControlPlane.Create(resource, TestSerializer.Json), publisher);
    }
}
