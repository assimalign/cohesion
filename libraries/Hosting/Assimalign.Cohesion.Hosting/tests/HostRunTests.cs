using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Hosting.Internal;

using Lifecycle = Assimalign.Cohesion.Hosting.Tests.TestLifecycleService.Lifecycle;

namespace Assimalign.Cohesion.Hosting.Tests;

public class HostRunTests
{
    private const string DisplayPrefix = "Cohesion Test [Hosting] - Host run: ";

    private static readonly Lifecycle[] _completeLifecycle =
    [
        Lifecycle.Starting,
        Lifecycle.Start,
        Lifecycle.Started,
        Lifecycle.Stopping,
        Lifecycle.Stop,
        Lifecycle.Stopped,
    ];

    [Fact(DisplayName = DisplayPrefix + "Plain run: Starts, waits for shutdown, and stops")]
    public async Task RunAsync_WhenShutdownRequested_RunsThePlainLifecycle()
    {
        // Arrange
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new TestHostOptions { ShutdownSignals = new TestShutdownSignals().Register };
        options.HostedServices.Add(new DelegateHostService(_ =>
        {
            started.TrySetResult();
            return Task.CompletedTask;
        }));
        var host = new TestHost(options);
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act
        Task run = host.RunAsync(timeoutSource.Token);
        await started.Task.WaitAsync(timeoutSource.Token);
        host.Context.Shutdown();
        await run.WaitAsync(timeoutSource.Token);

        // Assert
        host.Context.State.ShouldBe(HostState.Stopped);
    }

    [Fact(DisplayName = DisplayPrefix + "Interface host: Runs without the concrete host coordinator")]
    public async Task RunAsync_WithInterfaceOnlyHost_StartsWaitsAndStops()
    {
        // Arrange
        var host = new InterfaceOnlyHost();
        IHost interfaceHost = host;
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act
        Task run = interfaceHost.RunAsync(timeoutSource.Token);
        await WaitForStateAsync(interfaceHost, HostState.Started, timeoutSource.Token);
        interfaceHost.Context.Shutdown();
        await run.WaitAsync(timeoutSource.Token);

        // Assert
        host.StartCount.ShouldBe(1);
        host.StopCount.ShouldBe(1);
        interfaceHost.Context.State.ShouldBe(HostState.Stopped);
    }

    [Fact(DisplayName = DisplayPrefix + "Run guard: Rejects re-entry while a run is active")]
    public async Task RunAsync_WhenRunIsActive_RejectsReentry()
    {
        // Arrange
        var host = new TestHost(new TestHostOptions { ShutdownSignals = new TestShutdownSignals().Register });
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task firstRun = host.RunAsync(timeoutSource.Token);
        await WaitForStateAsync(host, HostState.Started, timeoutSource.Token);

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            () => host.RunAsync(timeoutSource.Token));

        // Assert
        exception.Message.ShouldBe("A host run is already active.");
        host.Context.Shutdown();
        await firstRun.WaitAsync(timeoutSource.Token);
    }

    [Fact(DisplayName = DisplayPrefix + "Stop join: Surfaces a direct StopAsync failure from RunAsync")]
    public async Task RunAsync_WhenDirectStopFails_SurfacesSameFailure()
    {
        // Arrange
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new TestHostOptions { ShutdownSignals = new TestShutdownSignals().Register };
        options.HostedServices.Add(new DelegateHostService(
            _ =>
            {
                started.TrySetResult();
                return Task.CompletedTask;
            },
            static _ => Task.FromException(new InvalidOperationException("direct stop failure"))));
        var host = new TestHost(options);
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task run = host.RunAsync(timeoutSource.Token);
        await started.Task.WaitAsync(timeoutSource.Token);

        // Act
        InvalidOperationException stopException = await Should.ThrowAsync<InvalidOperationException>(
            () => ((IHost)host).StopAsync(timeoutSource.Token));
        InvalidOperationException runException = await Should.ThrowAsync<InvalidOperationException>(
            () => run.WaitAsync(timeoutSource.Token));

        // Assert
        stopException.Message.ShouldBe("direct stop failure");
        runException.Message.ShouldBe("direct stop failure");
    }

    [Fact(DisplayName = DisplayPrefix + "Restart: Runs again after Stopped")]
    public async Task RunAsync_AfterStopped_RunsAgain()
    {
        // Arrange
        var signals = new TestShutdownSignals();
        var host = new TestHost(new TestHostOptions { ShutdownSignals = signals.Register });
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act
        Task firstRun = host.RunAsync(timeoutSource.Token);
        await WaitForStateAsync(host, HostState.Started, timeoutSource.Token);
        host.Context.Shutdown();
        await firstRun.WaitAsync(timeoutSource.Token);

        Task secondRun = host.RunAsync(timeoutSource.Token);
        await WaitForStateAsync(host, HostState.Started, timeoutSource.Token);
        host.Context.Shutdown();
        await secondRun.WaitAsync(timeoutSource.Token);

        // Assert
        host.Context.State.ShouldBe(HostState.Stopped);
        signals.Registrations.ShouldBe(2);
        signals.Disposals.ShouldBe(2);
    }

    [Fact(DisplayName = DisplayPrefix + "Dispose: Joins the stop already begun for an active run")]
    public async Task DisposeAsync_DuringRun_JoinsStopWithoutRetrying()
    {
        // Arrange
        var events = new List<Lifecycle>();
        var options = new TestHostOptions { ShutdownSignals = new TestShutdownSignals().Register };
        options.HostedServices.Add(new TestLifecycleService(events.Add));
        var host = new TestHost(options);
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task run = host.RunAsync(timeoutSource.Token);
        await WaitForStateAsync(host, HostState.Started, timeoutSource.Token);

        // Act
        await ((IAsyncDisposable)host).DisposeAsync();
        await run.WaitAsync(timeoutSource.Token);

        // Assert
        events.ShouldBe(_completeLifecycle);
        host.Context.State.ShouldBe(HostState.Stopped);
    }

    [Fact(DisplayName = DisplayPrefix + "Shutdown signal: Drains the run, which then returns")]
    public async Task RunAsync_WhenShutdownSignalArrives_DrainsAndReturns()
    {
        // Arrange
        var signals = new TestShutdownSignals();
        var events = new List<Lifecycle>();
        var options = new TestHostOptions { ShutdownSignals = signals.Register };
        options.HostedServices.Add(new TestLifecycleService(events.Add));
        var host = new TestHost(options);
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task run = host.RunAsync(timeoutSource.Token);
        await WaitForStateAsync(host, HostState.Started, timeoutSource.Token);

        // Act
        signals.Raise();
        await run.WaitAsync(timeoutSource.Token);

        // Assert
        events.ShouldBe(_completeLifecycle);
        host.Context.State.ShouldBe(HostState.Stopped);
        signals.Registrations.ShouldBe(1);
        signals.Disposals.ShouldBe(1);
    }

    [Fact(DisplayName = DisplayPrefix + "Shutdown signal: One during startup cancels the start")]
    public async Task RunAsync_WhenShutdownSignalArrivesDuringStartup_CancelsTheStart()
    {
        // Arrange
        var signals = new TestShutdownSignals();
        var options = new TestHostOptions { ShutdownSignals = signals.Register };
        options.HostedServices.Add(new DelegateHostService(cancellationToken =>
        {
            signals.Raise();
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }));
        var host = new TestHost(options);
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act
        Task run = host.RunAsync(timeoutSource.Token);

        // Assert
        await Should.ThrowAsync<OperationCanceledException>(() => run.WaitAsync(timeoutSource.Token));
        host.Context.State.ShouldBe(HostState.Failed);
        signals.Disposals.ShouldBe(1);
    }

    [Fact(DisplayName = DisplayPrefix + "Shutdown signal: A host started without RunAsync listens for none")]
    public async Task StartAsync_WithoutRun_RegistersNoShutdownSignals()
    {
        // Arrange
        var signals = new TestShutdownSignals();
        IHost host = new TestHost(new TestHostOptions { ShutdownSignals = signals.Register });
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act
        await host.StartAsync(timeoutSource.Token);
        await host.StopAsync(timeoutSource.Token);

        // Assert
        signals.Registrations.ShouldBe(0);
    }

    [Fact(DisplayName = DisplayPrefix + "Shutdown signal: The process's signals register and unregister")]
    public void Register_WithProcessSignals_InstallsAndRemovesHandlers()
    {
        // Act
        IDisposable registration = HostShutdownSignals.Register(static () => { });

        // Assert
        Should.NotThrow(registration.Dispose);
    }

    [Fact(DisplayName = DisplayPrefix + "Shutdown signal: A signal after its run ended is ignored")]
    public void RequestShutdown_AfterRunEnded_DoesNotThrow()
    {
        // Arrange
        var shutdownSignal = new CancellationTokenSource();
        shutdownSignal.Dispose();

        // Act & Assert
        Should.NotThrow(() => HostShutdownSignals.RequestShutdown(shutdownSignal));
    }

    private static async Task WaitForStateAsync(
        IHost host,
        HostState expected,
        CancellationToken cancellationToken)
    {
        while (host.Context.State != expected)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }
    }

    private sealed class InterfaceOnlyHost : IHost
    {
        internal InterfaceOnlyHost()
        {
            Context = new InterfaceOnlyHostContext();
        }

        public HostId Id => Context.HostId;

        public InterfaceOnlyHostContext Context { get; }

        IHostContext IHost.Context => Context;

        internal int StartCount { get; private set; }

        internal int StopCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            Context.SetState(HostState.Started);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCount++;
            Context.SetState(HostState.Stopped);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InterfaceOnlyHostContext : IHostContext
    {
        private readonly TaskCompletionSource _shutdownSource =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HostId HostId { get; } = HostId.New();

        public HostState State { get; private set; } = HostState.Idle;

        public IHostEnvironment Environment { get; } = new HostEnvironment("Test");

        public IEnumerable<IHostService> HostedServices => Array.Empty<IHostService>();

        public Task WaitForShutdownAsync(CancellationToken cancellationToken = default)
        {
            return cancellationToken.CanBeCanceled
                ? _shutdownSource.Task.WaitAsync(cancellationToken)
                : _shutdownSource.Task;
        }

        public void Shutdown()
        {
            _shutdownSource.TrySetResult();
        }

        internal void SetState(HostState state)
        {
            State = state;
            if (state is HostState.Stopping or HostState.Stopped or HostState.Failed)
            {
                _shutdownSource.TrySetResult();
            }
        }
    }
}
