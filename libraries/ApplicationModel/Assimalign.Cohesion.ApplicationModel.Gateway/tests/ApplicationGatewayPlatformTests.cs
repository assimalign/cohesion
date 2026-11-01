using System;
using System.Text;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// A platform the builder creates with <see cref="ApplicationGatewayBuilder.UsePlatform{TPlatform}"/>:
/// it is created at <see cref="ApplicationGatewayBuilder.Build"/>, fills both of a platform's roles,
/// and the gateway owns it; what <see cref="ApplicationGatewayBuilder.UseInfrastructure"/> and
/// <see cref="ApplicationGatewayBuilder.UseInstanceClient"/> receive is borrowed.
/// </summary>
public sealed class ApplicationGatewayPlatformTests
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - Gateway platform: ";

    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = DisplayPrefix + "The factory runs once, when the gateway is built")]
    public void UsePlatform_UntilBuild_ShouldNotCreateThePlatform()
    {
        // Arrange
        int created = 0;
        ApplicationGatewayBuilder builder = CreateBuilder().UsePlatform(() =>
        {
            created++;
            return new TestPlatform();
        });
        int createdBeforeBuild = created;

        // Act
        builder.Build();

        // Assert
        createdBeforeBuild.ShouldBe(0);
        created.ShouldBe(1);
    }

    [Fact(DisplayName = DisplayPrefix + "A build that fails its checks creates no platform")]
    public void Build_WhenChecksFail_ShouldNotCreateThePlatform()
    {
        // Arrange
        int created = 0;
        ApplicationGatewayBuilder builder = ApplicationGateway.CreateBuilder("east").UsePlatform(() =>
        {
            created++;
            return new TestPlatform();
        });
        ((IApplicationModelBuilder)builder).UseParent("root");

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => builder.Build()).Message.ShouldContain("call UseParent", Case.Sensitive);
        created.ShouldBe(0);
    }

    [Fact(DisplayName = DisplayPrefix + "A platform that names no platform is disposed before the build throws")]
    public void Build_WithUnnamedPlatform_ShouldDisposeItAndThrow()
    {
        // Arrange
        var platform = new TestPlatform { Platform = default };
        ApplicationGatewayBuilder builder = CreateBuilder().UsePlatform(() => platform);

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => builder.Build()).Message.ShouldContain("names no platform", Case.Sensitive);
        platform.Disposals.ShouldBe(1);
        platform.SynchronousDisposals.ShouldBe(1);
    }

    [Fact(DisplayName = DisplayPrefix + "A factory that returns null fails the build")]
    public void Build_WithFactoryReturningNull_ShouldThrow()
    {
        // Arrange
        ApplicationGatewayBuilder builder = CreateBuilder().UsePlatform<TestPlatform>(() => null!);

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => builder.Build()).Message.ShouldContain("returned null", Case.Sensitive);
    }

    [Fact(DisplayName = DisplayPrefix + "A factory is required, and a built gateway takes none")]
    public void UsePlatform_WithNullOrAfterBuild_ShouldThrow()
    {
        // Arrange
        ApplicationGatewayBuilder builder = CreateBuilder();

        // Act & Assert
        Should.Throw<ArgumentNullException>(() => builder.UsePlatform<TestPlatform>(null!));
        builder.UsePlatform(() => new TestPlatform()).Build();
        Should.Throw<InvalidOperationException>(() => builder.UsePlatform(() => new TestPlatform()));
    }

    [Fact(DisplayName = DisplayPrefix + "The platform is both the infrastructure and the instance client")]
    public async Task UsePlatform_WhenRun_ShouldDeployAndCallInstancesThroughThePlatform()
    {
        // Arrange
        var platform = new TestPlatform();
        await using ApplicationGateway gateway = CreateBuilder().UsePlatform(() => platform).Build();

        // Act
        await gateway.StartAsync().WaitAsync(_timeout);
        ApplicationActionResult result = await gateway.ExecuteAsync("Web", "orders-api", TestManifests.LogLevel, Write("Debug"));

        // Assert
        result.Status.ShouldBe(ApplicationActionStatus.Succeeded);
        platform.Infrastructure.Starts.ShouldBe(1);
        platform.Infrastructure.Deployed.ShouldHaveSingleItem();
        platform.Client.Calls.ShouldHaveSingleItem().ShouldStartWith("execute:");
    }

    [Fact(DisplayName = DisplayPrefix + "Disposing stops the run and then disposes the owned platform, asynchronously")]
    public async Task DisposeAsync_AfterStart_ShouldStopThenDisposeThePlatform()
    {
        // Arrange
        var platform = new TestPlatform();
        ApplicationGateway gateway = CreateBuilder().UsePlatform(() => platform).Build();
        await gateway.StartAsync().WaitAsync(_timeout);

        // Act
        await gateway.DisposeAsync().AsTask().WaitAsync(_timeout);

        // Assert
        gateway.Completion.IsCompletedSuccessfully.ShouldBeTrue();
        platform.Infrastructure.Stops.ShouldBe(1);
        platform.StopsAtDisposal.ShouldBe(1);
        platform.Disposals.ShouldBe(1);
        platform.SynchronousDisposals.ShouldBe(0);
        platform.Infrastructure.TornDown.ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "A platform that disposes only synchronously is disposed that way")]
    public async Task DisposeAsync_WithSynchronousPlatform_ShouldDisposeIt()
    {
        // Arrange
        var platform = new TestSynchronousPlatform();
        ApplicationGateway gateway = CreateBuilder().UsePlatform(() => platform).Build();
        await gateway.StartAsync().WaitAsync(_timeout);

        // Act
        await gateway.DisposeAsync().AsTask().WaitAsync(_timeout);

        // Assert
        platform.Disposals.ShouldBe(1);
        platform.StopsAtDisposal.ShouldBe(1);
    }

    [Fact(DisplayName = DisplayPrefix + "Disposing again, or at the same time, disposes the platform once")]
    public async Task DisposeAsync_Repeatedly_ShouldDisposeThePlatformOnce()
    {
        // Arrange
        var platform = new TestPlatform();
        ApplicationGateway gateway = CreateBuilder().UsePlatform(() => platform).Build();
        await gateway.StartAsync().WaitAsync(_timeout);

        // Act
        Task first = gateway.DisposeAsync().AsTask();
        Task second = gateway.DisposeAsync().AsTask();
        await Task.WhenAll(first, second).WaitAsync(_timeout);
        await gateway.DisposeAsync().AsTask().WaitAsync(_timeout);

        // Assert
        platform.Disposals.ShouldBe(1);
        platform.Infrastructure.Stops.ShouldBe(1);
    }

    [Fact(DisplayName = DisplayPrefix + "Disposing a gateway that never started disposes its platform, and the gateway never runs")]
    public async Task DisposeAsync_BeforeStart_ShouldDisposeThePlatformAndEndTheGateway()
    {
        // Arrange
        var platform = new TestPlatform();
        ApplicationGateway gateway = CreateBuilder().UsePlatform(() => platform).Build();

        // Act
        await gateway.DisposeAsync().AsTask().WaitAsync(_timeout);

        // Assert
        platform.Disposals.ShouldBe(1);
        platform.Infrastructure.Starts.ShouldBe(0);
        platform.Infrastructure.Stops.ShouldBe(0);
        gateway.Completion.IsCompletedSuccessfully.ShouldBeTrue();
        Should.Throw<ObjectDisposedException>(() => gateway.StartAsync());
        await gateway.StopAsync().WaitAsync(_timeout);
    }

    [Fact(DisplayName = DisplayPrefix + "A borrowed infrastructure and client are stopped but never disposed")]
    public async Task DisposeAsync_WithBorrowedInfrastructure_ShouldNotDisposeIt()
    {
        // Arrange
        var platform = new TestPlatform();
        ApplicationGateway gateway = CreateBuilder().UseInfrastructure(platform).UseInstanceClient(platform).Build();
        await gateway.StartAsync().WaitAsync(_timeout);

        // Act
        await gateway.DisposeAsync().AsTask().WaitAsync(_timeout);

        // Assert
        platform.Infrastructure.Stops.ShouldBe(1);
        platform.Disposals.ShouldBe(0);
    }

    [Fact(DisplayName = DisplayPrefix + "A client set after the platform replaces it as the client only")]
    public async Task UseInstanceClient_AfterUsePlatform_ShouldKeepThePlatformAsTheInfrastructure()
    {
        // Arrange
        var platform = new TestPlatform();
        var borrowed = new TestInstanceClient(platform.Infrastructure);
        ApplicationGateway gateway = CreateBuilder().UsePlatform(() => platform).UseInstanceClient(borrowed).Build();
        await gateway.StartAsync().WaitAsync(_timeout);

        // Act
        await gateway.ExecuteAsync("Web", "orders-api", TestManifests.LogLevel, Write("Debug"));
        await gateway.DisposeAsync().AsTask().WaitAsync(_timeout);

        // Assert
        platform.Infrastructure.Starts.ShouldBe(1);
        borrowed.Calls.ShouldHaveSingleItem().ShouldStartWith("execute:");
        platform.Client.Calls.ShouldBeEmpty();
        platform.Disposals.ShouldBe(1);
    }

    [Fact(DisplayName = DisplayPrefix + "A platform both of whose roles are replaced is never created")]
    public async Task UseInfrastructureAndClient_AfterUsePlatform_ShouldNotCreateThePlatform()
    {
        // Arrange
        int created = 0;
        var borrowed = new TestPlatform();
        ApplicationGateway gateway = CreateBuilder()
            .UsePlatform(() =>
            {
                created++;
                return new TestPlatform();
            })
            .UseInfrastructure(borrowed)
            .UseInstanceClient(borrowed)
            .Build();

        // Act
        await gateway.StartAsync().WaitAsync(_timeout);
        await gateway.DisposeAsync().AsTask().WaitAsync(_timeout);

        // Assert
        created.ShouldBe(0);
        borrowed.Infrastructure.Starts.ShouldBe(1);
        borrowed.Disposals.ShouldBe(0);
    }

    [Fact(DisplayName = DisplayPrefix + "A platform set after an infrastructure and a client replaces both")]
    public async Task UsePlatform_AfterUseInfrastructureAndClient_ShouldReplaceBoth()
    {
        // Arrange
        var borrowed = new TestPlatform();
        var platform = new TestPlatform();
        ApplicationGateway gateway = CreateBuilder()
            .UseInfrastructure(borrowed)
            .UseInstanceClient(borrowed)
            .UsePlatform(() => platform)
            .Build();

        // Act
        await gateway.StartAsync().WaitAsync(_timeout);
        await gateway.ExecuteAsync("Web", "orders-api", TestManifests.LogLevel, Write("Debug"));
        await gateway.DisposeAsync().AsTask().WaitAsync(_timeout);

        // Assert
        borrowed.Infrastructure.Starts.ShouldBe(0);
        platform.Infrastructure.Starts.ShouldBe(1);
        platform.Client.Calls.ShouldHaveSingleItem();
        platform.Disposals.ShouldBe(1);
    }

    [Fact(DisplayName = DisplayPrefix + "The gateway builder interface takes a platform and builds a disposable gateway")]
    public async Task UsePlatform_ThroughTheInterface_ShouldOwnThePlatform()
    {
        // Arrange
        var platform = new TestPlatform();
        IApplicationGatewayBuilder builder = CreateBuilder();

        // Act
        IApplicationGateway gateway = builder.UsePlatform(() => platform).Build();
        await gateway.StartAsync().WaitAsync(_timeout);
        await gateway.DisposeAsync().AsTask().WaitAsync(_timeout);

        // Assert
        platform.StopsAtDisposal.ShouldBe(1);
        platform.Disposals.ShouldBe(1);
    }

    private static ApplicationGatewayBuilder CreateBuilder()
    {
        ApplicationGatewayBuilder builder = ApplicationGateway.CreateBuilder("root");
        builder.AddResource("Web", "orders-api");
        return builder;
    }

    private static ApplicationActionContext Write(string level)
    {
        return ApplicationActionContext.Create(ApplicationActionOperation.Write, TestSerializer.Json, Encoding.UTF8.GetBytes($$"""{"Level":"{{level}}"}"""));
    }
}
