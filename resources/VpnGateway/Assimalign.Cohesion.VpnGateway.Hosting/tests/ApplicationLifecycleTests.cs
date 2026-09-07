using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.VpnGateway;

namespace Assimalign.Cohesion.VpnGateway.Hosting.Tests;

public class ApplicationLifecycleTests
{
    [Fact(DisplayName = "Cohesion Test [VpnGateway] - RunAsync: Should stop cleanly when cancellation is requested")]
    public async Task RunAsync_WhenCancellationIsRequested_ShouldStopCleanly()
    {
        // Arrange
        await using IVpnGatewayApplication application = VpnGatewayApplication.CreateBuilder([]).Build();
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        // Act
        await application.RunAsync(cancellationTokenSource.Token);

        // Assert
        application.Context.State.ShouldBe(HostState.Stopped);
    }
}
