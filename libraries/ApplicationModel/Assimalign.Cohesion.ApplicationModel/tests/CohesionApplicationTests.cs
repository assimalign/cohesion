using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

[Collection(ConsoleOutputCollection.Name)]
public class CohesionApplicationTests
{
    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Run starts and stops the selected gateway")]
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

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Describe emits the model document without gateway contact")]
    public async Task RunAsync_Describe_WritesModelDocumentWithoutContactingGateway()
    {
        var gateway = new FakeGateway();
        IApplicationBuilder builder = Application.CreateBuilder(
                ApplicationName.Parse("appa"),
                ["--mode=describe", "--gateway=fake", "--environment=Development"])
            .UseGateway(gateway);
        builder.AddResource(TestManifestFactory.Create());
        IApplication app = builder.Build();
        TextWriter original = Console.Out;

        try
        {
            using var output = new StringWriter();
            Console.SetOut(output);

            await app.RunAsync();

            gateway.Calls.ShouldBeEmpty();
            using JsonDocument document = JsonDocument.Parse(output.ToString());
            JsonElement root = document.RootElement;
            root.GetProperty("schema").GetString().ShouldBe("cohesion/model/v1");
            root.GetProperty("application").GetString().ShouldBe("appa");
            root.GetProperty("gateway").GetString().ShouldBe("fake");
            root.GetProperty("owner").GetString().ShouldBe("appa@fake");
            root.GetProperty("mode").GetString().ShouldBe("describe");
            JsonElement resource = root.GetProperty("resources")[0];
            resource.GetProperty("name").GetString().ShouldBe("worker");
            resource.GetProperty("manifest").GetProperty("schema").GetString()
                .ShouldBe(ResourceManifest.SchemaV1);
            resource.GetProperty("plan").GetProperty("schema").GetString()
                .ShouldBe(ResourcePlan.CurrentSchema);
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Unimplemented modes refuse before gateway contact")]
    [InlineData(GatewayRunMode.Apply)]
    [InlineData(GatewayRunMode.Teardown)]
    [InlineData(GatewayRunMode.Bootstrap)]
    [InlineData(GatewayRunMode.Render)]
    public async Task RunAsync_UnimplementedMode_ThrowsWithoutContactingGateway(GatewayRunMode mode)
    {
        var gateway = new FakeGateway();
        IApplicationBuilder builder = Application.CreateBuilder(
                ApplicationName.Parse("appa"),
                ["--mode", mode.ToString().ToLowerInvariant()])
            .UseGateway(gateway);
        builder.AddResource(new FakeResource("worker"));
        IApplication app = builder.Build();

        NotSupportedException error = await Should.ThrowAsync<NotSupportedException>(
            () => app.RunAsync());

        error.Message.ShouldContain(mode.ToString());
        error.Message.ShouldContain("No gateway operation was attempted");
        gateway.Calls.ShouldBeEmpty();
    }
}
