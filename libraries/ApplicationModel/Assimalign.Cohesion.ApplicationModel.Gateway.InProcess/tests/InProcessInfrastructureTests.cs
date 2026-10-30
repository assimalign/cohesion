using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess.Tests;

public class InProcessInfrastructureTests
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - In-process infrastructure: ";

    private static readonly string _portVariable = AppEnvironment.Variables.Endpoint("http", "PORT");

    [Fact(DisplayName = DisplayPrefix + "A resource's program runs in the gateway's process and its instance registers")]
    public async Task RunAsync_WithProgram_ShouldRunItAndRegisterItsInstance()
    {
        // Arrange
        await using var infrastructure = new InProcessInfrastructure();
        var orders = new TestProgram("Web", "orders-api");
        infrastructure.AddProgram("Web", "orders-api", orders.MainAsync);
        ApplicationBuilder builder = CreateBuilder(infrastructure);
        builder.AddResource("Web", "orders-api");

        // Act
        await using GatewayRun run = await GatewayRun.StartAsync(builder.Build());

        // Assert
        TestProgramRun program = orders.Runs.ShouldHaveSingleItem();
        IApplicationResourceDeployment deployment = run.Gateway.Resources.ShouldHaveSingleItem();
        ApplicationInstanceManifest instance = deployment.Instances.ShouldHaveSingleItem();
        instance.Key.ShouldBe(program.Key);
        instance.Deployment.ShouldBe(DeploymentId.Parse(program.Environment[AppEnvironment.Variables.Deployment]!));
        program.Environment[AppEnvironment.Variables.Application].ShouldBe("local");
        program.Environment[AppEnvironment.Variables.Gateway].ShouldBe("inprocess");
        program.Environment[AppEnvironment.Variables.Resource].ShouldBe("orders-api");
        program.Environment[AppEnvironment.Variables.ManifestEndpoint].ShouldBe(infrastructure.Endpoint.AbsoluteUri);
        program.Environment[AppEnvironment.Variables.ControlPlaneEndpoint].ShouldStartWith("http://127.0.0.1:");
    }

    [Fact(DisplayName = DisplayPrefix + "Each program reads its own environment, and a setting arrives as configuration")]
    public async Task RunAsync_WithSettingFromADependency_ShouldGiveEachProgramItsOwnEnvironment()
    {
        // Arrange
        await using var infrastructure = new InProcessInfrastructure();
        var orders = new TestProgram("Web", "orders-api");
        var storefront = new TestProgram("Web", "storefront", "Orders:Url", "Greeting");
        infrastructure
            .AddProgram("Web", "orders-api", orders.MainAsync)
            .AddProgram("Web", "storefront", storefront.MainAsync);
        ApplicationBuilder builder = CreateBuilder(infrastructure);
        IApplicationResourceDefinition ordersApi = builder.AddResource("Web", "orders-api");
        builder.AddResource("Web", "storefront")
            .WithSetting("Orders:Url", ordersApi.Endpoint)
            .WithSetting("Greeting", "hello");

        // Act
        await using GatewayRun run = await GatewayRun.StartAsync(builder.Build());

        // Assert
        TestProgramRun ordersRun = orders.Runs.ShouldHaveSingleItem();
        TestProgramRun storefrontRun = storefront.Runs.ShouldHaveSingleItem();
        int ordersPort = int.Parse(ordersRun.Environment[_portVariable]!, CultureInfo.InvariantCulture);
        storefrontRun.Environment["Orders:Url"].ShouldBe($"http://127.0.0.1:{ordersPort}");
        storefrontRun.Environment["Greeting"].ShouldBe("hello");
        storefrontRun.Environment[AppEnvironment.Variables.Resource].ShouldBe("storefront");
        storefrontRun.Environment[_portVariable].ShouldNotBe(ordersRun.Environment[_portVariable]);
        run.Gateway.Resources.First().Outputs[OutputName.Endpoint].ShouldBe($"http://127.0.0.1:{ordersPort}");
    }

    [Fact(DisplayName = DisplayPrefix + "Disposing the infrastructure stops every program, the last deployed first")]
    public async Task DisposeAsync_WithPrograms_ShouldStopThemInReverseOrder()
    {
        // Arrange
        var infrastructure = new InProcessInfrastructure();
        var orders = new TestProgram("Web", "orders-api");
        var storefront = new TestProgram("Web", "storefront");
        infrastructure
            .AddProgram("Web", "orders-api", orders.MainAsync)
            .AddProgram("Web", "storefront", storefront.MainAsync);
        ApplicationBuilder builder = CreateBuilder(infrastructure);
        IApplicationResourceDefinition ordersApi = builder.AddResource("Web", "orders-api");
        builder.AddResource("Web", "storefront").DependsOn(ordersApi);
        await using (await GatewayRun.StartAsync(builder.Build()))
        {
        }

        // Act
        await infrastructure.DisposeAsync();

        // Assert
        TestProgramRun ordersRun = orders.Runs.ShouldHaveSingleItem();
        TestProgramRun storefrontRun = storefront.Runs.ShouldHaveSingleItem();
        ordersRun.Stopped.IsCompleted.ShouldBeTrue();
        storefrontRun.Stopped.IsCompleted.ShouldBeTrue();
        storefrontRun.StopOrder.ShouldBeLessThan(ordersRun.StopOrder);
    }

    [Fact(DisplayName = DisplayPrefix + "A program outlives its gateway, and the next gateway over the infrastructure adopts it")]
    public async Task RunAsync_AfterAnEarlierGateway_ShouldAdoptTheRunningProgram()
    {
        // Arrange
        await using var infrastructure = new InProcessInfrastructure();
        var orders = new TestProgram("Web", "orders-api");
        infrastructure.AddProgram("Web", "orders-api", orders.MainAsync);
        var store = new TestStore();
        await using (await GatewayRun.StartAsync(CreateGateway(infrastructure, store)))
        {
        }

        // Act
        await using GatewayRun second = await GatewayRun.StartAsync(CreateGateway(infrastructure, store));

        // Assert
        TestProgramRun program = orders.Runs.ShouldHaveSingleItem();
        program.Stopped.IsCompleted.ShouldBeFalse();
        await Eventually.WaitUntilAsync(() => second.Gateway.Resources.ShouldHaveSingleItem().Instances.Any(instance => instance.Key == program.Key));

        static ApplicationGateway CreateGateway(InProcessInfrastructure infrastructure, TestStore store)
        {
            ApplicationBuilder builder = CreateBuilder(infrastructure);
            builder.LeaseDuration = TimeSpan.FromMilliseconds(300);
            builder.UseStore(store).AddResource("Web", "orders-api");
            return builder.Build();
        }
    }

    [Fact(DisplayName = DisplayPrefix + "A changed setting replaces the running program")]
    public async Task RunAsync_WithChangedSetting_ShouldReplaceTheProgram()
    {
        // Arrange
        await using var infrastructure = new InProcessInfrastructure();
        var orders = new TestProgram("Web", "orders-api", "Greeting");
        infrastructure.AddProgram("Web", "orders-api", orders.MainAsync);
        var store = new TestStore();
        await using (await GatewayRun.StartAsync(CreateGateway(infrastructure, store, "hello")))
        {
        }

        // Act
        await using GatewayRun second = await GatewayRun.StartAsync(CreateGateway(infrastructure, store, "welcome"));

        // Assert
        orders.Runs.Count.ShouldBe(2);
        orders.Runs[0].Stopped.IsCompleted.ShouldBeTrue();
        orders.Runs[1].Environment["Greeting"].ShouldBe("welcome");
        orders.Runs[1].Environment[_portVariable].ShouldBe(orders.Runs[0].Environment[_portVariable]);
        orders.Runs[1].Environment[AppEnvironment.Variables.Deployment].ShouldNotBe(orders.Runs[0].Environment[AppEnvironment.Variables.Deployment]);
        second.Gateway.Resources.ShouldHaveSingleItem().Instances.ShouldHaveSingleItem().Key.ShouldBe(orders.Runs[1].Key);

        static ApplicationGateway CreateGateway(InProcessInfrastructure infrastructure, TestStore store, string greeting)
        {
            ApplicationBuilder builder = CreateBuilder(infrastructure);
            builder.UseStore(store).AddResource("Web", "orders-api").WithSetting("Greeting", greeting);
            return builder.Build();
        }
    }

    [Fact(DisplayName = DisplayPrefix + "A resource the model no longer declares has its program stopped")]
    public async Task RunAsync_WithoutAnEarlierResource_ShouldStopItsProgram()
    {
        // Arrange
        await using var infrastructure = new InProcessInfrastructure();
        var orders = new TestProgram("Web", "orders-api");
        var storefront = new TestProgram("Web", "storefront");
        infrastructure
            .AddProgram("Web", "orders-api", orders.MainAsync)
            .AddProgram("Web", "storefront", storefront.MainAsync);
        var store = new TestStore();
        ApplicationBuilder first = CreateBuilder(infrastructure).UseStore(store);
        first.AddResource("Web", "orders-api");
        first.AddResource("Web", "storefront");
        await using (await GatewayRun.StartAsync(first.Build()))
        {
        }

        ApplicationBuilder second = CreateBuilder(infrastructure).UseStore(store);
        second.AddResource("Web", "orders-api");

        // Act
        await using GatewayRun run = await GatewayRun.StartAsync(second.Build());

        // Assert
        storefront.Runs.ShouldHaveSingleItem().Stopped.IsCompleted.ShouldBeTrue();
        orders.Runs.ShouldHaveSingleItem().Stopped.IsCompleted.ShouldBeFalse();
    }

    [Fact(DisplayName = DisplayPrefix + "A program that throws before it publishes fails the run at once, with its exception")]
    public async Task RunAsync_WhenAProgramThrowsBeforeItPublishes_ShouldFailAtOnceWithItsException()
    {
        // Arrange: the startup window is ten seconds; the program fails its composition at once.
        await using var infrastructure = new InProcessInfrastructure();
        var failure = new InvalidOperationException("The orders database is not configured.");
        var orders = new TestProgram("Web", "orders-api") { Failure = failure };
        infrastructure.AddProgram("Web", "orders-api", orders.MainAsync);
        ApplicationBuilder builder = CreateBuilder(infrastructure);
        builder.AddResource("Web", "orders-api");
        long started = Stopwatch.GetTimestamp();

        // Act
        ApplicationGatewayException exception = await Should.ThrowAsync<ApplicationGatewayException>(() => builder.Build().RunAsync());

        // Assert
        Stopwatch.GetElapsedTime(started).ShouldBeLessThan(TimeSpan.FromSeconds(5));
        exception.ErrorCode.ShouldBe(ApplicationGatewayErrorCode.ResourceFailed);
        exception.Message.ShouldContain("Web/orders-api (InvalidOperationException: The orders database is not configured.)", Case.Sensitive);
        exception.InnerException.ShouldBeSameAs(failure);
    }

    [Fact(DisplayName = DisplayPrefix + "A resource without a program fails the run before anything deploys")]
    public async Task RunAsync_WithoutProgram_ShouldThrow()
    {
        // Arrange
        await using var infrastructure = new InProcessInfrastructure();
        ApplicationBuilder builder = CreateBuilder(infrastructure);
        builder.AddResource("Web", "orders-api");

        // Act & Assert
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(() => builder.Build().RunAsync());
        exception.Message.ShouldContain("AddProgram", Case.Sensitive);
    }

    [Fact(DisplayName = DisplayPrefix + "A publication while no gateway runs is refused")]
    public async Task PublishAsync_WithoutRunningGateway_ShouldThrow()
    {
        // Arrange
        await using var infrastructure = new InProcessInfrastructure();
        ApplicationInstancePublisher publisher = ApplicationInstancePublisher.Create(new ApplicationInstancePublisherOptions { Endpoint = infrastructure.Endpoint });
        ApplicationInstanceManifest manifest = ApplicationInstanceControlPlane.Create(new TestResource("Web", "orders-api", null), JsonApplicationContentSerializer.Create()).Manifest;

        // Act & Assert
        await Should.ThrowAsync<InvalidOperationException>(() => publisher.PublishAsync(manifest).AsTask());
    }

    [Fact(DisplayName = DisplayPrefix + "A resource is bound to one program")]
    public async Task AddProgram_WithBoundResource_ShouldThrow()
    {
        // Arrange
        await using var infrastructure = new InProcessInfrastructure();
        infrastructure.AddProgram("Web", "orders-api", static _ => Task.CompletedTask);

        // Act & Assert
        Should.Throw<ArgumentException>(() => infrastructure.AddProgram("Web", "orders-api", static _ => Task.CompletedTask));
    }

    private static ApplicationBuilder CreateBuilder(InProcessInfrastructure infrastructure)
    {
        ApplicationBuilder builder = ApplicationGateway.CreateBuilder("local");
        builder.StartupTimeout = TimeSpan.FromSeconds(10);
        return builder.UseInProcess(infrastructure);
    }
}
