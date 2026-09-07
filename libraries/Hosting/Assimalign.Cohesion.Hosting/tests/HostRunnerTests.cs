using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Hosting.Tests;

public class HostRunnerTests
{
    private const string DisplayPrefix = "Cohesion Test [Hosting] - Host runner: ";

    [Fact(DisplayName = DisplayPrefix + "Plain run: Preserves the lifecycle without a runner")]
    public async Task RunAsync_WithoutRunner_PreservesPlainLifecycle()
    {
        // Arrange
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new TestHostOptions();
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
        host.Context.Runner.ShouldBeNull();
        host.Context.State.ShouldBe(HostState.Stopped);
    }

    [Fact(DisplayName = DisplayPrefix + "Delegation: Invokes the installed runner exactly once")]
    public async Task RunAsync_WithRunner_DelegatesExactlyOnce()
    {
        // Arrange
        var observer = new RecordingObserver();
        var runner = new PassthroughRunner(observer);
        var host = new TestHost(new TestHostOptions());
        host.Context.Runner = runner;
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act
        Task run = host.RunAsync(timeoutSource.Token);
        await observer.StartedSignal.Task.WaitAsync(timeoutSource.Token);
        host.Context.Shutdown();
        await run.WaitAsync(timeoutSource.Token);

        // Assert
        runner.InvocationCount.ShouldBe(1);
    }

    [Fact(DisplayName = DisplayPrefix + "Run guard: Rejects re-entry while a run is active")]
    public async Task RunAsync_WhenRunIsActive_RejectsReentry()
    {
        // Arrange
        var observer = new RecordingObserver();
        var runner = new PassthroughRunner(observer);
        var host = new TestHost(new TestHostOptions());
        host.Context.Runner = runner;
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task firstRun = host.RunAsync(timeoutSource.Token);
        await observer.StartedSignal.Task.WaitAsync(timeoutSource.Token);

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            () => host.RunAsync(timeoutSource.Token));

        // Assert
        exception.Message.ShouldBe("A host run is already active.");
        runner.InvocationCount.ShouldBe(1);
        host.Context.Shutdown();
        await firstRun.WaitAsync(timeoutSource.Token);
    }

    [Fact(DisplayName = DisplayPrefix + "Run guard: Rejects re-entry before the runner starts the handle")]
    public async Task RunAsync_WhenRunnerDelaysHandle_RejectsReentry()
    {
        // Arrange
        var observer = new RecordingObserver();
        var runner = new DelayedRunner(observer);
        var host = new TestHost(new TestHostOptions());
        host.Context.Runner = runner;
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task firstRun = host.RunAsync(timeoutSource.Token);
        await runner.Entered.Task.WaitAsync(timeoutSource.Token);

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            () => host.RunAsync(timeoutSource.Token));

        // Assert
        exception.Message.ShouldBe("A host run is already active.");
        runner.InvocationCount.ShouldBe(1);
        runner.Release.TrySetResult();
        await observer.StartedSignal.Task.WaitAsync(timeoutSource.Token);
        host.Context.Shutdown();
        await firstRun.WaitAsync(timeoutSource.Token);
    }

    [Fact(DisplayName = DisplayPrefix + "Observer: Reports Started, Stopping, and Stopped in order")]
    public async Task RunAsync_WhenShutdownRequested_ReportsObserverOrder()
    {
        // Arrange
        var observer = new RecordingObserver();
        var host = new TestHost(new TestHostOptions());
        host.Context.Runner = new PassthroughRunner(observer);
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act
        Task run = host.RunAsync(timeoutSource.Token);
        await observer.StartedSignal.Task.WaitAsync(timeoutSource.Token);
        host.Context.Shutdown();
        await run.WaitAsync(timeoutSource.Token);

        // Assert
        observer.Transitions.ShouldBe(new[] { "started", "stopping", "stopped" });
    }

    [Fact(DisplayName = DisplayPrefix + "Observer: Reports DrainAborted when the shutdown budget elapses")]
    public async Task RunAsync_WhenShutdownTimeoutElapses_ReportsDrainAborted()
    {
        // Arrange
        var observer = new RecordingObserver();
        var options = new TestHostOptions();
        options.HostedServices.Add(new DelegateHostService(
            static _ => Task.CompletedTask,
            static cancellationToken => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)));
        var host = new TestHost(options);
        host.Context.Runner = new TimeoutRunner(observer, TimeSpan.FromMilliseconds(25));
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task run = host.RunAsync(timeoutSource.Token);
        await observer.StartedSignal.Task.WaitAsync(timeoutSource.Token);

        // Act
        host.Context.Shutdown();
        await Should.ThrowAsync<OperationCanceledException>(
            () => run.WaitAsync(timeoutSource.Token));

        // Assert
        observer.Transitions.ShouldBe(
            new[] { "started", "stopping", "drain-aborted", "stopped" });
    }

    [Fact(DisplayName = DisplayPrefix + "TryShutdown: Accepts only while Started")]
    public async Task TryShutdown_AroundLifecycle_AcceptsOnlyWhileStarted()
    {
        // Arrange
        var runner = new ShutdownProbeRunner();
        var host = new TestHost(new TestHostOptions());
        host.Context.Runner = runner;
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act
        await host.RunAsync(timeoutSource.Token).WaitAsync(timeoutSource.Token);

        // Assert
        runner.BeforeStarted.ShouldBeFalse();
        runner.WhileStarted.ShouldBeTrue();
        runner.OnAcceptedState.ShouldBe(HostState.Started);
        runner.WhileStopping.ShouldBeFalse();
        runner.AfterStopped.ShouldBeFalse();
    }

    [Fact(DisplayName = DisplayPrefix + "Stop join: Surfaces a direct StopAsync failure from RunAsync")]
    public async Task RunAsync_WhenDirectStopFails_SurfacesSameFailure()
    {
        // Arrange
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new TestHostOptions();
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

    [Fact(DisplayName = DisplayPrefix + "Restart: Reuses an installed runner after Stopped")]
    public async Task RunAsync_AfterStopped_ReusesInstalledRunner()
    {
        // Arrange
        var observer = new RecordingObserver();
        var runner = new PassthroughRunner(observer);
        var host = new TestHost(new TestHostOptions());
        host.Context.Runner = runner;
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
        runner.InvocationCount.ShouldBe(2);
        host.Context.Runner.ShouldBeSameAs(runner);
        host.Context.State.ShouldBe(HostState.Stopped);
    }

    [Fact(DisplayName = DisplayPrefix + "Dispose: Joins the stop already begun for an active run")]
    public async Task DisposeAsync_DuringRun_JoinsStopWithoutRetrying()
    {
        // Arrange
        var observer = new RecordingObserver();
        var host = new TestHost(new TestHostOptions());
        host.Context.Runner = new PassthroughRunner(observer);
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task run = host.RunAsync(timeoutSource.Token);
        await observer.StartedSignal.Task.WaitAsync(timeoutSource.Token);

        // Act
        await ((IAsyncDisposable)host).DisposeAsync();
        await run.WaitAsync(timeoutSource.Token);

        // Assert
        observer.Transitions.ShouldBe(new[] { "started", "stopping", "stopped" });
        host.Context.State.ShouldBe(HostState.Stopped);
    }

    [Fact(DisplayName = DisplayPrefix + "Observer failure: Completes teardown when Stopping throws")]
    public async Task RunAsync_WhenStoppingObserverThrows_CompletesTeardownAndSurfacesFailure()
    {
        // Arrange
        var observer = new ThrowingStoppingObserver();
        var host = new TestHost(new TestHostOptions());
        host.Context.Runner = new PassthroughRunner(observer);
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task run = host.RunAsync(timeoutSource.Token);
        await observer.StartedSignal.Task.WaitAsync(timeoutSource.Token);

        // Act
        host.Context.Shutdown();
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            () => run.WaitAsync(timeoutSource.Token));

        // Assert
        exception.Message.ShouldBe("observer stopping failure");
        host.Context.State.ShouldBe(HostState.Stopped);
    }

    private static async Task WaitForStateAsync(
        TestHost host,
        HostState expected,
        CancellationToken cancellationToken)
    {
        while (host.Context.State != expected)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }
    }

    private sealed class PassthroughRunner : IHostRunner
    {
        private readonly IHostRunObserver _observer;

        internal PassthroughRunner(IHostRunObserver observer)
        {
            _observer = observer;
        }

        internal int InvocationCount { get; private set; }

        public Task RunAsync(IHostRun run, CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            return run.RunAsync(_observer, cancellationToken);
        }
    }

    private sealed class TimeoutRunner : IHostRunner
    {
        private readonly IHostRunObserver _observer;
        private readonly TimeSpan _shutdownTimeout;

        internal TimeoutRunner(IHostRunObserver observer, TimeSpan shutdownTimeout)
        {
            _observer = observer;
            _shutdownTimeout = shutdownTimeout;
        }

        public Task RunAsync(IHostRun run, CancellationToken cancellationToken = default)
        {
            run.ShutdownTimeout = _shutdownTimeout;
            return run.RunAsync(_observer, cancellationToken);
        }
    }

    private sealed class DelayedRunner : IHostRunner
    {
        private readonly IHostRunObserver _observer;

        internal DelayedRunner(IHostRunObserver observer)
        {
            _observer = observer;
        }

        internal int InvocationCount { get; private set; }

        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task RunAsync(IHostRun run, CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            await run.RunAsync(_observer, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class ShutdownProbeRunner : IHostRunner, IHostRunObserver
    {
        private IHostRun? _run;

        internal bool BeforeStarted { get; private set; }
        internal bool WhileStarted { get; private set; }
        internal HostState? OnAcceptedState { get; private set; }
        internal bool WhileStopping { get; private set; }
        internal bool AfterStopped { get; private set; }

        public async Task RunAsync(IHostRun run, CancellationToken cancellationToken = default)
        {
            _run = run;
            BeforeStarted = run.TryShutdown();
            await run.RunAsync(this, cancellationToken).ConfigureAwait(false);
            AfterStopped = run.TryShutdown();
        }

        public void Started(IHost host)
        {
            WhileStarted = _run!.TryShutdown(() => OnAcceptedState = host.Context.State);
        }

        public void Stopping(IHost host)
        {
            _ = host;
            WhileStopping = _run!.TryShutdown();
        }

        public void DrainAborted(IHost host)
        {
            _ = host;
        }

        public void Stopped(IHost host)
        {
            _ = host;
        }
    }

    private sealed class RecordingObserver : IHostRunObserver
    {
        internal List<string> Transitions { get; } = [];

        internal TaskCompletionSource StartedSignal { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Started(IHost host)
        {
            _ = host;
            Transitions.Add("started");
            StartedSignal.TrySetResult();
        }

        public void Stopping(IHost host)
        {
            _ = host;
            Transitions.Add("stopping");
        }

        public void DrainAborted(IHost host)
        {
            _ = host;
            Transitions.Add("drain-aborted");
        }

        public void Stopped(IHost host)
        {
            _ = host;
            Transitions.Add("stopped");
        }
    }

    private sealed class ThrowingStoppingObserver : IHostRunObserver
    {
        internal TaskCompletionSource StartedSignal { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Started(IHost host)
        {
            _ = host;
            StartedSignal.TrySetResult();
        }

        public void Stopping(IHost host)
        {
            _ = host;
            throw new InvalidOperationException("observer stopping failure");
        }

        public void DrainAborted(IHost host)
        {
            _ = host;
        }

        public void Stopped(IHost host)
        {
            _ = host;
        }
    }
}
