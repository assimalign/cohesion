using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Gateway;
using Assimalign.Cohesion.ApplicationModel.Gateway.InProcess;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Database.Hosting.ApplicationModel.Tests;

/// <summary>
/// A Database resource under a gateway that runs it in its own process: the program is an ordinary
/// Database application, which publishes its manifest over the in-process channel and serves the
/// action it added to the gateway's calls.
/// </summary>
public sealed class DatabaseResourceInProcessTests
{
    private const string displayPrefix = "Cohesion Test [Database.Hosting.ApplicationModel] - In process: ";

    [Fact(DisplayName = displayPrefix + "A Database program publishes, serves added actions and stops when its owning gateway is disposed")]
    public async Task ExecuteAsync_WithAddedAction_ShouldWriteThenReadThroughTheGateway()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var directory = new TemporaryDirectory();
        var program = new OrdersProgram(directory.Path, TestPorts.GetFree());
        ApplicationGatewayBuilder builder = ApplicationGateway.CreateBuilder("local").UseInProcess(platform =>
            platform.AddProgram("Database", OrdersProgram.Name, program.MainAsync));
        builder.StartupTimeout = TimeSpan.FromSeconds(30);
        builder.AddResource("Database", OrdersProgram.Name);
        ApplicationGateway gateway = builder.Build();

        try
        {
            await gateway.StartAsync(guard.Token);
            DatabaseApplication application = await program.Created.Task.WaitAsync(guard.Token);
            // Gateway G18 counts publication as ready. Wait for the host's recorded state before
            // calling its control plane, which opens after publication and restore.
            while (application.Context.State != HostState.Started)
            {
                await Task.Delay(10, guard.Token);
            }

            // Act
            ApplicationActionResult write = await gateway.ExecuteAsync("Database", OrdersProgram.Name, "greeting", ApplicationActionContext.Create(
                ApplicationActionOperation.Write,
                JsonApplicationContentSerializer.Create(),
                Encoding.UTF8.GetBytes("""{"text":"welcome"}""")), guard.Token);
            ApplicationActionResult read = await gateway.ExecuteAsync("Database", OrdersProgram.Name, "greeting", ApplicationActionContext.Create(
                ApplicationActionOperation.Read,
                JsonApplicationContentSerializer.Create()), guard.Token);

            // Assert: the instance published, repeating the deployment that started it, and the
            // gateway recorded the write and read it back from the instance.
            IApplicationGatewayDeployment resource = gateway.Resources.Single();
            ApplicationInstanceManifest instance = resource.Instances.ShouldHaveSingleItem();
            instance.Provider.ShouldBe((ProviderName)"Database");
            instance.Name.ShouldBe((ResourceName)"orders-db");
            instance.Deployment.ShouldNotBeNull();
            instance.Actions.ShouldBe(new[]
            {
                new ApplicationActionDescriptor("greeting", ApplicationActionKind.ReadWrite, IsStateful: true),
                new ApplicationActionDescriptor("second", ApplicationActionKind.ReadWrite, IsStateful: true),
            });
            resource.Outputs.ShouldContainKey(OutputName.Endpoint); // StartAsync completed its ready path.

            write.Status.ShouldBe(ApplicationActionStatus.Succeeded);
            resource.State.ShouldHaveSingleItem().Action.ShouldBe((ActionName)"greeting");
            read.Status.ShouldBe(ApplicationActionStatus.Succeeded);
            using JsonDocument output = JsonDocument.Parse(read.Output);
            output.RootElement.GetProperty("text").GetString().ShouldBe("welcome");
        }
        finally
        {
            await gateway.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await gateway.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            // G37/G40 leave workloads running on StopAsync; G42 disposes the owned in-process
            // platform and signals each program's scope shutdown when the gateway is disposed.
            await gateway.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        }

        program.Stopped.ShouldBeTrue();
        (await program.Created.Task).Context.State.ShouldBe(HostState.Stopped);
    }
}
