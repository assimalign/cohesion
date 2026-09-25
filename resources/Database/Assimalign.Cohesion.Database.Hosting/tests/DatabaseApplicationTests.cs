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

        Should.Throw<InvalidOperationException>(() => new DatabaseApplication(options))
            .Message.ShouldContain("sequential service start and stop");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Lifecycle: concurrent service stop is rejected to preserve server drain order")]
    public void Application_WithConcurrentStop_ShouldRejectUnorderedLifecycle()
    {
        var options = new DatabaseApplicationOptions
        {
            StopServicesConcurrently = true,
        };

        Should.Throw<InvalidOperationException>(() => new DatabaseApplication(options))
            .Message.ShouldContain("sequential service start and stop");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Lifecycle: post-build option mutation cannot bypass sequential startup")]
    public async Task Application_WhenConcurrencyIsEnabledAfterBuild_ShouldStillStartSequentially()
    {
        var serviceStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseService = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new DatabaseApplicationOptions();
        options.Services.Add(new ControlledStartService(serviceStarted, releaseService));
        options.Servers.Add(new ControlledStartServer(
            serverStarted,
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)));
        var application = new DatabaseApplication(options);
        options.StartServicesConcurrently = true;

        Task start = ((IHost)application).StartAsync(DatabaseHostTestHarness.Timeout());
        await serviceStarted.Task.WaitAsync(DatabaseHostTestHarness.Timeout());

        serverStarted.Task.IsCompleted.ShouldBeFalse();

        releaseService.TrySetResult(true);
        await serverStarted.Task.WaitAsync(DatabaseHostTestHarness.Timeout());
        ((ControlledStartServer)application.Context.Servers[0]).Accept();
        await start;
        await ((IHost)application).StopAsync(DatabaseHostTestHarness.Timeout());
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Context: post-build option mutation cannot change hosted registries")]
    public async Task Application_WhenRegistriesAreMutatedAfterBuild_ShouldRetainBuiltSnapshot()
    {
        var log = new List<string>();
        var registeredEngine = new RecordingEngine();
        var registeredServer = new RecordingServer(log, "registered", registeredEngine);
        var lateEngine = new RecordingEngine();
        var lateServer = new RecordingServer(log, "late", lateEngine);
        var options = new DatabaseApplicationOptions();
        options.Engines.Add(registeredEngine);
        options.Servers.Add(registeredServer);
        var application = new DatabaseApplication(options);

        options.Engines.Add(lateEngine);
        options.Servers.Add(lateServer);

        application.Context.Engines.ShouldBe([registeredEngine]);
        application.Context.Servers.ShouldBe([registeredServer]);

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

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Composition: the application is composition-only — one endpoint service per server, nothing else")]
    public async Task Application_Defaults_ShouldComposeOneEndpointServicePerServer()
    {
        // Arrange: the harness composes one SQL server; the engine takes no part in
        // the host lifecycle (it is a data machine, operational from creation).
        await using var harness = await DatabaseHostTestHarness.CreateAsync();

        // Assert: exactly one hosted service — the endpoint wrapper for the server.
        int count = 0;
        foreach (IHostService _ in harness.Application.Context.HostedServices)
        {
            count++;
        }

        count.ShouldBe(1);
        harness.Application.Context.Servers.ShouldHaveSingleItem().ShouldBeSameAs(harness.Server);
        harness.Application.Context.Engines.ShouldHaveSingleItem().ShouldBeSameAs(harness.Engine);
        harness.Server.Context.Engine.ShouldBeSameAs(harness.Engine);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Lifecycle: services start before the servers and stop after they drain")]
    public async Task Lifecycle_ServicesAndServers_ShouldStartServersLastAndStopThemFirst()
    {
        // Arrange: a recording service + two recording servers capture the relative
        // order of lifecycle calls made by the host (servers are per-model, so an
        // application may run several).
        var log = new List<string>();
        var options = new DatabaseApplicationOptions();

        options.Services.Add(new RecordingService(log, "provisioner"));
        options.Servers.Add(new RecordingServer(log, "sql-server", new RecordingEngine("sql")));
        options.Servers.Add(new RecordingServer(log, "docs-server", new RecordingEngine("documents")));

        var application = new DatabaseApplication(options);

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
        var options = new DatabaseApplicationOptions();
        options.Servers.Add(new ControlledStartServer(bindStarted, accepting));
        var application = new DatabaseApplication(options);

        Task start = ((IHost)application).StartAsync(DatabaseHostTestHarness.Timeout());
        await bindStarted.Task.WaitAsync(DatabaseHostTestHarness.Timeout());

        start.IsCompleted.ShouldBeFalse();
        application.Context.State.ShouldBe(HostState.Starting);

        accepting.TrySetResult(true);
        await start;
        application.Context.State.ShouldBe(HostState.Started);

        await ((IHost)application).StopAsync(DatabaseHostTestHarness.Timeout());
    }

    private sealed class ControlledStartServer : IDatabaseServer
    {
        private readonly RecordingServer _inner = new([], "controlled");
        private readonly TaskCompletionSource<bool> _bindStarted;
        private readonly TaskCompletionSource<bool> _accepting;

        /// <summary>Initializes a new instance of the <see cref="ControlledStartServer"/> class.</summary>
        /// <param name="bindStarted">Completed when the host begins starting the server.</param>
        /// <param name="accepting">Completes the server start once it is set, signalling the server is accepting.</param>
        public ControlledStartServer(
            TaskCompletionSource<bool> bindStarted,
            TaskCompletionSource<bool> accepting)
        {
            _bindStarted = bindStarted;
            _accepting = accepting;
        }

        public IDatabaseServerContext Context => _inner.Context;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            _bindStarted.TrySetResult(true);
            return _accepting.Task.WaitAsync(cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        internal void Accept() => _accepting.TrySetResult(true);
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
