using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Hosting.InProcess;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.IoTHub.Hosting.ApplicationModel.Tests;

/// <summary>
/// The <c>devices</c> resource's program, as its <c>Program.cs</c> would be: an ordinary IoTHub
/// application that makes itself a resource and adds an action of its own. Nothing in it knows where
/// it runs.
/// </summary>
internal static class DevicesProgram
{
    public const string Name = "devices";

    public static async Task MainAsync(string[] args)
    {
        IoTHubApplicationBuilder builder = IoTHubApplication.CreateBuilder(args);
        builder.Services.AddResource(Name, resource => resource
            .UseInProcess()
            .AddAction(new GreetingAction("hello"), GreetingJsonContext.Default));

        await using IoTHubApplication application = builder.Build();
        await application.RunAsync();
    }
}
