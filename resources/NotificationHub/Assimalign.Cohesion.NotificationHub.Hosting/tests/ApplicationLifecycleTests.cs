using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.NotificationHub;

namespace Assimalign.Cohesion.NotificationHub.Hosting.Tests;

public class ApplicationLifecycleTests
{
    [Fact(DisplayName = "Cohesion Test [NotificationHub.Hosting] - AddService: Should honor mixed registration lifecycle order")]
    public async Task AddService_WithInstanceAndFactory_ShouldStartInRegistrationOrderAndStopInReverseOrder()
    {
        // Arrange
        List<string> events = [];
        RecordingService firstService = new("first", events);
        RecordingService secondService = new("second", events);
        NotificationHubApplicationBuilder builder = NotificationHubApplication.CreateBuilder([]);
        NotificationHubApplicationContext? factoryContext = null;
        var factoryCount = 0;

        builder
            .AddService(firstService)
            .AddService(context =>
            {
                factoryCount++;
                factoryContext = context;
                return secondService;
            });

        await using NotificationHubApplication application = ((INotificationHubApplicationBuilder)builder).Build().ShouldBeOfType<NotificationHubApplication>();

        // Act
        await ((INotificationHubApplication)application).StartAsync(CancellationToken.None);
        await ((INotificationHubApplication)application).StopAsync(CancellationToken.None);

        // Assert
        factoryCount.ShouldBe(1);
        factoryContext.ShouldBeSameAs(application.Context);
        ((INotificationHubApplication)application).Context.ShouldBeSameAs(application.Context);
        ((INotificationHubApplication)application).Context.ContentRootPath.ShouldBe(application.Context.Environment.ContentRootPath);
        application.Context.HostedServices.ShouldBe(new IHostService[] { firstService, secondService });
        events.ShouldBe(new[] { "first:start", "second:start", "second:stop", "first:stop" });
    }

    [Fact(DisplayName = "Cohesion Test [NotificationHub.Hosting] - AddService: Null registrations should fail explicitly")]
    public void AddService_WithNullRegistration_ShouldRejectRegistration()
    {
        // Arrange
        NotificationHubApplicationBuilder builder = NotificationHubApplication.CreateBuilder([]);

        // Act and assert
        Should.Throw<ArgumentNullException>(() => builder.AddService((IHostService)null!));
        Should.Throw<ArgumentNullException>(() => builder.AddService(
            (Func<NotificationHubApplicationContext, IHostService>)null!));
    }

    [Fact(DisplayName = "Cohesion Test [NotificationHub.Hosting] - Build: Null service factory result should fail explicitly")]
    public void Build_WithNullServiceFactoryResult_ShouldRejectService()
    {
        // Arrange
        NotificationHubApplicationBuilder builder = NotificationHubApplication.CreateBuilder([]);
        builder.AddService(_ => null!);

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain("service factory returned null", Case.Insensitive);
    }

    [Fact(DisplayName = "Cohesion Test [NotificationHub] - RunAsync: Should stop cleanly when cancellation is requested")]
    public async Task RunAsync_WhenCancellationIsRequested_ShouldStopCleanly()
    {
        // Arrange
        await using NotificationHubApplication application = NotificationHubApplication.CreateBuilder([]).Build();
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

    private sealed class RecordingService(
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
