using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// A gateway program stops on its process's shutdown signals, or in process on its scope's shutdown,
/// as cancelling its run does.
/// </summary>
public sealed class ApplicationGatewayShutdownTests
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - ApplicationGateway shutdown: ";

    [Fact(DisplayName = DisplayPrefix + "A gateway in a program's scope stops on the scope's shutdown, and what it deployed keeps running")]
    public async Task RunAsync_WhenItsScopeShutsDown_ShouldStop()
    {
        // Arrange
        var infrastructure = new TestInfrastructure();
        IApplicationModelBuilder builder = ApplicationModel.CreateBuilder("root");
        builder.AddResource("Web", "orders-api");
        ApplicationGateway gateway = RunningGateway.Create(infrastructure, new InMemoryApplicationGatewayStore(), builder.Build());
        using var shutdown = new CancellationTokenSource();
        AppEnvironmentScope scope = AppEnvironmentScope.Create(new Dictionary<string, string>(), shutdown.Token);

        Task run;
        Task started;
        using (AppEnvironment.Enter(scope))
        {
            run = gateway.RunAsync(CancellationToken.None, out started);
        }

        await started.WaitAsync(TimeSpan.FromSeconds(10));

        // Act
        await shutdown.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert: the run returned as a cancelled one does, and tore nothing down.
        run.IsCompletedSuccessfully.ShouldBeTrue();
        infrastructure.TornDown.ShouldBeEmpty();
        Should.Throw<ApplicationGatewayException>(() => gateway.PublishAsync(TestManifests.Instance()))
            .ErrorCode.ShouldBe(ApplicationGatewayErrorCode.PublicationsClosed);
    }
}
