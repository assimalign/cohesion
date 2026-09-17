using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

/// <summary>
/// Tests for the application builder — the area's instance of the cross-area
/// builder pattern: engines, lifecycle services, and servers register against the root's
/// <c>IDatabaseApplicationBuilder</c> seam, a deferred server factory receives the
/// application context at build (the Web shape), and the built
/// <c>IDatabaseApplication</c> exposes the composition through its context.
/// </summary>
public class DatabaseApplicationBuilderTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Builder: Built application exposes the registered engine through its context")]
    public async Task Build_WithRegisteredEngine_ShouldExposeEngineOnContext()
    {
        // Arrange: register through the ROOT interface, the seam model verbs use.
        var engine = new RecordingEngine();
        IDatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder();

        builder.AddEngine(engine);
        builder.Engines.ShouldHaveSingleItem();

        // Act
        IDatabaseApplication application = builder.Build();
        await application.StartAsync(DatabaseHostTestHarness.Timeout());

        // Assert: the context carries the server-less registration; the engine is a
        // data machine the application does not drive (no lifecycle calls to fake).
        application.Context.Engines.ShouldHaveSingleItem().ShouldBeSameAs(engine);
        application.Context.Servers.ShouldBeEmpty();
        engine.State.ShouldBe(EngineState.Running);

        await application.StopAsync(DatabaseHostTestHarness.Timeout());
        engine.State.ShouldBe(EngineState.Running);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - AddService: an instance registered through the concrete builder runs before the server")]
    public async Task AddService_WithInstance_RegistersThroughConcreteBuilderAndRunsBeforeServer()
    {
        // Arrange
        var log = new List<string>();
        var service = new RecordingService(log, "service");
        var server = new RecordingServer(log, "server");
        DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder();

        // Act
        DatabaseApplicationBuilder returnedBuilder = builder.AddService(service);
        builder.AddServer(server);
        IDatabaseApplication application = builder.Build();
        await application.StartAsync(DatabaseHostTestHarness.Timeout());
        await application.StopAsync(DatabaseHostTestHarness.Timeout());

        // Assert
        returnedBuilder.ShouldBeSameAs(builder);
        log.ShouldBe(["service:start", "server:start", "server:stop", "service:stop"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - AddService: a context factory resolves once and preserves service lifecycle order")]
    public async Task AddService_WithContextFactory_ReceivesFinalContextOnceAndPreservesRegistrationOrder()
    {
        // Arrange: register the server first to prove service/server grouping is
        // independent of fluent call order, while service order remains faithful.
        var log = new List<string>();
        var engine = new RecordingEngine();
        var firstService = new RecordingService(log, "first-service");
        var secondService = new RecordingService(log, "second-service");
        var server = new RecordingServer(log, "server", engine);
        IDatabaseApplicationContext? observedContext = null;
        int factoryCalls = 0;
        DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder();
        builder.AddServer(server);
        builder.AddService(firstService);
        builder.AddService(context =>
        {
            factoryCalls++;
            observedContext = context;
            return secondService;
        });
        builder.AddEngine(engine);

        // Act
        IDatabaseApplication application = builder.Build();
        await application.StartAsync(DatabaseHostTestHarness.Timeout());
        await application.StopAsync(DatabaseHostTestHarness.Timeout());

        // Assert
        factoryCalls.ShouldBe(1);
        IDatabaseApplicationContext actualContext = observedContext.ShouldNotBeNull();
        actualContext.ShouldBeSameAs(application.Context);
        actualContext.Engines.ShouldHaveSingleItem().ShouldBeSameAs(engine);
        actualContext.Servers.ShouldHaveSingleItem().ShouldBeSameAs(server);
        log.ShouldBe([
            "first-service:start",
            "second-service:start",
            "server:start",
            "server:stop",
            "second-service:stop",
            "first-service:stop"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Builder: Deferred server factory receives the context with the final engine list")]
    public async Task Build_WithDeferredServerFactory_ShouldComposeEndpointOverContext()
    {
        // Arrange: the server factory is registered BEFORE the engine — it must
        // still see the full engine list, because it runs at Build with the context.
        var log = new List<string>();
        var engine = new RecordingEngine();
        var server = new RecordingServer(log, "deferred");
        IReadOnlyList<IDatabaseEngine>? observedEngines = null;

        DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder();
        builder.AddServer(context =>
        {
            observedEngines = context.Engines;
            return server;
        });
        builder.AddEngine(engine);

        // Act
        DatabaseApplication application = builder.Build();
        await ((IHost)application).StartAsync(DatabaseHostTestHarness.Timeout());
        await ((IHost)application).StopAsync(DatabaseHostTestHarness.Timeout());

        // Assert: the factory saw the registered engine through the context, and
        // the produced server ran as the endpoint.
        observedEngines.ShouldNotBeNull();
        observedEngines.ShouldHaveSingleItem().ShouldBeSameAs(engine);
        application.Context.Servers.ShouldHaveSingleItem().ShouldBeSameAs(server);
        log.ShouldBe(["deferred:start", "deferred:stop"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Builder: Multiple servers register — one per model — and resolve in registration order")]
    public async Task AddServer_MultipleRegistrations_ShouldComposeAllInOrder()
    {
        // Arrange: an instance registration plus a deferred factory that observes
        // the server registered ahead of it through the context.
        var log = new List<string>();
        var first = new RecordingServer(log, "first");
        var second = new RecordingServer(log, "second");
        IReadOnlyList<IDatabaseServer>? observedServers = null;

        DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder();
        builder.AddServer(first);
        builder.AddServer(context =>
        {
            observedServers = [.. context.Servers];
            return second;
        });

        // Act
        DatabaseApplication application = builder.Build();
        await ((IHost)application).StartAsync(DatabaseHostTestHarness.Timeout());
        await ((IHost)application).StopAsync(DatabaseHostTestHarness.Timeout());

        // Assert: both servers composed in registration order; the deferred factory
        // saw the first server already registered; stop drains in reverse.
        observedServers.ShouldNotBeNull();
        observedServers.ShouldHaveSingleItem().ShouldBeSameAs(first);
        application.Context.Servers.Count.ShouldBe(2);
        application.Context.Servers[0].ShouldBeSameAs(first);
        application.Context.Servers[1].ShouldBeSameAs(second);
        log.ShouldBe(["first:start", "second:start", "second:stop", "first:stop"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Provision: completes before server accept regardless of verb order")]
    public async Task Provision_WhenRegisteredAfterServer_ShouldCompleteBeforeServerStarts()
    {
        // Arrange: call the server verb first to prove composition-call order cannot move
        // provisioning behind endpoint accept.
        var log = new List<string>();
        var engine = new ProvisioningEngine(log);
        var server = new RecordingServer(log, "server", engine);
        DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder();
        builder.AddServer(server);
        CompiledSchema schema = CompileSchema("app");
        builder.Provision(engine, schema);

        // Act
        await using DatabaseApplication application = builder.Build();
        await ((IHost)application).StartAsync(DatabaseHostTestHarness.Timeout());
        await ((IHost)application).StopAsync(DatabaseHostTestHarness.Timeout());

        // Assert: a first launch attempts open, creates the missing database, and only then
        // starts the server. The server still drains before the provisioner stops.
        builder.Schemas.ShouldHaveSingleItem().ShouldBeSameAs(schema);
        log.ShouldBe(["engine:open", "engine:create", "engine:apply", "server:start", "server:stop"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Provision: unrelated database failures propagate without creating")]
    public async Task Provision_WhenOpenFailsForAnotherReason_ShouldPropagate()
    {
        // Arrange
        var log = new List<string>();
        var failure = new DatabaseException("The database storage could not be opened.");
        var engine = new ProvisioningEngine(log, openException: failure);
        DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder();
        builder.Provision(engine, CompileSchema("app"));

        // Act
        await using DatabaseApplication application = builder.Build();
        DatabaseException actual = await Should.ThrowAsync<DatabaseException>(async () =>
            await ((IHost)application).StartAsync(DatabaseHostTestHarness.Timeout()));

        // Assert
        actual.ShouldBeSameAs(failure);
        log.ShouldBe(["engine:open"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - AddDatabase: retains code-first schema and provisions it before accept")]
    public async Task AddDatabase_WithSchema_ShouldRetainAndProvisionBeforeServerStarts()
    {
        // Arrange
        var log = new List<string>();
        var engine = new ProvisioningEngine(log);
        var server = new RecordingServer(log, "server", engine);
        DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder();
        builder.AddServer(server);

        // Act
        CompiledSchema schema = builder.AddDatabase(engine, "orders", database =>
            database.Table<Order>("orders", table => table.Key(order => order.Id)));
        await using DatabaseApplication application = builder.Build();
        await ((IHost)application).StartAsync(DatabaseHostTestHarness.Timeout());
        await ((IHost)application).StopAsync(DatabaseHostTestHarness.Timeout());

        // Assert
        schema.Name.ShouldBe("orders");
        schema.Tables.ShouldHaveSingleItem().RowType.ShouldBe(
            $"{typeof(Order).Assembly.GetName().Name}:{typeof(Order).FullName}");
        builder.Schemas.ShouldHaveSingleItem().ShouldBeSameAs(schema);
        log.ShouldBe(["engine:open", "engine:create", "engine:apply", "server:start", "server:stop"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Builder: Building twice is rejected")]
    public void Build_WhenAlreadyBuilt_ShouldThrow()
    {
        // Arrange
        DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine(new RecordingEngine());
        builder.Build();

        // Act + Assert
        Should.Throw<InvalidOperationException>(() => builder.Build());
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Builder: A deferred factory returning null is rejected at build")]
    public void Build_WhenDeferredFactoryReturnsNull_ShouldThrow()
    {
        // Arrange
        DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder();
        builder.AddServer(_ => null!);

        // Act + Assert
        Should.Throw<InvalidOperationException>(() => builder.Build());
    }

    private sealed record Order(int Id);

    private static CompiledSchema CompileSchema(string name)
        => DatabaseSchemaCompiler.Compile(DatabaseSchema.Create(name, database =>
            database.Table<Order>("orders", table => table.Key(order => order.Id))), EngineModel.Sql);
}
