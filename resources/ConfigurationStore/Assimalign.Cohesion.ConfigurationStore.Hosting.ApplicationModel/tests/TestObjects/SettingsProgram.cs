using System;
using System.IO;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Hosting.InProcess;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.ConfigurationStore.Hosting.ApplicationModel.Tests;

/// <summary>
/// The <c>settings</c> resource's program, as its <c>Program.cs</c> would be: an ordinary ConfigurationStore
/// application that makes itself a resource and adds an action of its own. Nothing in it knows where
/// it runs. It binds its endpoint to a free loopback port and keeps its data in a temporary
/// directory, which it removes when it stops.
/// </summary>
internal static class SettingsProgram
{
    public const string Name = "settings";

    public static async Task MainAsync(string[] args)
    {
        string data = Path.Combine(Path.GetTempPath(), "cohesion-tests", Guid.NewGuid().ToString("N"));
        try
        {
            ConfigurationStoreApplicationBuilder builder = ConfigurationStoreApplication.CreateBuilder(
                [.. args, "--endpoint", $"http://127.0.0.1:{TestPorts.GetFree()}", "--data", data]);
            builder.Services.AddResource(Name, resource => resource
                .UseInProcess()
                .AddAction(new GreetingAction("hello"), GreetingJsonContext.Default));

            await using ConfigurationStoreApplication application = builder.Build();
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
