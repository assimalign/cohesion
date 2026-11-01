using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Hosting.InProcess;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.LoadBalancer.Hosting.ApplicationModel.Tests;

/// <summary>
/// The <c>ingress</c> resource's program, as its <c>Program.cs</c> would be: an ordinary LoadBalancer
/// application that makes itself a resource and adds an action of its own. Nothing in it knows where
/// it runs.
/// </summary>
internal static class IngressProgram
{
    public const string Name = "ingress";

    public static async Task MainAsync(string[] args)
    {
        LoadBalancerApplicationBuilder builder = LoadBalancerApplication.CreateBuilder(args);
        builder.Services.AddResource(Name, resource => resource
            .UseInProcess()
            .AddAction(new GreetingAction("hello"), GreetingJsonContext.Default));

        await using LoadBalancerApplication application = builder.Build();
        await application.RunAsync();
    }
}
