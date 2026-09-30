using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.MediaHub.Hosting.Tests;

public class ApplicationDependencyInjectionTests
{
    [Fact(DisplayName = "Cohesion Test [MediaHub] - Services: Registration should close when the application is built")]
    public async Task Build_ShouldCloseServiceRegistration()
    {
        // Arrange
        MediaHubApplicationBuilder builder = MediaHubApplication.CreateBuilder([]);

        // Act
        await using MediaHubApplication application = builder.Build();

        // Assert
        Should.Throw<InvalidOperationException>(() => builder.Services.AddSingleton<IHostService>(new DisposableHostService()));
        Should.Throw<InvalidOperationException>(() => builder.AddService(new DisposableHostService()));
        Should.Throw<InvalidOperationException>(() => builder.Build());
    }

    [Fact(DisplayName = "Cohesion Test [MediaHub] - Services: Direct IHostService registrations should join the lifecycle in registration order")]
    public async Task Services_WithDirectRegistration_ShouldJoinTheLifecycleInRegistrationOrder()
    {
        // Arrange
        var events = new List<string>();
        MediaHubApplicationBuilder builder = MediaHubApplication.CreateBuilder([]);
        builder.AddService(new RecordingHostService("first", events));
        builder.Services.AddSingleton<IHostService>(new RecordingHostService("second", events));
        builder.AddService(_ => new RecordingHostService("third", events));

        await using MediaHubApplication application = builder.Build();

        // Act
        await ((IMediaHubApplication)application).StartAsync(CancellationToken.None);
        await ((IMediaHubApplication)application).StopAsync(CancellationToken.None);

        // Assert
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

    [Fact(DisplayName = "Cohesion Test [MediaHub] - Services: Disposal should release factory-created services and leave instances with their callers")]
    public async Task DisposeAsync_ShouldDisposeOnlyFactoryCreatedServices()
    {
        // Arrange
        var owned = new DisposableHostService();
        var borrowed = new DisposableHostService();
        MediaHubApplicationBuilder builder = MediaHubApplication.CreateBuilder([]);
        builder.AddService(borrowed);
        builder.AddService(_ => owned);
        MediaHubApplication application = builder.Build();

        // Act
        await ((IAsyncDisposable)application).DisposeAsync();

        // Assert
        owned.DisposeCount.ShouldBe(1);
        borrowed.DisposeCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [MediaHub] - Services: A failed build should dispose the services it already created")]
    public void Build_WithFailingServiceFactory_ShouldDisposeCreatedServices()
    {
        // Arrange
        var created = new DisposableHostService();
        MediaHubApplicationBuilder builder = MediaHubApplication.CreateBuilder([]);
        builder.AddService(_ => created);
        builder.AddService(_ => throw new InvalidOperationException("factory failure"));

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldBe("factory failure");
        created.DisposeCount.ShouldBe(1);
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
