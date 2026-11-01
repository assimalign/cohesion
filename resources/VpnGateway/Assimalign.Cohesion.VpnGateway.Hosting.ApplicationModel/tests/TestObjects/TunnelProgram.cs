using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Hosting.InProcess;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.VpnGateway.Hosting.ApplicationModel.Tests;

/// <summary>
/// The <c>tunnel</c> resource's program, as its <c>Program.cs</c> would be: an ordinary VpnGateway
/// application that makes itself a resource and adds an action of its own. Nothing in it knows where
/// it runs.
/// </summary>
internal static class TunnelProgram
{
    public const string Name = "tunnel";

    public static async Task MainAsync(string[] args)
    {
        VpnGatewayApplicationBuilder builder = VpnGatewayApplication.CreateBuilder(args);
        builder.Services.AddResource(Name, resource => resource
            .UseInProcess()
            .AddAction(new GreetingAction("hello"), GreetingJsonContext.Default));

        await using VpnGatewayApplication application = builder.Build();
        await application.RunAsync();
    }
}
