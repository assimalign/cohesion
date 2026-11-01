using System;
using System.IO;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Hosting.InProcess;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.IdentityHub.Hosting.ApplicationModel.Tests;

/// <summary>
/// The <c>identity</c> resource's program, as its <c>Program.cs</c> would be: an ordinary IdentityHub
/// application that makes itself a resource and adds an action of its own. Nothing in it knows where
/// it runs. It binds its endpoint to a free loopback port and keeps its data in a temporary
/// directory, which it removes when it stops.
/// </summary>
internal static class IdentityProgram
{
    public const string Name = "identity";

    public static async Task MainAsync(string[] args)
    {
        string data = Path.Combine(Path.GetTempPath(), "cohesion-tests", Guid.NewGuid().ToString("N"));
        try
        {
            IdentityHubApplicationBuilder builder = IdentityHubApplication.CreateBuilder(
                [.. args, "--endpoint", $"http://127.0.0.1:{TestPorts.GetFree()}", "--data", data]);
            builder.Services.AddResource(Name, resource => resource
                .UseInProcess()
                .AddAction(new GreetingAction("hello"), GreetingJsonContext.Default));

            await using IdentityHubApplication application = builder.Build();
            await application.RunAsync();
        }
        finally
        {
            if (Directory.Exists(data))
            {
                Directory.Delete(data, recursive: true);
            }
        }
    }
}
