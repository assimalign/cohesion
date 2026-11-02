using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Hosting.InProcess;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.NatGateway.Hosting.ApplicationModel.Tests;

/// <summary>
/// The <c>egress</c> resource's program, as its <c>Program.cs</c> would be: an ordinary NatGateway
/// application that makes itself a resource and adds an action of its own. Nothing in it knows where
/// it runs.
/// </summary>
internal static class EgressProgram
{
    public const string Name = "egress";

    public static async Task MainAsync(string[] args)
    {
        NatGatewayApplicationBuilder builder = NatGatewayApplication.CreateBuilder(args);
        builder.Services.AddResource(Name, resource => resource
            .UseInProcess()
            .AddAction(new GreetingAction("hello"), GreetingJsonContext.Default));

        await using NatGatewayApplication application = builder.Build();
        await application.RunAsync();
    }
}
