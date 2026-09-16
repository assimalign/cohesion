using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.IoTHub;

namespace Assimalign.Cohesion.IoTHub.Hosting.Tests;

public class ApplicationLifecycleTests
{
    [Fact(DisplayName = "Cohesion Test [IoTHub] - AddService: Should materialize once and run in registration order")]
    public async Task AddService_WithInstanceAndFactory_ShouldMaterializeOnceAndRunInRegistrationOrder()
    {
        // Arrange
        var events = new List<string>();
        var firstService = new RecordingHostService("first", events);
        var secondService = new RecordingHostService("second", events);
        IoTHubApplicationContext? factoryContext = null;
        int factoryCalls = 0;
        IoTHubApplicationBuilder builder = IoTHubApplication.CreateBuilder([]);

        IoTHubApplicationBuilder returnedBuilder = builder
            .AddService(firstService)
            .AddService(context =>
            {
                factoryCalls++;
                factoryContext = context;
                return secondService;
            });

        await using IoTHubApplication application = ((IIoTHubApplicationBuilder)builder).Build().ShouldBeOfType<IoTHubApplication>();

        // Act
        await ((IIoTHubApplication)application).StartAsync(CancellationToken.None);
        await ((IIoTHubApplication)application).StopAsync(CancellationToken.None);

        // Assert
        returnedBuilder.ShouldBeSameAs(builder);
        factoryCalls.ShouldBe(1);
        factoryContext.ShouldBeSameAs(application.Context);
        ((IIoTHubApplication)application).Context.ShouldBeSameAs(application.Context);
        ((IIoTHubApplication)application).Context.ContentRootPath.ShouldBe(application.Context.Environment.ContentRootPath);
        application.Context.HostedServices.ShouldBe(
            new IHostService[] { firstService, secondService });
        events.ShouldBe(new[]
        {
            "first:start",
            "second:start",
            "second:stop",
            "first:stop",
        });
    }

    [Fact(DisplayName = "Cohesion Test [IoTHub] - AddService: Null registrations should fail explicitly")]
    public void AddService_WithNullRegistration_ShouldRejectRegistration()
    {
        IoTHubApplicationBuilder builder = IoTHubApplication.CreateBuilder([]);

        Should.Throw<ArgumentNullException>(() => builder.AddService((IHostService)null!));
        Should.Throw<ArgumentNullException>(() => builder.AddService(
            (Func<IoTHubApplicationContext, IHostService>)null!));
    }

    [Fact(DisplayName = "Cohesion Test [IoTHub] - AddService: Null factory result should fail at build")]
    public void Build_WithNullServiceFactoryResult_ShouldRejectService()
    {
        IoTHubApplicationBuilder builder = IoTHubApplication.CreateBuilder([]);
        builder.AddService(_ => null!);

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => builder.Build());

        exception.Message.ShouldContain("service factory returned null", Case.Insensitive);
    }

    [Fact(DisplayName = "Cohesion Test [IoTHub] - RunAsync: Should stop cleanly when cancellation is requested")]
    public async Task RunAsync_WhenCancellationIsRequested_ShouldStopCleanly()
    {
        // Arrange
        await using IoTHubApplication application = IoTHubApplication.CreateBuilder([]).Build();
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

    private sealed class RecordingHostService(
        string name,
        ICollection<string> events) : IHostService
    {
        public ServiceId Id { get; } = ServiceId.New();

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add($"{name}:start");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            events.Add($"{name}:stop");
            return Task.CompletedTask;
        }
    }
}
