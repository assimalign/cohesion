using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApiManager;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.ApiManager.Hosting.Tests;

public class ApplicationLifecycleTests
{
    [Fact(DisplayName = "Cohesion Test [ApiManager] - AddService: Should materialize once and run in registration order")]
    public async Task AddService_WithInstanceAndFactory_ShouldMaterializeOnceAndRunInRegistrationOrder()
    {
        // Arrange
        var events = new List<string>();
        var firstService = new RecordingHostService("first", events);
        var secondService = new RecordingHostService("second", events);
        ApiManagerApplicationContext? factoryContext = null;
        int factoryCalls = 0;
        ApiManagerApplicationBuilder builder = ApiManagerApplication.CreateBuilder([]);

        ApiManagerApplicationBuilder returnedBuilder = builder
            .AddService(firstService)
            .AddService(context =>
            {
                factoryCalls++;
                factoryContext = context;
                return secondService;
            });

        await using ApiManagerApplication application = ((IApiManagerApplicationBuilder)builder).Build().ShouldBeOfType<ApiManagerApplication>();

        // Act
        await ((IApiManagerApplication)application).StartAsync(CancellationToken.None);
        await ((IApiManagerApplication)application).StopAsync(CancellationToken.None);

        // Assert
        returnedBuilder.ShouldBeSameAs(builder);
        factoryCalls.ShouldBe(1);
        factoryContext.ShouldBeSameAs(application.Context);
        ((IApiManagerApplication)application).Context.ShouldBeSameAs(application.Context);
        ((IApiManagerApplication)application).Context.ContentRootPath.ShouldBe(application.Context.Environment.ContentRootPath);
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

    [Fact(DisplayName = "Cohesion Test [ApiManager] - AddService: Null registrations should fail explicitly")]
    public void AddService_WithNullRegistration_ShouldRejectRegistration()
    {
        ApiManagerApplicationBuilder builder = ApiManagerApplication.CreateBuilder([]);

        Should.Throw<ArgumentNullException>(() => builder.AddService((IHostService)null!));
        Should.Throw<ArgumentNullException>(() => builder.AddService(
            (Func<ApiManagerApplicationContext, IHostService>)null!));
    }

    [Fact(DisplayName = "Cohesion Test [ApiManager] - AddService: Null factory result should fail at build")]
    public void Build_WithNullServiceFactoryResult_ShouldRejectService()
    {
        ApiManagerApplicationBuilder builder = ApiManagerApplication.CreateBuilder([]);
        builder.AddService(_ => null!);

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => builder.Build());

        exception.Message.ShouldContain("service factory returned null", Case.Insensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApiManager] - RunAsync: Should stop cleanly when cancellation is requested")]
    public async Task RunAsync_WhenCancellationIsRequested_ShouldStopCleanly()
    {
        // Arrange
        await using ApiManagerApplication application = ApiManagerApplication.CreateBuilder([]).Build();
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
