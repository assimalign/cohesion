using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

/// <summary>
/// Tests for the application builder — the area's instance of the cross-area
/// builder pattern: engines register against the root's
/// <c>IDatabaseApplicationBuilder</c> seam, a deferred server factory receives the
/// final engine list at build, and the built <c>IDatabaseApplication</c> serves
/// the registered engines on the standard start/stop lifecycle.
/// </summary>
public class DatabaseApplicationBuilderTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Builder: Built application serves the registered engine across start and stop")]
    public async Task Build_WithRegisteredEngine_ShouldServeEngineLifecycle()
    {
        // Arrange: register through the ROOT interface, the seam model verbs use.
        var log = new List<string>();
        var engine = new RecordingEngine(log);
        IDatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder();

        builder.AddEngine(engine);
        builder.Engines.ShouldHaveSingleItem();

        // Act
        IDatabaseApplication application = builder.Build();
        await application.StartAsync(DatabaseHostTestHarness.Timeout());

        // Assert: the application serves the engine and drives its lifecycle.
        application.Engines.ShouldHaveSingleItem();
        application.Engines[0].ShouldBeSameAs(engine);
        engine.State.ShouldBe(EngineState.Running);

        await application.StopAsync(DatabaseHostTestHarness.Timeout());
        engine.State.ShouldBe(EngineState.Stopped);
        log.ShouldBe(["engine:start", "engine:stop"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Builder: Deferred server factory receives the final engine list and runs as the endpoint")]
    public async Task Build_WithDeferredServerFactory_ShouldComposeEndpointOverRegisteredEngines()
    {
        // Arrange: the server factory is registered BEFORE the engine — it must
        // still see the full engine list, because it runs at Build.
        var log = new List<string>();
        var engine = new RecordingEngine(log);
        var server = new RecordingServer(log);
        IReadOnlyList<IDatabaseEngine>? observedEngines = null;

        DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder();
        builder.AddServer(engines =>
        {
            observedEngines = engines;
            return server;
        });
        builder.AddEngine(engine);

        // Act
        DatabaseApplication application = builder.Build();
        await ((IHost)application).StartAsync(DatabaseHostTestHarness.Timeout());
        await ((IHost)application).StopAsync(DatabaseHostTestHarness.Timeout());

        // Assert: the factory saw the registered engine, and the endpoint started
        // last / drained first around the engine lifecycle.
        observedEngines.ShouldNotBeNull();
        observedEngines.ShouldHaveSingleItem();
        observedEngines[0].ShouldBeSameAs(engine);
        log.ShouldBe(["engine:start", "server:start", "server:stop", "engine:stop"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Builder: Second server registration is rejected")]
    public void AddServer_WhenServerAlreadyRegistered_ShouldThrow()
    {
        // Arrange
        var log = new List<string>();
        DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder();
        builder.AddServer(new RecordingServer(log));

        // Act + Assert
        Should.Throw<InvalidOperationException>(() => builder.AddServer(_ => new RecordingServer(log)));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Builder: Building twice is rejected")]
    public void Build_WhenAlreadyBuilt_ShouldThrow()
    {
        // Arrange
        DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine(new RecordingEngine(new List<string>()));
        builder.Build();

        // Act + Assert
        Should.Throw<InvalidOperationException>(() => builder.Build());
    }
}
