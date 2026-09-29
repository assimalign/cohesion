using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.ConfigurationStore.Hosting.Tests;

public class ApplicationDependencyInjectionTests
{
    [Fact(DisplayName = "Cohesion Test [ConfigurationStore] - Services: Registration should close when the application is built")]
    public async Task Build_ShouldCloseServiceRegistration()
    {
        // Arrange
        using var testScope = new PlainConfigurationStoreScope();
        ConfigurationStoreApplicationBuilder builder = testScope.Builder;

        // Act
        await using ConfigurationStoreApplication application = builder.Build();

        // Assert
        Should.Throw<InvalidOperationException>(() => builder.Services.AddSingleton<IHostService>(new DisposableHostService()));
        Should.Throw<InvalidOperationException>(() => builder.AddService(new DisposableHostService()));
        Should.Throw<InvalidOperationException>(() => builder.AddNamespace("late", _ => { }));
        Should.Throw<InvalidOperationException>(() => builder.Build());
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore] - Services: Direct IHostService registrations should run in registration order ahead of the endpoint")]
    public async Task Services_WithDirectRegistration_ShouldJoinTheLifecycleAheadOfTheEndpoint()
    {
        // Arrange
        using var testScope = new PlainConfigurationStoreScope();
        var events = new List<string>();
        ConfigurationStoreApplicationBuilder builder = testScope.Builder;
        builder.AddService(new RecordingHostService("first", events));
        builder.Services.AddSingleton<IHostService>(new RecordingHostService("second", events));
        builder.AddService(_ => new RecordingHostService("third", events));

        await using ConfigurationStoreApplication application = builder.Build();

        // Act
        await ((IConfigurationStoreApplication)application).StartAsync(CancellationToken.None);
        await ((IConfigurationStoreApplication)application).StopAsync(CancellationToken.None);

        // Assert
        application.Context.HostedServices.Count().ShouldBe(4);
        events.ShouldBe(new[]
        {
            "first:start",
            "second:start",
            "third:start",
            "third:stop",
            "second:stop",
            "first:stop",
        });
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore] - Services: Disposal should release factory-created services and leave instances with their callers")]
    public async Task DisposeAsync_ShouldDisposeOnlyFactoryCreatedServices()
    {
        // Arrange
        using var testScope = new PlainConfigurationStoreScope();
        var owned = new DisposableHostService();
        var borrowed = new DisposableHostService();
        ConfigurationStoreApplicationBuilder builder = testScope.Builder;
        builder.AddService(borrowed);
        builder.AddService(_ => owned);
        ConfigurationStoreApplication application = builder.Build();

        // Act
        await ((IAsyncDisposable)application).DisposeAsync();

        // Assert
        owned.DisposeCount.ShouldBe(1);
        borrowed.DisposeCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore] - Services: A failed build should dispose the services it already created")]
    public void Build_WithFailingServiceFactory_ShouldDisposeCreatedServices()
    {
        // Arrange
        using var testScope = new PlainConfigurationStoreScope();
        var created = new DisposableHostService();
        ConfigurationStoreApplicationBuilder builder = testScope.Builder;
        builder.AddService(_ => created);
        builder.AddService(_ => throw new InvalidOperationException("factory failure"));

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldBe("factory failure");
        created.DisposeCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore] - Services: AddNamespace should reject a namespace that is already declared")]
    public void AddNamespace_WithDuplicateName_ShouldThrow()
    {
        // Arrange
        using var testScope = new PlainConfigurationStoreScope();
        IConfigurationStoreApplicationBuilder builder = testScope.Builder;
        builder.AddNamespace("app", namespaceBuilder => namespaceBuilder.Set("Mode", "first"));

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => builder.AddNamespace("app", namespaceBuilder => namespaceBuilder.Set("Mode", "second")));

        // Assert
        exception.Message.ShouldContain("already declared", Case.Insensitive);
    }

    private sealed class RecordingHostService : IHostService
    {
        private readonly string _name;
        private readonly ICollection<string> _events;

        public RecordingHostService(string name, ICollection<string> events)
        {
            _name = name;
            _events = events;
        }

        public ServiceId Id { get; } = ServiceId.New();

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            _events.Add($"{_name}:start");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            _events.Add($"{_name}:stop");
            return Task.CompletedTask;
        }
    }

    private sealed class DisposableHostService : IHostService, IDisposable
    {
        public ServiceId Id { get; } = ServiceId.New();

        public int DisposeCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Dispose() => DisposeCount++;
    }
}
