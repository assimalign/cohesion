using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Hosting.InProcess;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.EventHub.Hosting.ApplicationModel.Tests;

/// <summary>
/// The <c>events</c> resource's program, as its <c>Program.cs</c> would be: an ordinary EventHub
/// application that makes itself a resource and adds an action of its own. Nothing in it knows where
/// it runs.
/// </summary>
internal static class EventsProgram
{
    public const string Name = "events";

    public static async Task MainAsync(string[] args)
    {
        EventHubApplicationBuilder builder = EventHubApplication.CreateBuilder(args);
        builder.Services.AddResource(Name, resource => resource
            .UseInProcess()
            .AddAction(new GreetingAction("hello"), GreetingJsonContext.Default));

        await using EventHubApplication application = builder.Build();
        await application.RunAsync();
    }
}
