using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.VpnGateway;

namespace Assimalign.Cohesion.VpnGateway.Hosting.Tests;

public class ApplicationLifecycleTests
{
    [Fact(DisplayName = "Cohesion Test [VpnGateway.Hosting] - AddService: Should honor mixed registration lifecycle order")]
    public async Task AddService_WithInstanceAndFactory_ShouldStartInRegistrationOrderAndStopInReverseOrder()
    {
        // Arrange
        List<string> events = [];
        RecordingService firstService = new("first", events);
        RecordingService secondService = new("second", events);
        VpnGatewayApplicationBuilder builder = VpnGatewayApplication.CreateBuilder([]);
        VpnGatewayApplicationContext? factoryContext = null;
        var factoryCount = 0;

        builder
            .AddService(firstService)
            .AddService(context =>
            {
                factoryCount++;
                factoryContext = context;
                return secondService;
            });

        await using VpnGatewayApplication application = ((IVpnGatewayApplicationBuilder)builder).Build().ShouldBeOfType<VpnGatewayApplication>();

        // Act
        await ((IVpnGatewayApplication)application).StartAsync(CancellationToken.None);
        await ((IVpnGatewayApplication)application).StopAsync(CancellationToken.None);

        // Assert
        factoryCount.ShouldBe(1);
        factoryContext.ShouldBeSameAs(application.Context);
        ((IVpnGatewayApplication)application).Context.ShouldBeSameAs(application.Context);
        ((IVpnGatewayApplication)application).Context.ContentRootPath.ShouldBe(application.Context.Environment.ContentRootPath);
        application.Context.HostedServices.ShouldBe(new IHostService[] { firstService, secondService });
        events.ShouldBe(new[] { "first:start", "second:start", "second:stop", "first:stop" });
    }

    [Fact(DisplayName = "Cohesion Test [VpnGateway.Hosting] - AddService: Null registrations should fail explicitly")]
    public void AddService_WithNullRegistration_ShouldRejectRegistration()
    {
        // Arrange
        VpnGatewayApplicationBuilder builder = VpnGatewayApplication.CreateBuilder([]);

        // Act and assert
        Should.Throw<ArgumentNullException>(() => builder.AddService((IHostService)null!));
        Should.Throw<ArgumentNullException>(() => builder.AddService(
            (Func<VpnGatewayApplicationContext, IHostService>)null!));
    }

    [Fact(DisplayName = "Cohesion Test [VpnGateway.Hosting] - Build: Null service factory result should fail explicitly")]
    public void Build_WithNullServiceFactoryResult_ShouldRejectService()
    {
        // Arrange
        VpnGatewayApplicationBuilder builder = VpnGatewayApplication.CreateBuilder([]);
        builder.AddService(_ => null!);

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain("service factory returned null", Case.Insensitive);
    }

    [Fact(DisplayName = "Cohesion Test [VpnGateway] - RunAsync: Should stop cleanly when cancellation is requested")]
    public async Task RunAsync_WhenCancellationIsRequested_ShouldStopCleanly()
    {
        // Arrange
        await using VpnGatewayApplication application = VpnGatewayApplication.CreateBuilder([]).Build();
        using var cancellationTokenSource = new CancellationTokenSource();


        // Act
        Task run = application.RunAsync(cancellationTokenSource.Token);
        using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (application.Context.State is not HostState.Started)
        {
            await Task.Delay(10, startupTimeout.Token);
        }
        cancellationTokenSource.Cancel();
        await run;

        // Assert
        application.Context.State.ShouldBe(HostState.Stopped);
    }

    private sealed class RecordingService : IHostService
    {
        private readonly string _name;
        private readonly ICollection<string> _events;

        /// <summary>
        /// Initializes a new instance of the <see cref="RecordingService"/> class.
        /// </summary>
        /// <param name="name">The service name recorded with each lifecycle event.</param>
        /// <param name="events">The collection that receives the recorded lifecycle events.</param>
        public RecordingService(
            string name,
            ICollection<string> events)
        {
            _name = name;
            _events = events;
        }

        public ServiceId Id { get; } = ServiceId.New();

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _events.Add($"{_name}:start");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            _events.Add($"{_name}:stop");
            return Task.CompletedTask;
        }
    }
}
