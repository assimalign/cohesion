using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

public class CohesionApplicationTests
{
    [Fact]
    public async Task RunAsync_OnCancellation_StartsThenStopsGateway()
    {
        var gateway = new FakeGateway();
        IApplicationBuilder builder = Application.CreateBuilder().UseGateway(gateway);
        builder.AddResource(new FakeResource("a"));
        IApplication app = builder.Build();

        using var cancellation = new CancellationTokenSource();
        Task run = app.RunAsync(cancellation.Token);
        cancellation.Cancel();
        await run;

        gateway.Calls.ShouldBe(new[] { "start", "stop" });
        gateway.StartedModel.ShouldBeSameAs(app.Model);
    }
}
