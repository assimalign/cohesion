using System;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess.Tests;

public class InProcessInfrastructureBuilderTests
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - In-process infrastructure builder: ";

    [Fact(DisplayName = DisplayPrefix + "A bare UseInProcess runs the program the build bound, and disposing the gateway stops it")]
    public async Task UseInProcess_WithBuildBoundProgram_ShouldRunItAndStopItWithTheGateway()
    {
        // Arrange: the catalog is process-wide, so the resource's name is this test's own.
        var program = new TestProgram("Web", "catalog-bound");
        InProcessProgramCatalog.Register("Web", "catalog-bound", program.MainAsync);
        ApplicationGatewayBuilder builder = CreateBuilder().UseInProcess();
        builder.AddResource("Web", "catalog-bound");
        ApplicationGateway gateway = builder.Build();

        // Act
        await gateway.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
        TestProgramRun run = program.Runs.ShouldHaveSingleItem();
        run.Stopped.IsCompleted.ShouldBeFalse();
        await gateway.DisposeAsync();

        // Assert: the gateway owned the infrastructure it built, so disposing it stopped the program.
        run.Stopped.IsCompleted.ShouldBeTrue();
        gateway.Resources.ShouldHaveSingleItem().Instances.ShouldHaveSingleItem().Key.ShouldBe(run.Key);
    }

    [Fact(DisplayName = DisplayPrefix + "Disposing a gateway leaves the programs of borrowed infrastructure running until the infrastructure is disposed")]
    public async Task UseInProcess_WithBorrowedInfrastructure_ShouldLeaveItsProgramsRunningWhenTheGatewayIsDisposed()
    {
        // Arrange
        var program = new TestProgram("Web", "borrowed-runs");
        await using var infrastructure = new InProcessInfrastructure();
        infrastructure.AddProgram("Web", "borrowed-runs", program.MainAsync);
        ApplicationGatewayBuilder builder = CreateBuilder().UseInProcess(infrastructure);
        builder.AddResource("Web", "borrowed-runs");
        ApplicationGateway gateway = builder.Build();
        await gateway.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
        TestProgramRun run = program.Runs.ShouldHaveSingleItem();

        // Act
        await gateway.DisposeAsync();
        bool runningAfterGateway = !run.Stopped.IsCompleted;
        await infrastructure.DisposeAsync();

        // Assert: the gateway disposes only the platform it owns; the caller's disposal stops the program.
        runningAfterGateway.ShouldBeTrue();
        run.Stopped.IsCompleted.ShouldBeTrue();
    }

    [Fact(DisplayName = DisplayPrefix + "A program bound on the builder takes the place of the one the build bound")]
    public async Task UseInProcess_WithExplicitProgram_ShouldRunItInsteadOfTheBuildBoundOne()
    {
        // Arrange
        var bound = new TestProgram("Web", "explicit-wins");
        var explicitProgram = new TestProgram("Web", "explicit-wins");
        InProcessProgramCatalog.Register("Web", "explicit-wins", bound.MainAsync);
        ApplicationGatewayBuilder builder = CreateBuilder().UseInProcess(inProcess => inProcess.AddProgram("Web", "explicit-wins", explicitProgram.MainAsync));
        builder.AddResource("Web", "explicit-wins");

        // Act
        await using ApplicationGateway gateway = builder.Build();
        await gateway.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));

        // Assert
        explicitProgram.Runs.ShouldHaveSingleItem();
        bound.Runs.ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "The configuration callback runs when the gateway is built, not before")]
    public void UseInProcess_WithConfigure_ShouldRunItWhenTheGatewayIsBuilt()
    {
        // Arrange
        int calls = 0;
        ApplicationGatewayBuilder builder = CreateBuilder().UseInProcess(_ => calls++);

        // Act
        calls.ShouldBe(0);
        ApplicationGateway gateway = builder.Build();

        // Assert
        calls.ShouldBe(1);
        gateway.ShouldNotBeNull();
    }

    [Fact(DisplayName = DisplayPrefix + "The options' stop timeout is the infrastructure's")]
    public async Task Build_WithStopTimeout_ShouldSetItOnTheInfrastructure()
    {
        // Arrange
        var builder = new InProcessInfrastructureBuilder();
        builder.Options.StopTimeout = TimeSpan.FromSeconds(3);

        // Act
        await using InProcessInfrastructure infrastructure = builder.Build();

        // Assert
        infrastructure.StopTimeout.ShouldBe(TimeSpan.FromSeconds(3));
        new InProcessInfrastructureOptions().StopTimeout.ShouldBe(TimeSpan.FromSeconds(30));
        Should.Throw<ArgumentOutOfRangeException>(() => builder.Options.StopTimeout = TimeSpan.Zero);
    }

    [Fact(DisplayName = DisplayPrefix + "Each build makes new infrastructure at its own endpoint")]
    public async Task Build_CalledTwice_ShouldMakeSeparateInfrastructure()
    {
        // Arrange
        var builder = new InProcessInfrastructureBuilder();

        // Act
        await using InProcessInfrastructure first = builder.Build();
        await using InProcessInfrastructure second = builder.Build();

        // Assert
        first.ShouldNotBeSameAs(second);
        first.Endpoint.ShouldNotBe(second.Endpoint);
    }

    [Fact(DisplayName = DisplayPrefix + "A resource is bound to one program on the builder")]
    public void AddProgram_WithBoundResource_ShouldThrow()
    {
        // Arrange
        var builder = new InProcessInfrastructureBuilder().AddProgram("Web", "orders-api", static _ => Task.CompletedTask);

        // Act & Assert
        Should.Throw<ArgumentException>(() => builder.AddProgram("Web", "orders-api", static _ => Task.CompletedTask));
        Should.Throw<ArgumentException>(() => builder.AddProgram(default, "orders-api", static _ => Task.CompletedTask));
        Should.Throw<ArgumentException>(() => builder.AddProgram("Web", default, static _ => Task.CompletedTask));
        Should.Throw<ArgumentNullException>(() => builder.AddProgram("Web", "storefront", null!));
    }

    [Fact(DisplayName = DisplayPrefix + "The build binds a resource to one program")]
    public void Register_WithRegisteredResource_ShouldThrow()
    {
        // Arrange
        InProcessProgramCatalog.Register("Web", "registered-twice", static _ => Task.CompletedTask);

        // Act & Assert
        Should.Throw<ArgumentException>(() => InProcessProgramCatalog.Register("Web", "registered-twice", static _ => Task.CompletedTask));
        Should.Throw<ArgumentException>(() => InProcessProgramCatalog.Register(default, "registered-default", static _ => Task.CompletedTask));
        Should.Throw<ArgumentNullException>(() => InProcessProgramCatalog.Register("Web", "registered-null", null!));
    }

    private static ApplicationGatewayBuilder CreateBuilder()
    {
        ApplicationGatewayBuilder builder = ApplicationGateway.CreateBuilder("local");
        builder.StartupTimeout = TimeSpan.FromSeconds(10);
        return builder;
    }
}
