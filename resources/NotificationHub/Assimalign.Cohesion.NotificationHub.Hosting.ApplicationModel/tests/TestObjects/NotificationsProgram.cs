using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Hosting.InProcess;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.NotificationHub.Hosting.ApplicationModel.Tests;

/// <summary>
/// The <c>notifications</c> resource's program, as its <c>Program.cs</c> would be: an ordinary NotificationHub
/// application that makes itself a resource and adds an action of its own. Nothing in it knows where
/// it runs.
/// </summary>
internal static class NotificationsProgram
{
    public const string Name = "notifications";

    public static async Task MainAsync(string[] args)
    {
        NotificationHubApplicationBuilder builder = NotificationHubApplication.CreateBuilder(args);
        builder.Services.AddResource(Name, resource => resource
            .UseInProcess()
            .AddAction(new GreetingAction("hello"), GreetingJsonContext.Default));

        await using NotificationHubApplication application = builder.Build();
        await application.RunAsync();
    }
}
