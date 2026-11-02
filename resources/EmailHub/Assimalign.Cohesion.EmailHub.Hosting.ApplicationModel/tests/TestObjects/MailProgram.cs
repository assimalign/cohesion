using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Hosting.InProcess;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.EmailHub.Hosting.ApplicationModel.Tests;

/// <summary>
/// The <c>mail</c> resource's program, as its <c>Program.cs</c> would be: an ordinary EmailHub
/// application that makes itself a resource and adds an action of its own. Nothing in it knows where
/// it runs.
/// </summary>
internal static class MailProgram
{
    public const string Name = "mail";

    public static async Task MainAsync(string[] args)
    {
        EmailHubApplicationBuilder builder = EmailHubApplication.CreateBuilder(args);
        builder.Services.AddResource(Name, resource => resource
            .UseInProcess()
            .AddAction(new GreetingAction("hello"), GreetingJsonContext.Default));

        await using EmailHubApplication application = builder.Build();
        await application.RunAsync();
    }
}
