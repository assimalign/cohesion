using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Sql.Schema;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

/// <summary>
/// Tests for the application builder — the area's instance of the cross-area
/// builder pattern: engine factories register against the root's
/// <c>IDatabaseApplicationBuilder</c> seam, nested servers are flattened at Build, and the built
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

        // Act
        await using IDatabaseApplication application = builder.Build();
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
        var engine = new RecordingEngine();
        engine.AddServer(owner => new RecordingServer(log, "server", owner));
        DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder();

        // Act
        DatabaseApplicationBuilder returnedBuilder = builder.AddService(service);
        builder.AddEngine(engine);
        await using IDatabaseApplication application = builder.Build();
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
        engine.AddServer(_ => server);
        builder.AddService(firstService);
        builder.AddService(context =>
        {
            factoryCalls++;
            observedContext = context;
            return secondService;
        });
        builder.AddEngine(engine);

        // Act
        await using IDatabaseApplication application = builder.Build();
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

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Builder: Deferred engine factory nests its endpoint and observes preceding engines")]
    public async Task Build_WithDeferredServerFactory_ShouldComposeEndpointOverContext()
    {
        // Arrange: engine factories, registered by name through the root seam the model verbs
        // call, observe earlier registrations and construct servers only after their own engine
        // exists.
        var log = new List<string>();
        var engine = new RecordingEngine();
        RecordingServer? server = null;
        IReadOnlyList<DatabaseEngine>? observedEngines = null;

        DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine(engine);
        ((IDatabaseApplicationBuilder)builder).AddEngine("endpoint-engine", context =>
        {
            observedEngines = [.. context.Engines];
            var endpointEngine = new RecordingEngine("endpoint-engine");
            endpointEngine.AddServer(owner => server = new RecordingServer(log, "deferred", owner));
            return endpointEngine;
        });
        server.ShouldBeNull();

        // Act
        await using DatabaseApplication application = builder.Build();
        await ((IHost)application).StartAsync(DatabaseHostTestHarness.Timeout());
        await ((IHost)application).StopAsync(DatabaseHostTestHarness.Timeout());

        // Assert: the factory saw the registered engine through the context, and
        // the produced server ran as the endpoint.
        observedEngines.ShouldNotBeNull();
        observedEngines.ShouldHaveSingleItem().ShouldBeSameAs(engine);
        application.Context.Servers.ShouldHaveSingleItem().ShouldBeSameAs(server);
        log.ShouldBe(["deferred:start", "deferred:stop"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Builder: Nested servers flatten in engine and server registration order")]
    public async Task Build_WithMultipleNestedServers_ShouldComposeAllInOrder()
    {
        // Arrange: servers belong to the same engine; the application discovers
        // their fixed order when the engine factory returns.
        var log = new List<string>();
        RecordingServer? first = null;
        RecordingServer? second = null;

        DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine("recording-engine", _ =>
        {
            var engine = new RecordingEngine();
            engine.AddServer(owner => first = new RecordingServer(log, "first", owner));
            engine.AddServer(owner => second = new RecordingServer(log, "second", owner));
            return engine;
        });

        // Act
        await using DatabaseApplication application = builder.Build();
        await ((IHost)application).StartAsync(DatabaseHostTestHarness.Timeout());
        await ((IHost)application).StopAsync(DatabaseHostTestHarness.Timeout());

        // Assert: nested server order is stable and stop drains in reverse.
        application.Context.Servers.Count.ShouldBe(2);
        application.Context.Servers[0].ShouldBeSameAs(first);
        application.Context.Servers[1].ShouldBeSameAs(second);
        log.ShouldBe(["first:start", "second:start", "second:stop", "first:stop"]);
    }

    /// <summary>
    /// The engine's own Build provisions the databases it declares, inside the application's Build
    /// (owner decision 49 of 2026-10-09), and servers start only when the application starts: so
    /// provisioning precedes accept (area DESIGN, R4) with no schema knowledge in the hosting layer.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Provisioning: a declared database is provisioned by Build, before any server accepts")]
    public async Task AddSql_DeclaredDatabase_ShouldBeProvisionedByBuildBeforeAnyServerStarts()
    {
        // Arrange
        var log = new List<string>();
        RecordingServer? server = null;
        DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder();
        builder.AddSql("orders-sql", sql =>
        {
            sql.AddServer(engine => server = new RecordingServer(log, "server", engine));
            sql.AddDatabase("orders", database => database.Schema(schema =>
                schema.Table<Order>("orders", table => table.Key(order => order.Id))));
        });

        // Act
        await using DatabaseApplication application = builder.Build();
        string[] loggedAtBuild = [.. log];
        var engine = application.Context.GetEngine<SqlDatabaseEngine>("orders-sql");
        engine.TryGetDatabase("orders", out SqlDatabase? orders).ShouldBeTrue();
        await using (var session = await orders!.CreateSessionAsync())
        {
            await session.ExecuteAsync("INSERT INTO orders VALUES (1)");
        }

        await ((IHost)application).StartAsync(DatabaseHostTestHarness.Timeout());
        await ((IHost)application).StopAsync(DatabaseHostTestHarness.Timeout());

        // Assert: the table existed when Build returned, and no server had started.
        server.ShouldNotBeNull().Engine.ShouldBeSameAs(engine);
        loggedAtBuild.ShouldBeEmpty();
        log.ShouldBe(["server:start", "server:stop"]);
    }

    /// <summary>
    /// A declaration the engine refuses fails the application's Build, and the application
    /// disposes what it had built before it.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Provisioning: a refused declaration fails Build and disposes the engines built before it")]
    public void AddSql_DeclarationRefused_ShouldFailBuildAndDisposeEarlierEngines()
    {
        // Arrange
        var earlier = new RecordingEngine("earlier");
        DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine("earlier", _ => earlier);
        builder.AddSql("refusing", sql => sql.AddDatabase("orders", database => database.Schema(schema =>
        {
            schema.Table<Order>("orders", table => table.Key(order => order.Id));
            schema.Principal("reader", principal => principal.Grant(SqlPermission.Read, "orders"));
        })));

        // Act
        var failure = Should.Throw<SqlSchemaMigrationException>(() => builder.Build());

        // Assert
        failure.Message.ShouldStartWith("COHSQLP001: SQL engine 'refusing' cannot provision database 'orders'", Case.Sensitive);
        earlier.DisposeCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Builder: Building twice is rejected")]
    public async Task Build_WhenAlreadyBuilt_ShouldThrow()
    {
        // Arrange
        DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine(new RecordingEngine());
        await using var application = builder.Build();

        // Act + Assert
        Should.Throw<InvalidOperationException>(() => builder.Build());
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Builder: A deferred factory returning null is rejected at build")]
    public void Build_WhenDeferredFactoryReturnsNull_ShouldThrow()
    {
        // Arrange
        DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine("null", _ => null!);

        // Act + Assert
        Should.Throw<InvalidOperationException>(() => builder.Build());
    }

    private sealed record Order(int Id);
}
