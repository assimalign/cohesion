using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

public class WebApplicationServerHostServiceTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Server wrapper: Forwards lifecycle tokens without disposing the server")]
    public async Task Lifecycle_WithBorrowedServer_ShouldForwardWithoutDisposal()
    {
        // Arrange
        RecordingServer server = new();
        WebApplicationServerHostService service = new(server);
        WebApplicationServerHostService second = new(server);
        using CancellationTokenSource startup = new();
        using CancellationTokenSource shutdown = new();

        // Act
        await service.StartAsync(startup.Token);
        await service.StopAsync(shutdown.Token);

        // Assert
        service.Server.ShouldBeSameAs(server);
        service.Id.ShouldNotBe(second.Id);
        server.StartToken.ShouldBe(startup.Token);
        server.StopToken.ShouldBe(shutdown.Token);
        server.StartCount.ShouldBe(1);
        server.StopCount.ShouldBe(1);
        server.DisposeCount.ShouldBe(0);
        (((object)service) is IDisposable).ShouldBeFalse();
        (((object)service) is IAsyncDisposable).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Server wrapper: Rejects a null server")]
    public void Constructor_WithNullServer_ShouldThrow()
    {
        // Arrange / Act / Assert
        Should.Throw<ArgumentNullException>(() => new WebApplicationServerHostService(null!));
    }

    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - UseServer: Accepts a server without IHostService and retains registration ownership")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UseServer_WithPlainServer_ShouldAdaptLifecycleAndPreserveOwnership(bool useFactory)
    {
        // Arrange
        RecordingServer server = new();
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        if (useFactory)
        {
            builder.Server.UseServer(_ => server);
        }
        else
        {
            builder.Server.UseServer(server);
        }
        await using WebApplication application = builder.Build();

        // Act
        await ((IWebApplication)application).StartAsync(CancellationToken.None);
        application.Context.Servers.Single().ShouldBeSameAs(server);
        application.Context.HostedServices.Single().ShouldBeOfType<WebApplicationServerHostService>()
            .Server.ShouldBeSameAs(server);
        await ((IAsyncDisposable)application).DisposeAsync();

        // Assert
        server.StartCount.ShouldBe(1);
        server.StopCount.ShouldBe(1);
        server.DisposeCount.ShouldBe(useFactory ? 1 : 0);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - UseServer: Wraps dual-interface servers and invokes their Web lifecycle")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UseServer_WithDualInterfaceServer_ShouldInvokeWebLifecycleThroughWrapper(bool useFactory)
    {
        // Arrange
        DualInterfaceServer server = new();
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        if (useFactory)
        {
            builder.Server.UseServer(_ => server);
        }
        else
        {
            builder.Server.UseServer(server);
        }
        await using WebApplication application = builder.Build();

        // Act
        await ((IWebApplication)application).StartAsync(CancellationToken.None);
        IHostService hostedService = application.Context.HostedServices.Single();
        await ((IWebApplication)application).StopAsync(CancellationToken.None);
        await ((IAsyncDisposable)application).DisposeAsync();

        // Assert
        server.WebStartCount.ShouldBe(1);
        server.WebStopCount.ShouldBe(1);
        server.HostStartCount.ShouldBe(0);
        server.HostStopCount.ShouldBe(0);
        hostedService.ShouldBeOfType<WebApplicationServerHostService>().Server.ShouldBeSameAs(server);
        application.Context.HostedServices.Single().ShouldBeSameAs(hostedService);
        application.Context.Servers.Single().ShouldBeSameAs(server);
        server.DisposeCount.ShouldBe(useFactory ? 1 : 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Server wrapper: Preserves a custom server failure")]
    public async Task StartAsync_WithCustomFailure_ShouldPreserveException()
    {
        // Arrange
        InvalidOperationException failure = new("custom startup");
        RecordingServer server = new() { Failure = failure };
        WebApplicationServerHostService service = new(server);

        // Act
        Exception actual = await Should.ThrowAsync<InvalidOperationException>(
            () => service.StartAsync(CancellationToken.None));

        // Assert
        actual.ShouldBeSameAs(failure);
    }

    private sealed class DualInterfaceServer : IWebApplicationServer, IHostService, IDisposable
    {
        public ServiceId Id { get; } = ServiceId.New();
        public int WebStartCount { get; private set; }
        public int WebStopCount { get; private set; }
        public int HostStartCount { get; private set; }
        public int HostStopCount { get; private set; }
        public int DisposeCount { get; private set; }

        Task IWebApplicationServer.StartAsync(CancellationToken cancellationToken)
        {
            WebStartCount++;
            return Task.CompletedTask;
        }

        Task IWebApplicationServer.StopAsync(CancellationToken cancellationToken)
        {
            WebStopCount++;
            return Task.CompletedTask;
        }

        Task IHostService.StartAsync(CancellationToken cancellationToken)
        {
            HostStartCount++;
            return Task.CompletedTask;
        }

        Task IHostService.StopAsync(CancellationToken cancellationToken)
        {
            HostStopCount++;
            return Task.CompletedTask;
        }

        public void Dispose() => DisposeCount++;
    }

    private sealed class RecordingServer : IWebApplicationServer, IDisposable
    {
        public CancellationToken StartToken { get; private set; }
        public CancellationToken StopToken { get; private set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int DisposeCount { get; private set; }
        public Exception? Failure { get; init; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartToken = cancellationToken;
            StartCount++;
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopToken = cancellationToken;
            StopCount++;
            return Task.CompletedTask;
        }

        public void Dispose() => DisposeCount++;
    }
}
