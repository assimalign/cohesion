using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.IdentityHub.Hosting.Tests;

public class ApplicationDependencyInjectionTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Services: Registration should close when the application is built")]
    public async Task Build_ShouldCloseServiceRegistration()
    {
        // Arrange
        using var data = new TemporaryDirectory();
        IdentityHubApplicationBuilder builder = IdentityHubTestHost.CreateBuilder(data.Path);

        // Act
        await using IdentityHubApplication application = builder.Build();

        // Assert
        Should.Throw<InvalidOperationException>(() => builder.Services.AddSingleton<IHostService>(new DisposableHostService()));
        Should.Throw<InvalidOperationException>(() => builder.AddService(new DisposableHostService()));
        Should.Throw<InvalidOperationException>(() => builder.AddAudience("late"));
        Should.Throw<InvalidOperationException>(() => builder.Build());
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Services: Direct IHostService registrations should run in registration order ahead of the endpoint")]
    public async Task Services_WithDirectRegistration_ShouldJoinTheLifecycleAheadOfTheEndpoint()
    {
        // Arrange
        using var data = new TemporaryDirectory();
        var events = new List<string>();
        IdentityHubApplicationBuilder builder = IdentityHubTestHost.CreateBuilder(data.Path);
        builder.AddService(new RecordingHostService("first", events));
        builder.Services.AddSingleton<IHostService>(new RecordingHostService("second", events));
        builder.AddService(_ => new RecordingHostService("third", events));

        await using IdentityHubApplication application = builder.Build();

        // Act
        await ((IIdentityHubApplication)application).StartAsync(CancellationToken.None);
        await ((IIdentityHubApplication)application).StopAsync(CancellationToken.None);

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

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Services: Disposal should release factory-created services and leave instances with their callers")]
    public async Task DisposeAsync_ShouldDisposeOnlyFactoryCreatedServices()
    {
        // Arrange
        using var data = new TemporaryDirectory();
        var owned = new DisposableHostService();
        var borrowed = new DisposableHostService();
        IdentityHubApplicationBuilder builder = IdentityHubTestHost.CreateBuilder(data.Path);
        builder.AddService(borrowed);
        builder.AddService(_ => owned);
        IdentityHubApplication application = builder.Build();

        // Act
        await ((IAsyncDisposable)application).DisposeAsync();

        // Assert
        owned.DisposeCount.ShouldBe(1);
        borrowed.DisposeCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Services: A failed build should dispose the services it already created")]
    public void Build_WithFailingServiceFactory_ShouldDisposeCreatedServices()
    {
        // Arrange
        using var data = new TemporaryDirectory();
        var created = new DisposableHostService();
        IdentityHubApplicationBuilder builder = IdentityHubTestHost.CreateBuilder(data.Path);
        builder.AddService(_ => created);
        builder.AddService(_ => throw new InvalidOperationException("factory failure"));

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldBe("factory failure");
        created.DisposeCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Services: A client that allows an undeclared audience should fail before any service is created")]
    public void Build_WithUndeclaredClientAudience_ShouldFailBeforeServicesAreCreated()
    {
        // Arrange
        using var data = new TemporaryDirectory();
        int factoryCalls = 0;
        IIdentityHubApplicationBuilder builder = IdentityHubTestHost.CreateBuilder(data.Path);
        builder
            .AddAudience("api")
            .AddClient("worker", options =>
            {
                options.ClientSecret = "secret-value-for-tests-only";
                options.Audiences.Add("undeclared");
            });
        ((IdentityHubApplicationBuilder)builder).AddService(_ =>
        {
            factoryCalls++;
            return new DisposableHostService();
        });

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => ((IdentityHubApplicationBuilder)builder).Build());

        // Assert
        exception.Message.ShouldContain("undeclared audience", Case.Insensitive);
        factoryCalls.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Services: AddAudience and AddClient should reject duplicate declarations")]
    public void RootVerbs_WithDuplicateDeclarations_ShouldThrow()
    {
        // Arrange
        using var data = new TemporaryDirectory();
        IIdentityHubApplicationBuilder builder = IdentityHubTestHost.CreateBuilder(data.Path);
        builder.AddAudience("api").AddClient("worker", options =>
        {
            options.ClientSecret = "secret-value-for-tests-only";
            options.Audiences.Add("api");
        });

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => builder.AddAudience("api"))
            .Message.ShouldContain("already declared", Case.Insensitive);
        Should.Throw<InvalidOperationException>(() => builder.AddClient("worker", _ => { }))
            .Message.ShouldContain("already registered", Case.Insensitive);
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
