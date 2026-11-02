using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Hosting.InProcess;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.ApiManager.Hosting.ApplicationModel.Tests;

/// <summary>
/// The <c>apis</c> resource's program, as its <c>Program.cs</c> would be: an ordinary ApiManager
/// application that makes itself a resource and adds an action of its own. Nothing in it knows where
/// it runs.
/// </summary>
internal static class ApisProgram
{
    public const string Name = "apis";

    public static async Task MainAsync(string[] args)
    {
        ApiManagerApplicationBuilder builder = ApiManagerApplication.CreateBuilder(args);
        builder.Services.AddResource(Name, resource => resource
            .UseInProcess()
            .AddAction(new GreetingAction("hello"), GreetingJsonContext.Default));

        await using ApiManagerApplication application = builder.Build();
        await application.RunAsync();
    }
}
