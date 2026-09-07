using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.NotificationHub;

namespace Assimalign.Cohesion.NotificationHub.Hosting.Tests;

public class ApplicationLifecycleTests
{
    [Fact(DisplayName = "Cohesion Test [NotificationHub] - RunAsync: Should stop cleanly when cancellation is requested")]
    public async Task RunAsync_WhenCancellationIsRequested_ShouldStopCleanly()
    {
        // Arrange
        await using INotificationHubApplication application = NotificationHubApplication.CreateBuilder([]).Build();
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        // Act
        await application.RunAsync(cancellationTokenSource.Token);

        // Assert
        application.Context.State.ShouldBe(HostState.Stopped);
    }
}
