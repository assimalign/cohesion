using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Gateway;
using Assimalign.Cohesion.ApplicationModel.Gateway.InProcess;

namespace Assimalign.Cohesion.Web.Hosting.ApplicationModel.Tests;

/// <summary>
/// Web resources under a gateway that runs them in its own process: each program is an ordinary
/// Web application, and the gateway deploys them in dependency order, hands each its own port and
/// settings, and receives their manifests over the in-process channel.
/// </summary>
public sealed class WebResourceInProcessTests
{
    private const string DisplayPrefix = "Cohesion Test [Web.Hosting.ApplicationModel] - In process: ";

    [Fact(DisplayName = DisplayPrefix + "Two Web applications run in the gateway's process, one calling the other at the address its setting received")]
    public async Task RunAsync_WithWebPrograms_ShouldServeThemInTheGatewaysProcess()
    {
        // Arrange
        await using var infrastructure = new InProcessInfrastructure();
        infrastructure
            .AddProgram("Web", "orders-api", OrdersProgram.MainAsync)
            .AddProgram("Web", "storefront", StorefrontProgram.MainAsync);

        ApplicationBuilder builder = ApplicationGateway.CreateBuilder("local").UseInProcess(infrastructure);
        builder.StartupTimeout = TimeSpan.FromSeconds(30);
        IApplicationResourceDefinition orders = builder.AddResource("Web", "orders-api");
        builder.AddResource("Web", "storefront").WithSetting("Orders:Url", orders.Endpoint);
        ApplicationGateway gateway = builder.Build();
        using var stop = new CancellationTokenSource();

        // Act
        Task run = gateway.RunAsync(stop.Token);
        try
        {
            await gateway.Started.WaitAsync(TimeSpan.FromSeconds(30));
            string storefront = gateway.Resources.Single(resource => resource.Name == "storefront").Outputs[OutputName.Endpoint];
            string answer = await GetAsync(new Uri(storefront));

            // Assert
            answer.ShouldBe("storefront -> orders");
            gateway.Resources.ShouldAllBe(resource => resource.Instances.Count == 1);

            // Each Web application repeats the deployment that started it, which is what made it ready.
            gateway.Resources.ShouldAllBe(resource => resource.Instances.Single().Deployment != null);
        }
        finally
        {
            await stop.CancelAsync();
            await run;
        }
    }

    // An instance registers before its servers start, so the first request can arrive before the
    // application listens.
    private static async Task<string> GetAsync(Uri address)
    {
        using var client = new HttpClient { BaseAddress = address };
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await client.GetStringAsync("/");
            }
            catch (HttpRequestException) when (attempt < 100)
            {
                await Task.Delay(50);
            }
        }
    }
}
