using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ConfigurationStore;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.ConfigurationStore.Hosting.Tests;

public class ApplicationLifecycleTests
{
    [Fact(DisplayName = "Cohesion Test [ConfigurationStore] - AddService: Should materialize once and run in registration order")]
    public async Task AddService_WithInstanceAndFactory_ShouldMaterializeOnceAndRunInRegistrationOrder()
    {
        // Arrange
        using var testScope = new PlainConfigurationStoreScope();
        var events = new List<string>();
        var firstService = new RecordingHostService("first", events);
        var secondService = new RecordingHostService("second", events);
        ConfigurationStoreApplicationContext? factoryContext = null;
        int factoryCalls = 0;
        ConfigurationStoreApplicationBuilder builder = testScope.Builder;

        ConfigurationStoreApplicationBuilder returnedBuilder = builder
            .AddService(firstService)
            .AddService(context =>
            {
                factoryCalls++;
                factoryContext = context;
                return secondService;
            });

        await using ConfigurationStoreApplication application = ((IConfigurationStoreApplicationBuilder)builder).Build().ShouldBeOfType<ConfigurationStoreApplication>();

        // Act
        await ((IConfigurationStoreApplication)application).StartAsync(CancellationToken.None);
        await ((IConfigurationStoreApplication)application).StopAsync(CancellationToken.None);

        // Assert
        returnedBuilder.ShouldBeSameAs(builder);
        factoryCalls.ShouldBe(1);
        factoryContext.ShouldBeSameAs(application.Context);
        ((IConfigurationStoreApplication)application).Context.ShouldBeSameAs(application.Context);
        ((IConfigurationStoreApplication)application).Context.ContentRootPath.ShouldBe(application.Context.Environment.ContentRootPath);
        application.Context.HostedServices.Take(2).ShouldBe(
            new IHostService[] { firstService, secondService });
        application.Context.HostedServices.Count().ShouldBe(3);
        ((HostContext)application.Context).Runner.ShouldBeNull();
        events.ShouldBe(new[]
        {
            "first:start",
            "second:start",
            "second:stop",
            "first:stop",
        });
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore] - AddService: Null registrations should fail explicitly")]
    public void AddService_WithNullRegistration_ShouldRejectRegistration()
    {
        using var testScope = new PlainConfigurationStoreScope();
        ConfigurationStoreApplicationBuilder builder = testScope.Builder;

        Should.Throw<ArgumentNullException>(() => builder.AddService((IHostService)null!));
        Should.Throw<ArgumentNullException>(() => builder.AddService(
            (Func<ConfigurationStoreApplicationContext, IHostService>)null!));
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore] - AddService: Null factory result should fail at build")]
    public void Build_WithNullServiceFactoryResult_ShouldRejectService()
    {
        using var testScope = new PlainConfigurationStoreScope();
        ConfigurationStoreApplicationBuilder builder = testScope.Builder;
        builder.AddService(_ => null!);

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => builder.Build());

        exception.Message.ShouldContain("service factory returned null", Case.Insensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore] - RunAsync: Should stop cleanly when cancellation is requested")]
    public async Task RunAsync_WhenCancellationIsRequested_ShouldStopCleanly()
    {
        // Arrange
        using var testScope = new PlainConfigurationStoreScope();
        await using ConfigurationStoreApplication application = testScope.Builder.Build();
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
