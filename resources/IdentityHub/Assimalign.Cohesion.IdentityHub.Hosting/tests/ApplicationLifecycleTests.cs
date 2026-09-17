using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.IdentityHub;

namespace Assimalign.Cohesion.IdentityHub.Hosting.Tests;

public class ApplicationLifecycleTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityHub] - AddService: Should materialize once and run in registration order")]
    public async Task AddService_WithInstanceAndFactory_ShouldMaterializeOnceAndRunInRegistrationOrder()
    {
        // Arrange
        var events = new List<string>();
        var firstService = new RecordingHostService("first", events);
        var secondService = new RecordingHostService("second", events);
        using var data = new TemporaryDirectory();
        IdentityHubApplicationContext? factoryContext = null;
        int factoryCalls = 0;
        IdentityHubApplicationBuilder builder = IdentityHubTestHost.CreateBuilder(data.Path);

        IdentityHubApplicationBuilder returnedBuilder = builder
            .AddService(firstService)
            .AddService(context =>
            {
                factoryCalls++;
                factoryContext = context;
                return secondService;
            });

        await using IdentityHubApplication application = ((IIdentityHubApplicationBuilder)builder).Build().ShouldBeOfType<IdentityHubApplication>();

        // Act
        await ((IIdentityHubApplication)application).StartAsync(CancellationToken.None);
        await ((IIdentityHubApplication)application).StopAsync(CancellationToken.None);

        // Assert
        returnedBuilder.ShouldBeSameAs(builder);
        factoryCalls.ShouldBe(1);
        factoryContext.ShouldBeSameAs(application.Context);
        ((IIdentityHubApplication)application).Context.ShouldBeSameAs(application.Context);
        ((IIdentityHubApplication)application).Context.ContentRootPath.ShouldBe(application.Context.Environment.ContentRootPath);
        application.Context.HostedServices.Take(2).ShouldBe(
            new IHostService[] { firstService, secondService });
        events.ShouldBe(new[]
        {
            "first:start",
            "second:start",
            "second:stop",
            "first:stop",
        });
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - AddService: Null registrations should fail explicitly")]
    public void AddService_WithNullRegistration_ShouldRejectRegistration()
    {
        IdentityHubApplicationBuilder builder = IdentityHubApplication.CreateBuilder([]);

        Should.Throw<ArgumentNullException>(() => builder.AddService((IHostService)null!));
        Should.Throw<ArgumentNullException>(() => builder.AddService(
            (Func<IdentityHubApplicationContext, IHostService>)null!));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - AddService: Null factory result should fail at build")]
    public void Build_WithNullServiceFactoryResult_ShouldRejectService()
    {
        IdentityHubApplicationBuilder builder = IdentityHubApplication.CreateBuilder([]);
        builder.AddService(_ => null!);

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => builder.Build());

        exception.Message.ShouldContain("service factory returned null", Case.Insensitive);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - RunAsync: Should stop cleanly when cancellation is requested")]
    public async Task RunAsync_WhenCancellationIsRequested_ShouldStopCleanly()
    {
        // Arrange
        using var data = new TemporaryDirectory();
        await using IdentityHubApplication application = IdentityHubTestHost.CreateBuilder(data.Path).Build();
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        // Act
        await application.RunAsync(cancellationTokenSource.Token);

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
