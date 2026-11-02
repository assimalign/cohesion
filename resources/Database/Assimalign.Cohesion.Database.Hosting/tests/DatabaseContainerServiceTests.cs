using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Hosting.Internal;
using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

public sealed class DatabaseContainerServiceTests
{
    private const string displayPrefix = "Cohesion Test [Database.Hosting] - Container services: ";

    [Fact(DisplayName = displayPrefix + "Build resolves once after AddService and starts in the documented order")]
    public async Task Build_WithContainerServices_ShouldResolveOnceAndPreserveLifecycleOrder()
    {
        // Arrange: register container services first to prove the grouping is independent of call order.
        var events = new List<string>();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var builder = DatabaseApplication.CreateBuilder();
        DatabaseApplicationContext? context = null;
        int factoryCalls = 0;
        builder.Services.AddSingleton<IHostService>(_ =>
        {
            factoryCalls++;
            context.ShouldNotBeNull().Engines.ShouldHaveSingleItem().Name.ShouldBe("sql");
            context.Servers.Count.ShouldBe(2);
            events.Add("container:create");
            return new RecordingService(events, "container-first");
        });
        builder.Services.AddSingleton<IHostService>(new RecordingService(events, "container-second"));
        builder.AddEngine("sql", _ =>
        {
            var engine = new RecordingEngine("sql");
            engine.AddServer(owner => new RecordingServer(events, "server-first", owner));
            engine.AddServer(owner => new RecordingServer(events, "server-second", owner));
            return engine;
        });
        builder.AddService(applicationContext =>
        {
            context = applicationContext;
            events.Add("add:create");
            return new RecordingService(events, "add-first");
        });
        builder.AddService(new RecordingService(events, "add-second"));

        // Act
        await using DatabaseApplication application = builder.Build();
        application.Context.HostedServices.ElementAt(4).ShouldBeOfType<DatabaseReopenService>();
        await ((IHost)application).StartAsync(guard.Token);
        await ((IHost)application).StopAsync(guard.Token);

        // Assert
        factoryCalls.ShouldBe(1);
        events.ShouldBe([
            "add:create", "container:create",
            "add-first:start", "add-second:start", "container-first:start", "container-second:start",
            "server-first:start", "server-second:start", "server-second:stop", "server-first:stop",
            "container-second:stop", "container-first:stop", "add-second:stop", "add-first:stop"]);
    }

    [Theory(DisplayName = displayPrefix + "Factory products are disposed once and instances remain borrowed")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisposeAsync_WithContainerService_ShouldFollowRegistrationOwnership(bool factory)
    {
        // Arrange
        var service = new DisposableHostService();
        var builder = DatabaseApplication.CreateBuilder();
        if (factory)
        {
            builder.Services.AddSingleton<IHostService>(_ => service);
        }
        else
        {
            builder.Services.AddSingleton<IHostService>(service);
        }
        DatabaseApplication application = builder.Build();

        // Act
        await ((IAsyncDisposable)application).DisposeAsync();
        await ((IAsyncDisposable)application).DisposeAsync();

        // Assert
        service.DisposeCount.ShouldBe(factory ? 1 : 0);
    }

    [Theory(DisplayName = displayPrefix + "Duplicate lifecycle objects fail Build")]
    [InlineData(true)]
    [InlineData(false)]
    public void Build_WithDuplicateContainerService_ShouldRefuse(bool alsoAddService)
    {
        // Arrange
        var service = new DisposableHostService();
        var builder = DatabaseApplication.CreateBuilder();
        builder.Services.AddSingleton<IHostService>(service);
        if (alsoAddService)
        {
            builder.AddService(service);
        }
        else
        {
            builder.Services.AddSingleton<IHostService>(service);
        }

        // Act
        var failure = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        failure.Message.ShouldBe(ErrorMessages.BuilderBuildCompositionFailure8);
        service.DisposeCount.ShouldBe(0);
    }

    [Fact(DisplayName = displayPrefix + "A server registered as a container service fails Build")]
    public void Build_WithServerAsContainerService_ShouldRefuse()
    {
        // Arrange
        var builder = DatabaseApplication.CreateBuilder();
        var engine = new RecordingEngine();
        var server = new HostServer(engine);
        engine.AddServer(_ => server);
        builder.AddEngine("recording-engine", _ => engine);
        builder.Services.AddSingleton<IHostService>(server);

        // Act
        var failure = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        failure.Message.ShouldBe(ErrorMessages.BuilderBuildCompositionFailure8);
        engine.DisposeCount.ShouldBe(1);
    }

    [Theory(DisplayName = displayPrefix + "A throwing container factory rolls back all owned roots and aggregates cleanup failures")]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_WhenContainerFactoryThrows_ShouldRollbackAndPreserveFailures(bool cleanupThrows)
    {
        // Arrange
        var construction = new InvalidOperationException("container factory failed");
        var cleanup = new InvalidOperationException("service cleanup failed");
        var engine = new RecordingEngine();
        var owned = new DisposableHostService { DisposeFailure = cleanupThrows ? cleanup : null };
        var containerService = new DisposableHostService();
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine("recording-engine", _ => engine);
        builder.AddService(_ => owned);
        builder.Services.AddSingleton<IHostService>(_ => containerService);
        builder.Services.AddSingleton<IHostService>(_ => throw construction);

        // Act
        Exception failure = Should.Throw<Exception>(() => builder.Build());

        // Assert
        if (cleanupThrows)
        {
            var aggregate = failure.ShouldBeOfType<AggregateException>();
            aggregate.InnerExceptions[0].ShouldBeSameAs(construction);
            aggregate.Flatten().InnerExceptions.ShouldContain(cleanup);
        }
        else
        {
            failure.ShouldBeSameAs(construction);
        }
        owned.DisposeCount.ShouldBe(1);
        engine.DisposeCount.ShouldBe(1);
        containerService.DisposeCount.ShouldBe(1);
    }

    private sealed class DisposableHostService : IHostService, IDisposable
    {
        public ServiceId Id { get; } = ServiceId.New();
        public int DisposeCount { get; private set; }
        public Exception? DisposeFailure { get; init; }
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Dispose()
        {
            DisposeCount++;
            if (DisposeFailure is { } failure)
            {
                throw failure;
            }
        }
    }

    private sealed class HostServer : DatabaseServer, IHostService
    {
        public HostServer(DatabaseEngine engine) : base(engine) { }
        public ServiceId Id { get; } = ServiceId.New();
        public override IReadOnlyCollection<DatabaseServerSession> Sessions => [];
        protected override Task StartCoreAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        protected override Task StopCoreAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
