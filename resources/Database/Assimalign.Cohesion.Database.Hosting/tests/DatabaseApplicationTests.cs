using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Sql.Client;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

/// <summary>
/// Tests for the composition-only database host: the application wraps registered
/// servers as endpoint services (started last, drained first), serves a query
/// end-to-end through a composed per-model server, and drains on stop.
/// </summary>
public class DatabaseApplicationTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Lifecycle: concurrent service start is rejected to preserve provisioning before accept")]
    public void Application_WithConcurrentStart_ShouldRejectUnorderedLifecycle()
    {
        var options = new DatabaseApplicationOptions
        {
            StartServicesConcurrently = true,
        };

        Should.Throw<InvalidOperationException>(() => new DatabaseApplicationBuilder(options).Build())
            .Message.ShouldContain("sequential service start and stop");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Lifecycle: concurrent service stop is rejected to preserve server drain order")]
    public void Application_WithConcurrentStop_ShouldRejectUnorderedLifecycle()
    {
        var options = new DatabaseApplicationOptions
        {
            StopServicesConcurrently = true,
        };

        Should.Throw<InvalidOperationException>(() => new DatabaseApplicationBuilder(options).Build())
            .Message.ShouldContain("sequential service start and stop");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Lifecycle: post-build option mutation cannot bypass sequential startup")]
    public async Task Application_WhenConcurrencyIsEnabledAfterBuild_ShouldStillStartSequentially()
    {
        var serviceStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseService = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new RecordingEngine();
        engine.AddServer(owner => new ControlledStartServer(
            owner,
            serverStarted,
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)));
        var builder = new DatabaseApplicationBuilder(new DatabaseApplicationOptions());
        builder.AddService(new ControlledStartService(serviceStarted, releaseService));
        builder.AddEngine(engine);
        var application = builder.Build();
        builder.Options.StartServicesConcurrently = true;

        Task start = ((IHost)application).StartAsync(DatabaseHostTestHarness.Timeout());
        await serviceStarted.Task.WaitAsync(DatabaseHostTestHarness.Timeout());

        serverStarted.Task.IsCompleted.ShouldBeFalse();

        releaseService.TrySetResult(true);
        await serverStarted.Task.WaitAsync(DatabaseHostTestHarness.Timeout());
        ((ControlledStartServer)application.Context.Servers[0]).Accept();
        await start;
        await ((IHost)application).StopAsync(DatabaseHostTestHarness.Timeout());
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Context: a server attached after build cannot change hosted registries")]
    public async Task Application_WhenRegistriesAreMutatedAfterBuild_ShouldRetainBuiltSnapshot()
    {
        var log = new List<string>();
        var registeredEngine = new RecordingEngine();
        RecordingServer? registeredServer = null;
        registeredEngine.AddServer(owner => registeredServer = new RecordingServer(log, "registered", owner));
        var builder = new DatabaseApplicationBuilder(new DatabaseApplicationOptions());
        builder.AddEngine(registeredEngine);
        var application = builder.Build();

        registeredEngine.AddServer(owner => new RecordingServer(log, "late", owner));
        Should.Throw<InvalidOperationException>(() => builder.AddEngine(new RecordingEngine("late")));

        application.Context.Engines.ShouldBe([registeredEngine]);
        application.Context.Servers.ShouldBe([registeredServer.ShouldNotBeNull()]);

        await ((IHost)application).StartAsync(DatabaseHostTestHarness.Timeout());
        await ((IHost)application).StopAsync(DatabaseHostTestHarness.Timeout());

        log.ShouldBe(["registered:start", "registered:stop"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Host: a started host serves a query over the composed endpoint")]
    public async Task StartHost_WithEndpointService_ShouldServeQueryEndToEnd()
    {
        // Arrange
        await using var harness = await DatabaseHostTestHarness.CreateAsync();
        await harness.StartHostAsync(DatabaseHostTestHarness.Timeout());

        // Act: the host started the wire server; drive a served round-trip through the client
        await using var connection = await harness.Client.RentAsync(DatabaseHostTestHarness.Timeout());
        DatabaseClientResult result = await connection.ExecuteAsync("SELECT id, name FROM users ORDER BY id", cancellationToken: DatabaseHostTestHarness.Timeout());

        // Assert
        harness.Application.Context.State.ShouldBe(HostState.Started);
        result.Rows.Count.ShouldBe(2);
        result.Rows[0].ShouldBe([1, "ada"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Composition: the application is composition-only — its reopen service, then one endpoint service per server, nothing else")]
    public async Task Application_Defaults_ShouldComposeOneEndpointServicePerServer()
    {
        // Arrange: the harness composes one SQL server; the engine takes no part in
        // the host lifecycle (it is a data machine, operational from creation).
        await using var harness = await DatabaseHostTestHarness.CreateAsync();

        // Assert: exactly two hosted services — the module's own reopen service (owner decision
        // 22), started before the servers, and the endpoint wrapper for the server.
        var services = new List<IHostService>(harness.Application.Context.HostedServices);

        services.Count.ShouldBe(2);
        services[0].ShouldBeSameAs(harness.Application.Context.ReopenService);
        harness.Application.Context.Servers.ShouldHaveSingleItem().ShouldBeSameAs(harness.Server);
        harness.Application.Context.Engines.ShouldHaveSingleItem().ShouldBeSameAs(harness.Engine);
        harness.Server.Engine.ShouldBeSameAs(harness.Engine);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Lifecycle: services start before the servers and stop after they drain")]
    public async Task Lifecycle_ServicesAndServers_ShouldStartServersLastAndStopThemFirst()
    {
        // Arrange: a recording service + two recording servers capture the relative
        // order of lifecycle calls made by the host (servers are per-model, so an
        // application may run several).
        var log = new List<string>();
        var sql = new RecordingEngine("sql");
        var documents = new RecordingEngine("documents");
        sql.AddServer(owner => new RecordingServer(log, "sql-server", owner));
        documents.AddServer(owner => new RecordingServer(log, "docs-server", owner));
        var builder = new DatabaseApplicationBuilder(new DatabaseApplicationOptions());
        builder.AddService(new RecordingService(log, "provisioner"));
        builder.AddEngine(sql);
        builder.AddEngine(documents);

        var application = builder.Build();

        // Act
        await ((IHost)application).StartAsync(DatabaseHostTestHarness.Timeout());
        await ((IHost)application).StopAsync(DatabaseHostTestHarness.Timeout());

        // Assert: the service starts ahead of both servers; both servers stop
        // (drain) before the service stops; server order follows registration.
        log.IndexOf("provisioner:start").ShouldBeLessThan(log.IndexOf("sql-server:start"));
        log.IndexOf("sql-server:start").ShouldBeLessThan(log.IndexOf("docs-server:start"));
        log.IndexOf("docs-server:stop").ShouldBeLessThan(log.IndexOf("sql-server:stop"));
        log.IndexOf("sql-server:stop").ShouldBeLessThan(log.IndexOf("provisioner:stop"));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Lifecycle: stopping the host drains and reaches the stopped state")]
    public async Task StopHost_AfterServing_ShouldDrainToStoppedState()
    {
        // Arrange
        await using var harness = await DatabaseHostTestHarness.CreateAsync();
        await harness.StartHostAsync(DatabaseHostTestHarness.Timeout());

        await using (var connection = await harness.Client.RentAsync(DatabaseHostTestHarness.Timeout()))
        {
            await connection.ExecuteAsync("SELECT id FROM users WHERE id = 1", cancellationToken: DatabaseHostTestHarness.Timeout());
        }

        // Act
        await harness.StopHostAsync(DatabaseHostTestHarness.Timeout());

        // Assert: the host drained; the engine — untouched by the host lifecycle —
        // is still a live data machine.
        harness.Application.Context.State.ShouldBe(HostState.Stopped);
        harness.Engine.State.ShouldBe(EngineState.Running);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Lifecycle: host startup waits until every server is accepting")]
    public async Task StartHost_WhileServerBindIsPending_ShouldRemainStarting()
    {
        var bindStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new RecordingEngine();
        engine.AddServer(owner => new ControlledStartServer(owner, bindStarted, accepting));
        var builder = new DatabaseApplicationBuilder(new DatabaseApplicationOptions());
        builder.AddEngine(engine);
        var application = builder.Build();

        Task start = ((IHost)application).StartAsync(DatabaseHostTestHarness.Timeout());
        await bindStarted.Task.WaitAsync(DatabaseHostTestHarness.Timeout());

        start.IsCompleted.ShouldBeFalse();
        application.Context.State.ShouldBe(HostState.Starting);

        accepting.TrySetResult(true);
        await start;
        application.Context.State.ShouldBe(HostState.Started);

        await ((IHost)application).StopAsync(DatabaseHostTestHarness.Timeout());
    }

    private sealed class ControlledStartServer : DatabaseServer
    {
        private readonly TaskCompletionSource<bool> _bindStarted;
        private readonly TaskCompletionSource<bool> _accepting;

        /// <summary>Initializes a new instance of the <see cref="ControlledStartServer"/> class.</summary>
        /// <param name="engine">The engine the server fronts, which attaches it.</param>
        /// <param name="bindStarted">Completed when the host begins starting the server.</param>
        /// <param name="accepting">Completes the server start once it is set, signalling the server is accepting.</param>
        public ControlledStartServer(
            DatabaseEngine engine,
            TaskCompletionSource<bool> bindStarted,
            TaskCompletionSource<bool> accepting)
            : base(engine)
        {
            _bindStarted = bindStarted;
            _accepting = accepting;
        }

        public override IReadOnlyCollection<DatabaseServerSession> Sessions => [];

        internal void Accept() => _accepting.TrySetResult(true);

        protected override Task StartCoreAsync(CancellationToken cancellationToken)
        {
            _bindStarted.TrySetResult(true);
            return _accepting.Task.WaitAsync(cancellationToken);
        }

        protected override Task StopCoreAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ControlledStartService : IHostService
    {
        private readonly TaskCompletionSource<bool> _started;
        private readonly TaskCompletionSource<bool> _release;

        /// <summary>Initializes a new instance of the <see cref="ControlledStartService"/> class.</summary>
        /// <param name="started">Completed when the host begins starting the service.</param>
        /// <param name="release">Completes the service start once it is set.</param>
        public ControlledStartService(
            TaskCompletionSource<bool> started,
            TaskCompletionSource<bool> release)
        {
            _started = started;
            _release = release;
        }

        public ServiceId Id { get; } = ServiceId.New();

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            _started.TrySetResult(true);
            await _release.Task.WaitAsync(cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

}
