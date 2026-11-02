using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Hosting.InProcess;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Database.Hosting.ApplicationModel.Tests;

/// <summary>
/// The <c>orders-db</c> resource's program, as its <c>Program.cs</c> would be: an ordinary Database
/// application that makes itself a resource and adds an action of its own. Nothing in it knows where
/// it runs.
/// </summary>
internal sealed class OrdersProgram
{
    public const string Name = "orders-db";

    private readonly string _path;
    private readonly int _port;

    public OrdersProgram(string path, int port)
    {
        _path = path;
        _port = port;
    }

    public TaskCompletionSource<DatabaseApplication> Created { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Stopped { get; private set; }

    public async Task MainAsync(string[] args)
    {
        DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder(args);
        RunningResource.ConfigureSql(builder, _path, _port);
        builder.Services.AddResource(Name, resource => resource
            .UseInProcess()
            .AddAction(new GreetingAction("hello"), GreetingJsonContext.Default)
            .AddAction(new GreetingAction(name: "second"), GreetingJsonContext.Default));

        await using (DatabaseApplication application = builder.Build())
        {
            Created.SetResult(application);
            await application.RunAsync();
        }
        Stopped = true;
    }
}
