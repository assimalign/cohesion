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
        gateway.StopTokenCanBeCanceled.ShouldBe(false);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Run cancellation during startup stops gracefully")]
    public async Task RunAsync_CanceledWhileGatewayStartIsBlocked_StopsAndCompletes()
    {
        // Arrange
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new FakeGateway
        {
            StartBehavior = async cancellationToken =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
        };
        IApplicationBuilder builder = Application.CreateBuilder().UseGateway(gateway);
        builder.AddResource(new FakeResource("a"));
        IApplication app = builder.Build();
        using var cancellation = new CancellationTokenSource();
        Task run = app.RunAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Act
        cancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        // Assert
        gateway.Calls.ShouldBe(new[] { "start", "stop" });
        gateway.StopTokenCanBeCanceled.ShouldBe(false);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Describe emits the model document without gateway contact")]
    public async Task RunAsync_Describe_WritesModelDocumentWithoutContactingGateway()
    {
        var gateway = new FakeGateway();
        IApplicationBuilder builder = Application.CreateBuilder(
                ApplicationName.Parse("appa"),
                ["--mode=describe", "--gateway=fake", "--environment=Development", "--restart-orphans"])
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
            root.GetProperty("restartOrphans").GetBoolean().ShouldBeTrue();
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

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Apply and Teardown dispatch to their gateway operations")]
    [InlineData(GatewayRunMode.Apply, "reconcile")]
    [InlineData(GatewayRunMode.Teardown, "uninstall")]
    public async Task RunAsync_LifecycleMode_DispatchesToGateway(GatewayRunMode mode, string expectedCall)
    {
        // Arrange
        var gateway = new FakeGateway();
        IApplicationBuilder builder = Application.CreateBuilder(
                ApplicationName.Parse("appa"),
                ["--mode", mode.ToString().ToLowerInvariant()])
            .UseGateway(gateway);
        builder.AddResource(new FakeResource("worker"));
        IApplication app = builder.Build();

        // Act
        await app.RunAsync();

        // Assert
        gateway.Calls.ShouldBe(new[] { expectedCall });
        gateway.StartedModel.ShouldBeSameAs(app.Model);
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Trust issue arguments build and dispatch a validated gateway command")]
    [InlineData("--mode", "trust-issue", "--developer", "developer-a")]
    [InlineData("--mode=trust-issue", "--developer=developer-a")]
    public async Task RunAsync_TrustIssueArguments_DispatchesValidatedCommand(params string[] args)
    {
        // Arrange
        var gateway = new FakeCommandGateway();
        IApplicationBuilder builder = Application.CreateBuilder(ApplicationName.Parse("appa"), args)
            .UseGateway(gateway);
        builder.AddResource(new FakeResource("worker"));
        builder.RunMode.ShouldBe(GatewayRunMode.TrustIssue);
        IApplication app = builder.Build();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(1));

        // Act
        await app.RunAsync(cancellation.Token);

        // Assert
        app.Model.RunMode.ShouldBe(GatewayRunMode.TrustIssue);
        gateway.Calls.ShouldBe(new[] { "command" });
        gateway.ExecutedModel.ShouldBeSameAs(app.Model);
        GatewayCommand command = gateway.ExecutedCommand.ShouldNotBeNull();
        command.Mode.ShouldBe(GatewayRunMode.TrustIssue);
        command.DeveloperName.ShouldBe("developer-a");
        command.PeerName.ShouldBeNull();
        command.ExportSource.ShouldBeNull();
        gateway.ExecutedCancellationToken.ShouldBe(cancellation.Token);
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Trust add arguments build and dispatch a validated gateway command")]
    [InlineData("--mode", "trust-add", "--peer", "peer-a", "--from", "exports/peer-a/export.json")]
    [InlineData("--mode=trust-add", "--peer=peer-a", "--from=exports/peer-a/export.json")]
    public async Task RunAsync_TrustAddArguments_DispatchesValidatedCommand(params string[] args)
    {
        // Arrange
        var gateway = new FakeCommandGateway();
        IApplicationBuilder builder = Application.CreateBuilder(ApplicationName.Parse("appa"), args)
            .UseGateway(gateway);
        builder.AddResource(new FakeResource("worker"));
        builder.RunMode.ShouldBe(GatewayRunMode.TrustAdd);
        IApplication app = builder.Build();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(1));

        // Act
        await app.RunAsync(cancellation.Token);

        // Assert
        app.Model.RunMode.ShouldBe(GatewayRunMode.TrustAdd);
        gateway.Calls.ShouldBe(new[] { "command" });
        gateway.ExecutedModel.ShouldBeSameAs(app.Model);
        GatewayCommand command = gateway.ExecutedCommand.ShouldNotBeNull();
        command.Mode.ShouldBe(GatewayRunMode.TrustAdd);
        command.DeveloperName.ShouldBeNull();
        command.PeerName.ShouldBe("peer-a");
        command.ExportSource.ShouldBe("exports/peer-a/export.json");
        gateway.ExecutedCancellationToken.ShouldBe(cancellation.Token);
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Trust commands name gateways without command support")]
    [InlineData("--mode=trust-issue", "--developer=developer-a")]
    [InlineData("--mode=trust-add", "--peer=peer-a", "--from=exports/peer-a/export.json")]
    public async Task RunAsync_TrustCommandWithoutHandler_ThrowsNamedNotSupported(params string[] args)
    {
        // Arrange
        var gateway = new FakeGateway("lifecycle-only");
        IApplicationBuilder builder = Application.CreateBuilder(ApplicationName.Parse("appa"), args)
            .UseGateway(gateway);
        builder.AddResource(new FakeResource("worker"));
        IApplication app = builder.Build();

        // Act
        NotSupportedException error = await Should.ThrowAsync<NotSupportedException>(
            () => app.RunAsync(CancellationToken.None));

        // Assert
        error.Message.ShouldContain("lifecycle-only", Case.Sensitive);
        error.Message.ShouldContain(app.Model.RunMode.ToString(), Case.Sensitive);
        gateway.Calls.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Unimplemented modes refuse before gateway contact")]
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
