using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.SecretStore.Hosting.Tests;

public class ApplicationDependencyInjectionTests
{
    [Fact(DisplayName = "Cohesion Test [SecretStore.Hosting] - Services: Registration should close when the application is built")]
    public async Task Build_ShouldCloseServiceRegistration()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        using IDisposable scope = CreateScope(directory);
        SecretStoreApplicationBuilder builder = SecretStoreTestHost.CreateBuilder();

        // Act
        await using SecretStoreApplication application = builder.Build();

        // Assert
        Should.Throw<InvalidOperationException>(() => builder.Services.AddSingleton<IHostService>(new DisposableHostService()));
        Should.Throw<InvalidOperationException>(() => builder.AddService(new DisposableHostService()));
        Should.Throw<InvalidOperationException>(() => builder.AddSecret("late", new byte[] { 1 }));
        Should.Throw<InvalidOperationException>(() => builder.Build());
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.Hosting] - Services: Direct IHostService registrations should run in registration order ahead of the endpoint")]
    public async Task Services_WithDirectRegistration_ShouldJoinTheLifecycleAheadOfTheEndpoint()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        using IDisposable scope = CreateScope(directory);
        var events = new List<string>();
        SecretStoreApplicationBuilder builder = SecretStoreTestHost.CreateBuilder();
        builder.AddService(new RecordingHostService("first", events));
        builder.Services.AddSingleton<IHostService>(new RecordingHostService("second", events));
        builder.AddService(_ => new RecordingHostService("third", events));

        await using SecretStoreApplication application = builder.Build();

        // Act
        await ((ISecretStoreApplication)application).StartAsync(CancellationToken.None);
        await ((ISecretStoreApplication)application).StopAsync(CancellationToken.None);

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

    [Fact(DisplayName = "Cohesion Test [SecretStore.Hosting] - Services: Disposal should release factory-created services and leave instances with their callers")]
    public async Task DisposeAsync_ShouldDisposeOnlyFactoryCreatedServices()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        using IDisposable scope = CreateScope(directory);
        var owned = new DisposableHostService();
        var borrowed = new DisposableHostService();
        SecretStoreApplicationBuilder builder = SecretStoreTestHost.CreateBuilder();
        builder.AddService(borrowed);
        builder.AddService(_ => owned);
        SecretStoreApplication application = builder.Build();

        // Act
        await ((IAsyncDisposable)application).DisposeAsync();

        // Assert
        owned.DisposeCount.ShouldBe(1);
        borrowed.DisposeCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.Hosting] - Services: A failed build should dispose the services it already created")]
    public void Build_WithFailingServiceFactory_ShouldDisposeCreatedServices()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        using IDisposable scope = CreateScope(directory);
        var created = new DisposableHostService();
        SecretStoreApplicationBuilder builder = SecretStoreTestHost.CreateBuilder();
        builder.AddService(_ => created);
        builder.AddService(_ => throw new InvalidOperationException("factory failure"));

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldBe("factory failure");
        created.DisposeCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.Hosting] - Services: AddSecret and AddCertificateAuthority should reject duplicate declarations")]
    public void RootVerbs_WithDuplicateDeclarations_ShouldThrow()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        using IDisposable scope = CreateScope(directory);
        ISecretStoreApplicationBuilder builder = SecretStoreTestHost.CreateBuilder();
        builder.AddSecret("db/password", new byte[] { 1, 2, 3 }).AddCertificateAuthority();

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => builder.AddSecret("db/password", new byte[] { 4 }))
            .Message.ShouldContain("already declared", Case.Insensitive);
        Should.Throw<InvalidOperationException>(() => builder.AddCertificateAuthority())
            .Message.ShouldContain("already declared", Case.Insensitive);
    }

    private static IDisposable CreateScope(TemporaryDirectory directory) =>
        ResourceRuntime.CreateScope(SecretStoreTestHost.CreateContext(
            SecretStoreTestHost.GetEndpoint(),
            directory.Path,
            gatewayName: null));

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
