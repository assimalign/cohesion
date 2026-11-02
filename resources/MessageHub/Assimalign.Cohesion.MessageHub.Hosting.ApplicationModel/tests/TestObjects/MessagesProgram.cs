using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Hosting.InProcess;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.MessageHub.Hosting.ApplicationModel.Tests;

/// <summary>
/// The <c>messages</c> resource's program, as its <c>Program.cs</c> would be: an ordinary MessageHub
/// application that makes itself a resource and adds an action of its own. Nothing in it knows where
/// it runs.
/// </summary>
internal static class MessagesProgram
{
    public const string Name = "messages";

    public static async Task MainAsync(string[] args)
    {
        MessageHubApplicationBuilder builder = MessageHubApplication.CreateBuilder(args);
        builder.Services.AddResource(Name, resource => resource
            .UseInProcess()
            .AddAction(new GreetingAction("hello"), GreetingJsonContext.Default));

        await using MessageHubApplication application = builder.Build();
        await application.RunAsync();
    }
}
