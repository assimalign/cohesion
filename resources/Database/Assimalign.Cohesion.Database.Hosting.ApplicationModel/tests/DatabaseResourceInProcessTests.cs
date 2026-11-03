using System;
using System.Linq;
using System.Net;
using System.Net.Http;
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
            while (application.Context.State is not (HostState.Started or HostState.Failed))
            {
                await Task.Delay(10, guard.Token);
            }
            if (application.Context.State == HostState.Failed)
            {
                // Surface the gateway's failure, or its program's startup failure, instead of
                // timing out while polling a terminal state.
                if (gateway.Completion.IsFaulted)
                {
                    await gateway.Completion.WaitAsync(guard.Token);
                }
                await program.Completion.WaitAsync(guard.Token);
            }
            application.Context.State.ShouldBe(HostState.Started);

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
            // Pins today's documented gap (open question 1): this is the platform's HTTP
            // endpoint, not Database's wire address. Update deliberately when named endpoints land.
            resource.Outputs.ShouldContainKey(OutputName.Endpoint);
            using var healthClient = new HttpClient { BaseAddress = new Uri(resource.Outputs[OutputName.Endpoint]) };
            foreach (string path in new[] { "/cohesion/v1/readyz", "/cohesion/v1/livez" })
            {
                using HttpResponseMessage response = await healthClient.GetAsync(path, guard.Token);
                response.StatusCode.ShouldBe(HttpStatusCode.OK);
            }

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
            // G37/G40 leave the Database application Started and accepting connections after
            // StopAsync and Completion. G42 DisposeAsync stops the run, then disposes the owned
            // platform; platform disposal owns workload shutdown, so it is our shutdown criterion.
            await gateway.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        }

        program.Stopped.ShouldBeTrue();
        (await program.Created.Task).Context.State.ShouldBe(HostState.Stopped);
    }
}
