using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Configuration;
using Assimalign.Cohesion.Database.Documents;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

/// <summary>Verifies the one-shot composition, infrastructure, and ownership boundary.</summary>
public sealed class DatabaseCompositionTests
{
    /// <summary>Verifies hosting composes repeated and unrelated engine models without model-specific branches.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Build: accepts two engines of one model and another model")]
    public async Task Build_WithRepeatedAndDifferentModels_ShouldRetainEveryNamedEngine()
    {
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddSql((_, engine) => engine.EngineName = "orders");
        builder.AddSql((_, engine) => engine.EngineName = "analytics");
        builder.AddDocuments((_, engine) => engine.EngineName = "catalog");

        await using var application = builder.Build();

        application.Context.Engines.Count.ShouldBe(3);
        application.Context.GetEngine("orders").Model.ShouldBe(EngineModel.Sql);
        application.Context.GetEngine("analytics").Model.ShouldBe(EngineModel.Sql);
        application.Context.GetEngine("catalog").Model.ShouldBe(EngineModel.Document);
        foreach (IDatabaseEngine engine in application.Context.Engines)
        {
            IDatabase database = await engine.CreateDatabaseAsync("app");
            database.Engine.ShouldBeSameAs(engine);
            engine.TryGetDatabase("app", out IDatabase found).ShouldBeTrue();
            found.ShouldBeSameAs(database);
        }
    }

    /// <summary>Verifies deferred construction and registration freeze through retained surfaces.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Build: defers factories and freezes every registration surface")]
    public async Task Build_WithDeferredFactories_ShouldConstructOnceAndFreezeRegistrations()
    {
        var builder = DatabaseApplication.CreateBuilder([]);
        var configuration = builder.Configuration;
        var services = builder.Services;
        var container = services.Container;
        int engineCalls = 0;
        int configurationCalls = 0;
        configuration.AddProvider(_ =>
        {
            configurationCalls++;
            return new TestConfigurationProvider();
        });
        builder.AddEngine(_ =>
        {
            engineCalls++;
            Should.Throw<InvalidOperationException>(() => builder.Build());
            Should.Throw<InvalidOperationException>(() => builder.AddEngine(new RecordingEngine("late")));
            return new RecordingEngine();
        });
        engineCalls.ShouldBe(0);
        configurationCalls.ShouldBe(0);
        Should.Throw<InvalidOperationException>(() => services.Build());
        Should.Throw<InvalidOperationException>(() => configuration.Build());

        await using var application = builder.Build();

        engineCalls.ShouldBe(1);
        configurationCalls.ShouldBe(1);
        Should.Throw<InvalidOperationException>(() => builder.Build());
        Should.Throw<InvalidOperationException>(() => builder.AddEngine(_ => new RecordingEngine()));
        Should.Throw<InvalidOperationException>(() => builder.AddService(new RecordingService([])));
        Should.Throw<InvalidOperationException>(() => configuration.AddProvider(_ => new TestConfigurationProvider()));
        Should.Throw<InvalidOperationException>(() => services.AddSingleton(new Marker()));
        Should.Throw<InvalidOperationException>(() => container.Clear());
    }

    /// <summary>Verifies that failed construction compensates accepted products and consumes Build.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Build: failure disposes owned products and preserves borrowed inputs")]
    public void Build_WhenLaterFactoryFails_ShouldRollbackOwnedProductsAndRejectRetry()
    {
        var borrowed = new RecordingEngine("borrowed");
        var owned = new RecordingEngine("owned");
        var server = new RecordingServer([], engine: owned);
        owned.AddServer(_ => server);
        var failure = new InvalidOperationException("construction failed");
        var builder = DatabaseApplication.CreateBuilder();
        var configuration = new TestConfigurationProvider();
        var dependency = new DisposableService();
        builder.Configuration.AddProvider(_ => configuration);
        builder.Services.AddSingleton(_ => dependency);
        builder.AddEngine(borrowed);
        builder.AddEngine("owned", context =>
        {
            context.Services.GetRequiredService<DisposableService>().ShouldBeSameAs(dependency);
            return owned;
        });
        builder.AddEngine(_ => throw failure);

        Should.Throw<InvalidOperationException>(() => builder.Build()).ShouldBeSameAs(failure);

        borrowed.DisposeCount.ShouldBe(0);
        owned.DisposeCount.ShouldBe(1);
        server.DisposeCount.ShouldBe(1);
        dependency.DisposeCount.ShouldBe(1);
        configuration.DisposeCount.ShouldBe(1);
        Should.Throw<InvalidOperationException>(() => builder.Build());
        Should.Throw<InvalidOperationException>(() => builder.AddEngine(new RecordingEngine("late")));
    }

    /// <summary>Verifies compensation retains construction and cleanup errors and continues cleanup.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Build: rollback preserves failures and continues cleanup")]
    public void Build_WhenRollbackAlsoFails_ShouldPreserveBothFailures()
    {
        var first = new RecordingEngine("first");
        var cleanupFailure = new InvalidOperationException("cleanup failed");
        var second = new RecordingEngine("second") { DisposeException = cleanupFailure };
        var constructionFailure = new InvalidOperationException("construction failed");
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine(_ => first);
        builder.AddEngine(_ => second);
        builder.AddEngine(_ => throw constructionFailure);

        var actual = Should.Throw<AggregateException>(() => builder.Build());

        actual.InnerExceptions.ShouldContain(constructionFailure);
        actual.InnerExceptions.ShouldContain(cleanupFailure);
        first.DisposeCount.ShouldBe(1);
        second.DisposeCount.ShouldBe(1);
    }

    /// <summary>Verifies ordinal engine identity and named-factory compensation.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Build: rejects mismatched named engines and disposes their products")]
    public void Build_WhenNamedFactoryReturnsWrongName_ShouldDisposeRejectedProduct()
    {
        var engine = new RecordingEngine("actual");
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine("expected", _ => engine);

        Should.Throw<InvalidOperationException>(() => builder.Build());

        engine.DisposeCount.ShouldBe(1);
    }

    /// <summary>Verifies duplicate names fail without treating a borrowed engine as owned.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Build: rejects duplicate names and borrowed factory products")]
    public void Build_WhenEngineIdentityCollides_ShouldPreserveBorrowedOwnership()
    {
        var borrowed = new RecordingEngine("same");
        var duplicate = new RecordingEngine("same");
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine(borrowed);
        builder.AddEngine(_ => duplicate);
        Should.Throw<InvalidOperationException>(() => builder.Build());
        borrowed.DisposeCount.ShouldBe(0);
        duplicate.DisposeCount.ShouldBe(1);

        var collision = DatabaseApplication.CreateBuilder();
        collision.AddEngine(borrowed);
        collision.AddEngine(_ => borrowed);
        Should.Throw<InvalidOperationException>(() => collision.Build());
        borrowed.DisposeCount.ShouldBe(0);
    }

    /// <summary>Verifies nested servers cannot front another engine.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Build: rejects a nested server with a different engine")]
    public void Build_WhenNestedServerFrontsAnotherEngine_ShouldRejectComposition()
    {
        var owner = new RecordingEngine("owner");
        var other = new RecordingEngine("other");
        var server = new RecordingServer([], engine: other);
        owner.AddServer(_ => server);
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine(_ => owner);

        Should.Throw<InvalidOperationException>(() => builder.Build());

        owner.DisposeCount.ShouldBe(1);
        other.DisposeCount.ShouldBe(0);
        server.DisposeCount.ShouldBe(1);
    }

    /// <summary>Verifies a nested server cannot also be registered as an independent lifecycle service.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Build: rejects one object registered as both nested server and service")]
    public void Build_WhenServerAlsoRegisteredAsService_ShouldRejectLifecycleAlias()
    {
        var engine = new RecordingEngine();
        var alias = new ServerService(engine);
        engine.AddServer(_ => alias);
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine(_ => engine);
        builder.AddService(alias);

        Should.Throw<InvalidOperationException>(() => builder.Build());
    }

    /// <summary>Verifies application disposal owns factories, including nested servers, and borrows instances.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Dispose: owned engines dispose nested servers once and borrowed inputs survive")]
    public async Task Dispose_WithOwnedAndBorrowedProducts_ShouldRespectOwnership()
    {
        var owned = new RecordingEngine("owned");
        var borrowed = new RecordingEngine("borrowed");
        var ownedServer = new RecordingServer([], engine: owned);
        var borrowedServer = new RecordingServer([], engine: borrowed);
        owned.AddServer(_ => ownedServer);
        borrowed.AddServer(_ => borrowedServer);
        var ownedService = new DisposableService();
        var borrowedService = new DisposableService();
        var builder = DatabaseApplication.CreateBuilder();
        var configuration = new TestConfigurationProvider();
        builder.Configuration.AddProvider(_ => configuration);
        builder.AddEngine(_ => owned);
        builder.AddEngine(borrowed);
        builder.AddService(_ => ownedService);
        builder.AddService(borrowedService);
        var application = builder.Build();

        await ((IAsyncDisposable)application).DisposeAsync();
        await ((IAsyncDisposable)application).DisposeAsync();

        owned.DisposeCount.ShouldBe(1);
        ownedServer.DisposeCount.ShouldBe(1);
        ownedService.DisposeCount.ShouldBe(1);
        borrowed.DisposeCount.ShouldBe(0);
        borrowedServer.DisposeCount.ShouldBe(0);
        borrowedService.DisposeCount.ShouldBe(0);
        configuration.DisposeCount.ShouldBe(1);
    }

    /// <summary>Verifies built registries retain snapshots even if legacy options or fake engines mutate.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Context: nested servers and option lists are frozen snapshots")]
    public async Task Build_WhenRetainedInputsMutate_ShouldPreserveRuntimeRegistries()
    {
        var log = new List<string>();
        var engine = new RecordingEngine("built");
        var first = new RecordingServer(log, "first", engine);
        engine.AddServer(_ => first);
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine(engine);
        await using var application = builder.Build();
        engine.AddServer(owner => new RecordingServer(log, "late", owner));
        builder.Options.Engines.Add(new RecordingEngine("late"));
        builder.Options.StartServicesConcurrently = true;

        await ((IHost)application).StartAsync(DatabaseHostTestHarness.Timeout());
        await ((IHost)application).StopAsync(DatabaseHostTestHarness.Timeout());

        application.Context.Engines.ShouldBe([engine]);
        application.Context.Servers.ShouldBe([first]);
        application.Context.GetEngine("built").ShouldBeSameAs(engine);
        Should.Throw<KeyNotFoundException>(() => application.Context.GetEngine("BUILT"));
        log.ShouldBe(["first:start", "first:stop"]);
    }

    /// <summary>Verifies JSON, environment, arguments, and final provider registrations are loaded at Build.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Configuration: defaults and final services reach named engine construction")]
    public async Task Build_WithConfigurationAndServices_ShouldApplyDefaultsAndBuildTimeFactory()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("cohesion-phase29-");
        const string environmentKey = "COHESION_CONFIG__Phase29__Environment";
        string? oldValue = Environment.GetEnvironmentVariable(environmentKey);
        try
        {
            File.WriteAllText(System.IO.Path.Combine(directory.FullName, "appsettings.json"),
                """{"Phase29":{"Json":"base","Environment":"json","Arguments":"json"}}""");
            File.WriteAllText(System.IO.Path.Combine(directory.FullName, "appsettings.Testing.json"),
                """{"Phase29":{"Json":"environment-json"}}""");
            Environment.SetEnvironmentVariable(environmentKey, "environment");
            string[] args = ["--Phase29:Arguments=arguments"];
            var builder = DatabaseApplication.CreateBuilder(args);
            builder.Options.ContentRootPath = FileSystemPath.Parse(directory.FullName);
            builder.Options.Environment = "Testing";
            args[0] = "--Phase29:Arguments=mutated";
            var provider = new TestConfigurationProvider();
            builder.Configuration.AddProvider(_ => provider);
            builder.Services.AddSingleton(services => new Marker(services.GetRequiredService<IConfiguration>()["Phase29:Custom"]));
            DatabaseApplicationBuildContext? observed = null;
            builder.AddEngine("custom", context =>
            {
                observed = context;
                context.Services.GetRequiredService<Marker>().Value.ShouldBe("custom");
                var engine = SqlDatabaseEngine.CreateBuilder();
                engine.EngineName = context.Services.GetRequiredService<Marker>().Value;
                engine.AddServer(owner => new RecordingServer([], engine: owner));
                return engine.Build();
            });
            provider.LoadCount.ShouldBe(0);

            await using var application = builder.Build();

            provider.LoadCount.ShouldBe(1);
            application.Context.Configuration["Phase29:Json"].ShouldBe("environment-json");
            application.Context.Configuration["Phase29:Environment"].ShouldBe("environment");
            application.Context.Configuration["Phase29:Arguments"].ShouldBe("arguments");
            observed.ShouldNotBeNull().Configuration.ShouldBeSameAs(application.Context.Configuration);
            observed.Services.ShouldBeSameAs(application.Context.Services);
            application.Context.Services.GetRequiredService<IConfiguration>().ShouldBeSameAs(application.Context.Configuration);
            application.Context.GetEngine("custom").ShouldBeOfType<SqlDatabaseEngine>();
            application.Context.Servers.ShouldHaveSingleItem().Context.Engine
                .ShouldBeSameAs(application.Context.GetEngine("custom"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentKey, oldValue);
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Verifies hosting rejects reflective activation and open generic service descriptors.</summary>
    [Theory(DisplayName = "Cohesion Test [Database.Hosting] - Services: rejects constructor activation and open generic registrations")]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_WithUnsupportedServiceDescriptors_ShouldRejectActivation(bool openGeneric)
    {
        var builder = DatabaseApplication.CreateBuilder();
        builder.Services.Add(openGeneric
            ? new ServiceDescriptor(typeof(List<>), _ => new object(), ServiceLifetime.Transient)
            : new ServiceDescriptor(typeof(Marker), typeof(Marker), ServiceLifetime.Transient));

        Should.Throw<InvalidOperationException>(() => builder.Build());
    }

    /// <summary>Verifies the hosting provider enforces scopes and remains independent per application.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Services: validates scopes and isolates each application")]
    public async Task Build_WithScopedServices_ShouldRejectRootResolutionAndIsolateProviders()
    {
        var first = DatabaseApplication.CreateBuilder();
        var second = DatabaseApplication.CreateBuilder();
        first.Services.AddScoped(_ => new Marker());
        second.Services.AddScoped(_ => new Marker());
        await using var firstApplication = first.Build();
        await using var secondApplication = second.Build();

        Should.Throw<InvalidOperationException>(() => firstApplication.Context.Services.GetRequiredService<Marker>());
        using var firstScope = firstApplication.Context.Services.CreateScope();
        using var secondScope = secondApplication.Context.Services.CreateScope();
        firstScope.ServiceProvider.GetRequiredService<Marker>()
            .ShouldNotBeSameAs(secondScope.ServiceProvider.GetRequiredService<Marker>());
        firstApplication.Context.Configuration.ShouldNotBeSameAs(secondApplication.Context.Configuration);
    }

    /// <summary>Verifies strict hosting emits no compilation diagnostics on a JIT runtime.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Services: repeated JIT resolution emits no dynamic code")]
    public async Task Resolve_OnJit_ShouldKeepHostingProviderInterpreted()
    {
        if (!RuntimeFeature.IsDynamicCodeCompiled)
        {
            return;
        }
        using var listener = new CompilationListener();
        using var controlBuilder = new ServiceProviderBuilder();
        controlBuilder.AddTransient(_ => new Marker());
        using var control = (ServiceProvider)((IServiceProviderBuilder)controlBuilder).Build();
        var builder = DatabaseApplication.CreateBuilder();
        builder.Services.AddTransient(_ => new Marker());
        await using var application = builder.Build();
        for (int index = 0; index < 128; index++)
        {
            application.Context.Services.GetRequiredService<Marker>().ShouldNotBeNull();
            control.GetRequiredService<Marker>().ShouldNotBeNull();
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (listener.Count(control) == 0)
        {
            await Task.Delay(10, timeout.Token);
        }

        listener.Count(application.Context.Services).ShouldBe(0);
    }

    private sealed record Marker(string? Value = null);

    private sealed class DisposableService : IHostService, IAsyncDisposable
    {
        internal int DisposeCount { get; private set; }
        public ServiceId Id { get; } = ServiceId.New();
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
    }

    private sealed class ServerService(IDatabaseEngine engine) : IDatabaseServer, IHostService
    {
        private readonly RecordingServer _server = new([], engine: engine);
        public ServiceId Id { get; } = ServiceId.New();
        public IDatabaseServerContext Context => _server.Context;
        public Task StartAsync(CancellationToken cancellationToken = default) => _server.StartAsync(cancellationToken);
        public Task StopAsync(CancellationToken cancellationToken = default) => _server.StopAsync(cancellationToken);
        public ValueTask DisposeAsync() => _server.DisposeAsync();
    }

    private sealed class TestConfigurationProvider : ConfigurationProvider
    {
        internal int LoadCount { get; private set; }
        internal int DisposeCount { get; private set; }
        public override string Name => "phase29";
        protected override Task OnLoadAsync(
            IDictionary<Assimalign.Cohesion.Configuration.Path, string?> entries,
            CancellationToken cancellationToken = default)
        {
            LoadCount++;
            entries["Phase29:Custom"] = "custom";
            return Task.CompletedTask;
        }

        protected override ValueTask OnDisposeAsync(IEnumerable<IConfigurationEntry> entries)
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CompilationListener : EventListener
    {
        private readonly ConcurrentDictionary<int, int> _compilations = new();

        internal int Count(IServiceProvider provider) => _compilations.GetValueOrDefault(provider.GetHashCode());

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "Assimalign-Cohesion-DependencyInjection")
            {
                EnableEvents(eventSource, EventLevel.Verbose, EventKeywords.All);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventId is 3 or 4 && eventData.Payload is { Count: >= 3 } payload && payload[2] is int providerId)
            {
                _compilations?.AddOrUpdate(providerId, 1, (_, count) => count + 1);
            }
        }
    }
}
