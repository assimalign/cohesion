using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Gateway;
using Assimalign.Cohesion.ApplicationModel.Gateway.InProcess;

namespace Assimalign.Cohesion.LogSpace.Hosting.ApplicationModel.Tests;

/// <summary>
/// A LogSpace resource under a gateway that runs it in its own process: the program is an ordinary
/// LogSpace application, which publishes its manifest over the in-process channel and serves the
/// action it added to the gateway's calls.
/// </summary>
public sealed class LogSpaceResourceInProcessTests
{
    private const string DisplayPrefix = "Cohesion Test [LogSpace.Hosting.ApplicationModel] - In process: ";

    [Fact(DisplayName = DisplayPrefix + "A LogSpace application runs in the gateway's process and serves the action it added")]
    public async Task ExecuteAsync_WithAddedAction_ShouldWriteThenReadThroughTheGateway()
    {
        // Arrange
        await using var infrastructure = new InProcessInfrastructure();
        infrastructure.AddProgram("LogSpace", LogsProgram.Name, LogsProgram.MainAsync);

        ApplicationGatewayBuilder builder = ApplicationGateway.CreateBuilder("local").UseInProcess(infrastructure);
        builder.StartupTimeout = TimeSpan.FromSeconds(30);
        builder.AddResource("LogSpace", LogsProgram.Name);
        ApplicationGateway gateway = builder.Build();

        try
        {
            await gateway.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));

            // Act
            ApplicationActionResult write = await gateway.ExecuteAsync("LogSpace", LogsProgram.Name, "greeting", ApplicationActionContext.Create(
                ApplicationActionOperation.Write,
                JsonApplicationContentSerializer.Create(),
                Encoding.UTF8.GetBytes("""{"text":"welcome"}""")));
            ApplicationActionResult read = await gateway.ExecuteAsync("LogSpace", LogsProgram.Name, "greeting", ApplicationActionContext.Create(
                ApplicationActionOperation.Read,
                JsonApplicationContentSerializer.Create()));

            // Assert: the instance published, repeating the deployment that started it, and the
            // gateway recorded the write and read it back from the instance.
            IApplicationGatewayDeployment resource = gateway.Resources.Single();
            ApplicationInstanceManifest instance = resource.Instances.ShouldHaveSingleItem();
            instance.Deployment.ShouldNotBeNull();
            instance.Actions.ShouldHaveSingleItem().ShouldBe(new ApplicationActionDescriptor("greeting", ApplicationActionKind.ReadWrite, IsStateful: true));

            write.Status.ShouldBe(ApplicationActionStatus.Succeeded);
            resource.State.ShouldHaveSingleItem().Action.ShouldBe((ActionName)"greeting");
            read.Status.ShouldBe(ApplicationActionStatus.Succeeded);
            using JsonDocument output = JsonDocument.Parse(read.Output);
            output.RootElement.GetProperty("text").GetString().ShouldBe("welcome");
        }
        finally
        {
            await gateway.StopAsync();
            await gateway.Completion;
        }
    }
}
