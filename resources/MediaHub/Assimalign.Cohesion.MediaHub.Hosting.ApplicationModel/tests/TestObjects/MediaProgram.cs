using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Hosting.InProcess;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.MediaHub.Hosting.ApplicationModel.Tests;

/// <summary>
/// The <c>media</c> resource's program, as its <c>Program.cs</c> would be: an ordinary MediaHub
/// application that makes itself a resource and adds an action of its own. Nothing in it knows where
/// it runs.
/// </summary>
internal static class MediaProgram
{
    public const string Name = "media";

    public static async Task MainAsync(string[] args)
    {
        MediaHubApplicationBuilder builder = MediaHubApplication.CreateBuilder(args);
        builder.Services.AddResource(Name, resource => resource
            .UseInProcess()
            .AddAction(new GreetingAction("hello"), GreetingJsonContext.Default));

        await using MediaHubApplication application = builder.Build();
        await application.RunAsync();
    }
}
