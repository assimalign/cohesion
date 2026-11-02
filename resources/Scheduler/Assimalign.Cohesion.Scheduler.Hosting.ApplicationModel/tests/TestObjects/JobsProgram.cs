using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Hosting.InProcess;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Scheduler.Hosting.ApplicationModel.Tests;

/// <summary>
/// The <c>jobs</c> resource's program, as its <c>Program.cs</c> would be: an ordinary Scheduler
/// application that makes itself a resource and adds an action of its own. Nothing in it knows where
/// it runs.
/// </summary>
internal static class JobsProgram
{
    public const string Name = "jobs";

    public static async Task MainAsync(string[] args)
    {
        SchedulerApplicationBuilder builder = SchedulerApplication.CreateBuilder(args);
        builder.Services.AddResource(Name, resource => resource
            .UseInProcess()
            .AddAction(new GreetingAction("hello"), GreetingJsonContext.Default));

        await using SchedulerApplication application = builder.Build();
        await application.RunAsync();
    }
}
