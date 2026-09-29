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
    /// <summary>Verifies a listenerless application waits for shutdown through the host runner seam.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Run: listenerless application waits through IHostRunner")]
    public async Task Run_WithoutServers_ShouldWaitForShutdownThroughRunner()
    {
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine(_ => new RecordingEngine());
        await using var application = builder.Build();
        var runner = new RecordingRunner();
        application.Context.Runner = runner;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        Task running = application.RunAsync(timeout.Token);
        await runner.StartedSignal.Task.WaitAsync(timeout.Token);

        running.IsCompleted.ShouldBeFalse();
        runner.RunCount.ShouldBe(1);
        runner.Run.ShouldNotBeNull().Host.ShouldBeSameAs(application);
        application.Context.Servers.ShouldBeEmpty();
        runner.Run.TryShutdown().ShouldBeTrue();
        await running;
        application.Context.State.ShouldBe(HostState.Stopped);
        runner.Events.ShouldBe(["started", "stopping", "stopped"]);
    }

    /// <summary>Verifies engines remain usable after stop but endpoints cannot be restarted.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Lifecycle: nested servers run once and restart is rejected")]
    public async Task Start_AfterStop_ShouldRejectRestartWithoutRestartingServers()
    {
        var log = new List<string>();
        var engine = new RecordingEngine();
        engine.AddServer(owner => new RecordingServer(log, "nested", owner));
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine(_ => engine);
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
        builder.AddEngine(_ => engine);
        await using var application = builder.Build();

        var actual = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await ((IHost)application).StartAsync(DatabaseHostTestHarness.Timeout()));

        actual.ShouldBeSameAs(failure);
        log.ShouldBe(["service:start", "first:start", "failing:start", "failing:stop", "first:stop", "service:stop"]);
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
        builder.AddEngine(_ => first);
        builder.AddEngine(_ => second);
        var application = builder.Build();
        await ((IHost)application).StartAsync(DatabaseHostTestHarness.Timeout());

        await Should.ThrowAsync<AggregateException>(async () => await ((IAsyncDisposable)application).DisposeAsync());

        first.DisposeCount.ShouldBe(1);
        second.DisposeCount.ShouldBe(1);
        server.DisposeCount.ShouldBe(1);
    }

    private sealed class RecordingRunner : IHostRunner, IHostRunObserver
    {
        internal TaskCompletionSource<bool> StartedSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<string> Events { get; } = [];
        internal IHostRun? Run { get; private set; }
        internal int RunCount { get; private set; }

        public Task RunAsync(IHostRun run, CancellationToken cancellationToken = default)
        {
            Run = run;
            RunCount++;
            return run.RunAsync(this, cancellationToken);
        }

        public void Started(IHost host) { Events.Add("started"); StartedSignal.TrySetResult(true); }
        public void Stopping(IHost host) => Events.Add("stopping");
        public void DrainAborted(IHost host) => Events.Add("drain-aborted");
        public void Stopped(IHost host) => Events.Add("stopped");
    }
}
