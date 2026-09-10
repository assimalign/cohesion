using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.SecretStore;

namespace Assimalign.Cohesion.SecretStore.Hosting.Tests;

public class ApplicationLifecycleTests
{
    [Fact(DisplayName = "Cohesion Test [SecretStore.Hosting] - AddService: Should honor mixed registration lifecycle order")]
    public async Task AddService_WithInstanceAndFactory_ShouldStartInRegistrationOrderAndStopInReverseOrder()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        using IDisposable scope = ResourceRuntime.CreateScope(SecretStoreTestHost.CreateContext(
            SecretStoreTestHost.GetEndpoint(),
            directory.Path,
            gatewayName: null));
        List<string> events = [];
        RecordingService firstService = new("first", events);
        RecordingService secondService = new("second", events);
        ISecretStoreApplicationBuilder builder = SecretStoreTestHost.CreateBuilder();
        IHostContext? factoryContext = null;
        var factoryCount = 0;

        builder
            .AddService(firstService)
            .AddService(context =>
            {
                factoryCount++;
                factoryContext = context;
                return secondService;
            });

        await using ISecretStoreApplication application = builder.Build();

        // Act
        await application.StartAsync();
        await application.StopAsync();

        // Assert
        factoryCount.ShouldBe(1);
        factoryContext.ShouldBeSameAs(application.Context);
        application.Context.HostedServices.Count().ShouldBe(3);
        application.Context.HostedServices.Take(2)
            .ShouldBe(new IHostService[] { firstService, secondService });
        events.ShouldBe(new[] { "first:start", "second:start", "second:stop", "first:stop" });
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.Hosting] - AddService: Null registrations should fail explicitly")]
    public void AddService_WithNullRegistration_ShouldRejectRegistration()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        using IDisposable scope = ResourceRuntime.CreateScope(SecretStoreTestHost.CreateContext(
            SecretStoreTestHost.GetEndpoint(),
            directory.Path,
            gatewayName: null));
        ISecretStoreApplicationBuilder builder = SecretStoreTestHost.CreateBuilder();

        // Act and assert
        Should.Throw<ArgumentNullException>(() => builder.AddService((IHostService)null!));
        Should.Throw<ArgumentNullException>(() => builder.AddService(
            (Func<IHostContext, IHostService>)null!));
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.Hosting] - Build: Null service factory result should fail explicitly")]
    public void Build_WithNullServiceFactoryResult_ShouldRejectService()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        using IDisposable scope = ResourceRuntime.CreateScope(SecretStoreTestHost.CreateContext(
            SecretStoreTestHost.GetEndpoint(),
            directory.Path,
            gatewayName: null));
        ISecretStoreApplicationBuilder builder = SecretStoreTestHost.CreateBuilder();
        builder.AddService(_ => null!);

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain("service factory returned null", Case.Insensitive);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore] - RunAsync: Should stop cleanly when cancellation is requested")]
    public async Task RunAsync_WhenCancellationIsRequested_ShouldStopCleanly()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        using IDisposable scope = ResourceRuntime.CreateScope(SecretStoreTestHost.CreateContext(
            SecretStoreTestHost.GetEndpoint(),
            directory.Path,
            gatewayName: null));
        await using ISecretStoreApplication application = SecretStoreTestHost.CreateBuilder().Build();
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        // Act
        await application.RunAsync(cancellationTokenSource.Token);

        // Assert
        application.Context.State.ShouldBe(HostState.Stopped);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.Hosting] - DisposeAsync: Should dispose the owned endpoint service")]
    public async Task DisposeAsync_ShouldDisposeOwnedEndpointService()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        using IDisposable scope = ResourceRuntime.CreateScope(SecretStoreTestHost.CreateContext(
            SecretStoreTestHost.GetEndpoint(),
            directory.Path,
            gatewayName: null));
        ISecretStoreApplication application = SecretStoreTestHost.CreateBuilder().Build();
        IHostService endpointService = application.Context.HostedServices.Last();
        await application.StartAsync();

        // Act
        await application.DisposeAsync();

        // Assert
        await Should.ThrowAsync<ObjectDisposedException>(() => endpointService.StartAsync());
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
