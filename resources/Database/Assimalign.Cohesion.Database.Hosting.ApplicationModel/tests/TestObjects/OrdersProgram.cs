using System.Linq;
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
    private readonly bool _managed;

    public OrdersProgram(string path, int port, bool managed = true)
    {
        _path = path;
        _port = port;
        _managed = managed;
    }

    public TaskCompletionSource<DatabaseApplication> Created { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Stopped { get; private set; }
    public Task Completion { get; private set; } = Task.CompletedTask;
    public int AddedRegistrations { get; private set; }

    public Task MainAsync(string[] args) => Completion = RunAsync(args);

    private async Task RunAsync(string[] args)
    {
        DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder(args);
        RunningResource.ConfigureSql(builder, _path, _port);
        int registrations = builder.Services.Container.Count();
        builder.Services.AddResource(Name, resource =>
        {
            resource.UseInProcess()
                .AddAction(new GreetingAction("hello"), GreetingJsonContext.Default)
                .AddAction(new GreetingAction(name: "second"), GreetingJsonContext.Default);
            if (!_managed) { resource.Platform = null; }
        });
        AddedRegistrations = builder.Services.Container.Count() - registrations;

        await using (DatabaseApplication application = builder.Build())
        {
            Created.SetResult(application);
            await application.RunAsync();
        }
        Stopped = true;
    }
}
