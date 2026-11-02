using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

/// <summary>Verifies nested endpoint lifecycle and complete host-run integration.</summary>
public sealed class DatabaseApplicationLifetimeTests
{
    /// <summary>Verifies a listenerless application's run waits for a shutdown request.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Run: listenerless application waits for shutdown")]
    public async Task Run_WithoutServers_ShouldWaitForShutdown()
    {
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine("recording-engine", _ => new RecordingEngine());
        await using var application = builder.Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        Task running = application.RunAsync(timeout.Token);
        while (application.Context.State != HostState.Started)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }

        running.IsCompleted.ShouldBeFalse();
        application.Context.Servers.ShouldBeEmpty();
        application.Context.Shutdown();
        await running;
        application.Context.State.ShouldBe(HostState.Stopped);
    }

    /// <summary>Verifies engines remain usable after stop but endpoints cannot be restarted.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Lifecycle: nested servers run once and restart is rejected")]
    public async Task Start_AfterStop_ShouldRejectRestartWithoutRestartingServers()
    {
        var log = new List<string>();
        var engine = new RecordingEngine();
        engine.AddServer(owner => new RecordingServer(log, "nested", owner));
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine(engine.Name, _ => engine);
        await using var application = builder.Build();

        await ((IHost)application).StartAsync(DatabaseHostTestHarness.Timeout());
        await ((IHost)application).StopAsync(DatabaseHostTestHarness.Timeout());
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await ((IHost)application).StartAsync(DatabaseHostTestHarness.Timeout()));

        engine.State.ShouldBe(EngineState.Running);
        log.ShouldBe(["nested:start", "nested:stop"]);
    }

    /// <summary>Verifies failed startup rolls back prior services and consumes the start lifecycle.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Lifecycle: nested startup failure rolls back and prevents retry")]
    public async Task Start_WhenNestedServerFails_ShouldRollbackAndRejectRetry()
    {
        var log = new List<string>();
        var failure = new InvalidOperationException("listener failed");
        var engine = new RecordingEngine();
        engine.AddServer(owner => new RecordingServer(log, "first", owner));
        engine.AddServer(owner => new RecordingServer(log, "failing", owner) { StartException = failure });
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddService(new RecordingService(log, "service"));
        builder.AddEngine(engine.Name, _ => engine);
        await using var application = builder.Build();

        var actual = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await ((IHost)application).StartAsync(DatabaseHostTestHarness.Timeout()));

        // The host rolls back every service it started, the failed server included, but a failed
        // start is terminal on the server base: the failing server released what its start held,
        // so its stop reaches no core (concrete-types plan, phase 6).
        actual.ShouldBeSameAs(failure);
        log.ShouldBe(["service:start", "first:start", "failing:start", "first:stop", "service:stop"]);
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await ((IHost)application).StartAsync(DatabaseHostTestHarness.Timeout()));
    }

    /// <summary>Verifies stop errors do not prevent cleanup of independent owned roots.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Dispose: stop failure still disposes all owned engines")]
    public async Task Dispose_WhenServerStopFails_ShouldDisposeIndependentOwnedEngines()
    {
        var first = new RecordingEngine("first");
        var second = new RecordingEngine("second");
        var server = new RecordingServer([], engine: first)
        {
            StopException = new InvalidOperationException("stop failed"),
        };
        first.AddServer(_ => server);
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine(first.Name, _ => first);
        builder.AddEngine(second.Name, _ => second);
        var application = builder.Build();
        await ((IHost)application).StartAsync(DatabaseHostTestHarness.Timeout());

        await Should.ThrowAsync<AggregateException>(async () => await ((IAsyncDisposable)application).DisposeAsync());

        // The host's stop ran the server's stop core, which failed; the engine's disposal of the
        // stopped server then finds nothing left to stop.
        first.DisposeCount.ShouldBe(1);
        second.DisposeCount.ShouldBe(1);
        server.Stops.ShouldBe(1);
    }
}
