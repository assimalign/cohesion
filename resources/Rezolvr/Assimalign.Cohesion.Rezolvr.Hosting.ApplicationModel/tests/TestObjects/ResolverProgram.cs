using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Hosting.InProcess;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Rezolvr.Hosting.ApplicationModel.Tests;

/// <summary>
/// The <c>resolver</c> resource's program, as its <c>Program.cs</c> would be: an ordinary Rezolvr
/// application that makes itself a resource and adds an action of its own. Nothing in it knows where
/// it runs.
/// </summary>
internal static class ResolverProgram
{
    public const string Name = "resolver";

    public static async Task MainAsync(string[] args)
    {
        RezolvrApplicationBuilder builder = RezolvrApplication.CreateBuilder(args);
        builder.Services.AddResource(Name, resource => resource
            .UseInProcess()
            .AddAction(new GreetingAction("hello"), GreetingJsonContext.Default));

        await using RezolvrApplication application = builder.Build();
        await application.RunAsync();
    }
}
