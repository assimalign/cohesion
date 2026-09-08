using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Hosting.Tests;

[Collection(nameof(SerialCollection))]
public class HostToServiceWrapperTests
{
    public const string DisplayPrefix = "Cohesion Test [Hosting] - HostToServiceWrapper: ";

    private static (TestHost Host, Task Started) CreateIdleHost()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new TestHostOptions();

        options.HostedServices.Add(new DelegateBackgroundService(async cancellationToken =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }));

        return (new TestHost(options), started.Task);
    }

    [Fact(DisplayName = DisplayPrefix + "The shutdown wait completes when the host begins stopping")]
    public async Task WaitForShutdownAsync_BeforeStop_CompletesOnStoppingTransition()
    {
        // Arrange
        var context = new TestHostContext(new List<IHostService>());
        var stopped = context.WaitForShutdownAsync();

        // Act & Assert
        stopped.IsCompleted.ShouldBeFalse();

        context.SetState(HostState.Starting);
        context.SetState(HostState.Started);
        stopped.IsCompleted.ShouldBeFalse();

        context.SetState(HostState.Stopping);
        await stopped.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

        context.SetState(HostState.Stopped);
    }

    [Fact(DisplayName = DisplayPrefix + "The public shutdown wait completes when a running host stops")]
    public async Task WaitForShutdownAsync_WhenHostStops_Completes()
    {
        // Arrange
        IHost host = new TestHost(new TestHostOptions());
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await host.StartAsync(timeoutSource.Token);
        Task shutdown = host.Context.WaitForShutdownAsync(timeoutSource.Token);

        // Act
        await host.StopAsync(timeoutSource.Token);

        // Assert
        await shutdown.WaitAsync(timeoutSource.Token);
        host.Context.State.ShouldBe(HostState.Stopped);
    }

    [Fact(DisplayName = DisplayPrefix + "The shutdown wait is already completed for a stopped host")]
    public void WaitForShutdownAsync_WhenAlreadyStopped_ReturnsCompletedTask()
    {
        // Arrange
        var context = new TestHostContext(new List<IHostService>());
        context.SetState(HostState.Stopped);

        // Act & Assert
        context.WaitForShutdownAsync().IsCompleted.ShouldBeTrue();
    }

    [Fact(DisplayName = DisplayPrefix + "The shutdown wait resets for the next run after a restart")]
    public async Task WaitForShutdownAsync_AfterRestart_ProducesAFreshSignal()
    {
        // Arrange
        var context = new TestHostContext(new List<IHostService>());
        context.SetState(HostState.Stopped);
        context.SetState(HostState.Starting);
        context.SetState(HostState.Started);

        // Act
        var stopped = context.WaitForShutdownAsync();

        // Assert
        stopped.IsCompleted.ShouldBeFalse();

        context.SetState(HostState.Stopped);
        await stopped.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
    }

    [Fact(DisplayName = DisplayPrefix + "The shutdown wait also completes when the host fails")]
    public async Task WaitForShutdownAsync_OnFailedTransition_Completes()
    {
        // Arrange
        var context = new TestHostContext(new List<IHostService>());
        var stopped = context.WaitForShutdownAsync();

        // Act
        context.SetState(HostState.Starting);
        context.SetState(HostState.Failed);

        // Assert
        await stopped.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
    }

    [Fact(DisplayName = DisplayPrefix + "Beginning a run publishes shutdown acceptance without losing its signal")]
    public async Task BeginStart_WhenShutdownIsRequested_PreservesTheShutdownSignal()
    {
        // Arrange
        var context = new TestHostContext(new List<IHostService>());
        var callbackInvoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task earlyWait = context.WaitForShutdownAsync();

        // Act
        context.BeginStart(callbackInvoked.SetResult);
        context.Shutdown();

        // Assert
        await callbackInvoked.Task.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        await earlyWait.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        context.State.ShouldBe(HostState.Starting);
        context.WaitForShutdownAsync().IsCompleted.ShouldBeTrue();
    }

    [Fact(DisplayName = DisplayPrefix + "A nested host runs until the outer stop and is stopped by it")]
    public async Task StopAsync_AfterNestedHostStarted_StopsTheNestedHost()
    {
        // Arrange
        var (inner, started) = CreateIdleHost();
        var service = inner.AsService();

        // Act
        await service.StartAsync();
        await started;

        // Assert
        inner.Context.State.ShouldBe(HostState.Started);

        await service.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

        inner.Context.State.ShouldBe(HostState.Stopped);
    }

    [Fact(DisplayName = DisplayPrefix + "A nested child's shutdown request stops it without invoking its runner")]
    public async Task Shutdown_WhenRequestedByNestedChild_StopsChildWithoutInvokingRunner()
    {
        // Arrange
        var childStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new CountingRunner();
        var childOptions = new TestHostOptions();
        childOptions.HostedServices.Add(new DelegateHostService(
            static _ => Task.CompletedTask,
            _ =>
            {
                childStopped.TrySetResult();
                return Task.CompletedTask;
            }));
        var child = new TestHost(childOptions);
        child.Context.Runner = runner;
        IHostService service = ((IHost)child).AsService();
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await service.StartAsync(timeoutSource.Token);

        // Act
        child.Context.Shutdown();

        // Assert
        await childStopped.Task.WaitAsync(timeoutSource.Token);
        await service.StopAsync(timeoutSource.Token);
        child.Context.State.ShouldBe(HostState.Stopped);
        runner.InvocationCount.ShouldBe(0);
    }

    [Fact(DisplayName = DisplayPrefix + "When the wrapped host stops on its own the wrapper completes without an outer cancel")]
    public async Task ExecuteAsync_WhenNestedHostStopsOnItsOwn_WrapperCompletesWithoutOuterCancel()
    {
        // Arrange
        var (inner, started) = CreateIdleHost();
        var service = inner.AsService();

        await service.StartAsync();
        await started;

        // Act - stop the wrapped host directly, not through the wrapper.
        await ((IHost)inner).StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        await Task.Delay(500);

        // Assert - a pre-cancelled stop token only succeeds when the wrapper's work already
        // completed (a completed task wins over a cancelled token in WaitAsync); if the
        // wrapper were still parked, this would throw OperationCanceledException.
        await Should.NotThrowAsync(() => service.StopAsync(new CancellationToken(canceled: true)));
    }

    [Fact(DisplayName = DisplayPrefix + "Parent lifecycle: Runs and stops a nested child")]
    public async Task RunAsync_WhenParentShutsDown_StopsNestedChild()
    {
        // Arrange
        var childStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var childOptions = new TestHostOptions();
        childOptions.HostedServices.Add(new DelegateHostService(
            static _ => Task.CompletedTask,
            _ =>
            {
                childStopped.TrySetResult();
                return Task.CompletedTask;
            }));
        IHost child = new TestHost(childOptions);

        var parentOptions = new TestHostOptions();
        parentOptions.HostedServices.Add(child.AsService());
        IHost parent = new TestHost(parentOptions);
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act
        Task run = parent.RunAsync(timeoutSource.Token);
        await WaitForStateAsync(parent, HostState.Started, timeoutSource.Token);
        parent.Context.Shutdown();
        await run.WaitAsync(timeoutSource.Token);

        // Assert
        await childStopped.Task.WaitAsync(timeoutSource.Token);
        child.Context.State.ShouldBe(HostState.Stopped);
        parent.Context.State.ShouldBe(HostState.Stopped);
    }

    [Fact(DisplayName = DisplayPrefix + "Readiness: Parent waits for nested child readiness")]
    public async Task RunAsync_WhileNestedChildIsStarting_KeepsParentStarting()
    {
        // Arrange
        var childStartEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseChild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var childOptions = new TestHostOptions();
        childOptions.HostedServices.Add(new DelegateHostService(async cancellationToken =>
        {
            childStartEntered.TrySetResult();
            await releaseChild.Task.WaitAsync(cancellationToken);
        }));
        IHost child = new TestHost(childOptions);
        var parentOptions = new TestHostOptions();
        parentOptions.HostedServices.Add(child.AsService());
        IHost parent = new TestHost(parentOptions);
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act
        Task run = parent.RunAsync(timeoutSource.Token);
        await childStartEntered.Task.WaitAsync(timeoutSource.Token);

        // Assert
        child.Context.State.ShouldBe(HostState.Starting);
        parent.Context.State.ShouldBe(HostState.Starting);

        releaseChild.TrySetResult();
        await WaitForStateAsync(parent, HostState.Started, timeoutSource.Token);
        parent.Context.Shutdown();
        await run.WaitAsync(timeoutSource.Token);
    }

    [Fact(DisplayName = DisplayPrefix + "Readiness: Child start failure faults the parent run")]
    public async Task RunAsync_WhenNestedChildStartFails_SurfacesFailureOnParent()
    {
        // Arrange
        var childOptions = new TestHostOptions();
        childOptions.HostedServices.Add(new DelegateHostService(
            static _ => Task.FromException(new InvalidOperationException("child start failure"))));
        IHost child = new TestHost(childOptions);

        var parentOptions = new TestHostOptions();
        parentOptions.HostedServices.Add(child.AsService());
        IHost parent = new TestHost(parentOptions);

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            () => parent.RunAsync());

        // Assert
        exception.Message.ShouldBe("child start failure");
        child.Context.State.ShouldBe(HostState.Failed);
        parent.Context.State.ShouldBe(HostState.Failed);
    }

    [Fact(DisplayName = DisplayPrefix + "Startup cleanup: A faulted raw child start still receives StopAsync")]
    public async Task RunAsync_WhenRawChildStartFaults_StillAttemptsChildCleanup()
    {
        // Arrange
        var child = new FaultingStartHost();
        var parentOptions = new TestHostOptions();
        parentOptions.HostedServices.Add(((IHost)child).AsService());
        IHost parent = new TestHost(parentOptions);

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            () => parent.RunAsync());

        // Assert
        exception.Message.ShouldBe("raw child start failure");
        child.StopCount.ShouldBe(1);
        child.Context.State.ShouldBe(HostState.Stopped);
        parent.Context.State.ShouldBe(HostState.Failed);
    }

    [Fact(DisplayName = DisplayPrefix + "Readiness budget: A child that never starts produces a typed fault")]
    public async Task RunAsync_WhenNestedChildExceedsParentBudget_ThrowsHostStartupException()
    {
        // Arrange
        IHost child = new NeverReadyHost();
        var parentOptions = new TestHostOptions
        {
            StartupTimeout = TimeSpan.FromMilliseconds(50),
        };
        parentOptions.HostedServices.Add(child.AsService());
        IHost parent = new TestHost(parentOptions);
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act
        HostStartupException exception = await Should.ThrowAsync<HostStartupException>(
            () => parent.RunAsync(timeoutSource.Token));

        // Assert
        exception.Message.ShouldContain("startup readiness budget");
        exception.InnerException.ShouldBeAssignableTo<OperationCanceledException>();
        child.Context.State.ShouldBe(HostState.Stopped);
        parent.Context.State.ShouldBe(HostState.Failed);
    }

    [Fact(DisplayName = DisplayPrefix + "Readiness budget: A late child start is cleaned up after the parent fails")]
    public async Task RunAsync_WhenTimedOutChildLaterStarts_PerformsDeferredCleanup()
    {
        // Arrange
        var child = new DelayedStartingHost();
        var runner = new CountingRunner();
        child.Context.Runner = runner;
        var parentOptions = new TestHostOptions
        {
            StartupTimeout = TimeSpan.FromMilliseconds(25),
        };
        parentOptions.HostedServices.Add(((IHost)child).AsService());
        IHost parent = new TestHost(parentOptions);
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task run = parent.RunAsync(timeoutSource.Token);
        await child.StartEntered.Task.WaitAsync(timeoutSource.Token);

        // Act - parent failure must not wait for the child operation that ignored its token.
        HostStartupException exception = await Should.ThrowAsync<HostStartupException>(
            () => run.WaitAsync(timeoutSource.Token));

        // Assert
        exception.Message.ShouldContain("startup readiness budget");
        parent.Context.State.ShouldBe(HostState.Failed);
        child.Context.State.ShouldBe(HostState.Starting);

        child.ReleaseStart.TrySetResult();
        await child.Stopped.Task.WaitAsync(timeoutSource.Token);

        child.Context.State.ShouldBe(HostState.Stopped);
        child.StopCount.ShouldBe(1);
        runner.InvocationCount.ShouldBe(0);
    }

    [Fact(DisplayName = DisplayPrefix + "Readiness budget: A late faulted child start still receives deferred cleanup")]
    public async Task RunAsync_WhenTimedOutChildLaterFaultsWhileStarting_PerformsDeferredCleanup()
    {
        // Arrange
        var child = new DelayedStartingHost(faultAfterRelease: true);
        var runner = new CountingRunner();
        child.Context.Runner = runner;
        var parentOptions = new TestHostOptions
        {
            StartupTimeout = TimeSpan.FromMilliseconds(25),
        };
        parentOptions.HostedServices.Add(((IHost)child).AsService());
        IHost parent = new TestHost(parentOptions);
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task run = parent.RunAsync(timeoutSource.Token);
        await child.StartEntered.Task.WaitAsync(timeoutSource.Token);

        // Act
        await Should.ThrowAsync<HostStartupException>(() => run.WaitAsync(timeoutSource.Token));
        child.ReleaseStart.TrySetResult();

        // Assert
        await child.Stopped.Task.WaitAsync(timeoutSource.Token);
        child.Context.State.ShouldBe(HostState.Stopped);
        child.StopCount.ShouldBe(1);
        runner.InvocationCount.ShouldBe(0);
    }

    [Fact(DisplayName = DisplayPrefix + "Nested startup: Re-entry is rejected while the raw child start is pending")]
    public async Task StartAsync_WhileNestedChildIsStillStarting_RejectsReentry()
    {
        // Arrange
        var child = new DelayedStartingHost();
        IHostService service = ((IHost)child).AsService();
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task firstStart = service.StartAsync(timeoutSource.Token);
        await child.StartEntered.Task.WaitAsync(timeoutSource.Token);

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            () => service.StartAsync(timeoutSource.Token));

        // Assert
        exception.Message.ShouldContain("still starting");

        child.ReleaseStart.TrySetResult();
        await firstStart.WaitAsync(timeoutSource.Token);
        await service.StopAsync(timeoutSource.Token);
    }

    [Fact(DisplayName = DisplayPrefix + "Readiness budget: Cancellation-ignoring startup cannot publish readiness")]
    public async Task RunAsync_WhenStartupIgnoresExpiredBudget_ThrowsHostStartupException()
    {
        // Arrange
        var startEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new TestHostOptions
        {
            StartupTimeout = TimeSpan.FromMilliseconds(25),
        };
        options.HostedServices.Add(new DelegateHostService(async _ =>
        {
            startEntered.TrySetResult();
            await releaseStart.Task;
        }));
        IHost host = new TestHost(options);
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task run = host.RunAsync(timeoutSource.Token);
        await startEntered.Task.WaitAsync(timeoutSource.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(100), timeoutSource.Token);

        // Act
        releaseStart.TrySetResult();
        HostStartupException exception = await Should.ThrowAsync<HostStartupException>(
            () => run.WaitAsync(timeoutSource.Token));

        // Assert
        exception.Message.ShouldContain("startup readiness budget");
        exception.InnerException.ShouldBeAssignableTo<OperationCanceledException>();
        host.Context.State.ShouldBe(HostState.Failed);
    }

    [Fact(DisplayName = DisplayPrefix + "Readiness budget: Rollback cannot relabel an earlier cancellation as timeout")]
    public async Task RunAsync_WhenStartupCancellationPrecedesSlowRollback_PreservesOriginalCancellation()
    {
        // Arrange
        var options = new TestHostOptions
        {
            StartupTimeout = TimeSpan.FromMilliseconds(100),
        };
        options.HostedServices.Add(new DelegateHostService(
            static _ => Task.FromException(new OperationCanceledException("service cancelled independently")),
            static _ => Task.Delay(TimeSpan.FromMilliseconds(300))));
        IHost host = new TestHost(options);
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act
        await Should.ThrowAsync<OperationCanceledException>(
            () => host.RunAsync(timeoutSource.Token));

        // Assert
        host.Context.State.ShouldBe(HostState.Failed);
    }

    [Fact(DisplayName = DisplayPrefix + "Dependency order: Nested children stop in reverse start order")]
    public async Task RunAsync_WithTwoNestedChildren_StopsInReverseStartOrder()
    {
        // Arrange
        var transitions = new List<string>();
        IHost first = CreateRecordingHost("first", transitions);
        IHost second = CreateRecordingHost("second", transitions);
        var parentOptions = new TestHostOptions();
        parentOptions.HostedServices.Add(first.AsService());
        parentOptions.HostedServices.Add(second.AsService());
        IHost parent = new TestHost(parentOptions);
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act
        Task run = parent.RunAsync(timeoutSource.Token);
        await WaitForStateAsync(parent, HostState.Started, timeoutSource.Token);
        parent.Context.Shutdown();
        await run.WaitAsync(timeoutSource.Token);

        // Assert
        transitions.ShouldBe(
            new[] { "first:start", "second:start", "second:stop", "first:stop" });
    }

    [Fact(DisplayName = DisplayPrefix + "Fault surfacing: Child stop failure faults the parent run")]
    public async Task RunAsync_WhenNestedChildStopFails_SurfacesFailureOnParent()
    {
        // Arrange
        var childOptions = new TestHostOptions();
        childOptions.HostedServices.Add(new DelegateHostService(
            static _ => Task.CompletedTask,
            static _ => Task.FromException(new InvalidOperationException("child stop failure"))));
        IHost child = new TestHost(childOptions);
        var parentOptions = new TestHostOptions();
        parentOptions.HostedServices.Add(child.AsService());
        IHost parent = new TestHost(parentOptions);
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task run = parent.RunAsync(timeoutSource.Token);
        await WaitForStateAsync(parent, HostState.Started, timeoutSource.Token);

        // Act
        parent.Context.Shutdown();
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            () => run.WaitAsync(timeoutSource.Token));

        // Assert
        exception.Message.ShouldBe("child stop failure");
        parent.Context.State.ShouldBe(HostState.Stopped);
    }

    [Fact(DisplayName = DisplayPrefix + "Shutdown budget: Parent caps the nested child's drain")]
    public async Task RunAsync_WhenParentShutdownBudgetExpires_CancelsNestedChildDrain()
    {
        // Arrange
        var childBudgetCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var childOptions = new TestHostOptions
        {
            ShutdownTimeout = TimeSpan.FromSeconds(30),
        };
        childOptions.HostedServices.Add(new DelegateHostService(
            static _ => Task.CompletedTask,
            async cancellationToken =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    childBudgetCanceled.TrySetResult();
                    throw;
                }
            }));
        IHost child = new TestHost(childOptions);
        var parentOptions = new TestHostOptions
        {
            ShutdownTimeout = TimeSpan.FromMilliseconds(50),
        };
        parentOptions.HostedServices.Add(child.AsService());
        IHost parent = new TestHost(parentOptions);
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task run = parent.RunAsync(timeoutSource.Token);
        await WaitForStateAsync(parent, HostState.Started, timeoutSource.Token);

        // Act
        parent.Context.Shutdown();
        await Should.ThrowAsync<OperationCanceledException>(() => run.WaitAsync(timeoutSource.Token));

        // Assert
        await childBudgetCanceled.Task.WaitAsync(timeoutSource.Token);
        child.Context.State.ShouldBe(HostState.Stopped);
        parent.Context.State.ShouldBe(HostState.Stopped);
    }

    [Fact(DisplayName = DisplayPrefix + "Runner ownership: Nested execution does not invoke the child runner")]
    public async Task RunAsync_WhenChildHasRunner_DoesNotInvokeChildRunner()
    {
        // Arrange
        var runner = new CountingRunner();
        var child = new TestHost(new TestHostOptions());
        child.Context.Runner = runner;
        var parentOptions = new TestHostOptions();
        parentOptions.HostedServices.Add(((IHost)child).AsService());
        IHost parent = new TestHost(parentOptions);
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act
        Task run = parent.RunAsync(timeoutSource.Token);
        await WaitForStateAsync(parent, HostState.Started, timeoutSource.Token);
        parent.Context.Shutdown();
        await run.WaitAsync(timeoutSource.Token);

        // Assert
        runner.InvocationCount.ShouldBe(0);
        child.Context.Runner.ShouldBeSameAs(runner);
    }

    [Fact(DisplayName = DisplayPrefix + "An idle nested host does not busy-spin a core")]
    public async Task ExecuteAsync_WhileNestedHostIdles_DoesNotBusySpin()
    {
        // Arrange
        var (inner, started) = CreateIdleHost();
        var service = inner.AsService();

        await service.StartAsync();
        await started;

        // Let transient work (JIT, GC, expiring timers from sibling tests) settle.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        await Task.Delay(100);

        // Act - sample process CPU over idle windows. A busy-spin burns every window on a
        // full core, so a single quiet window proves the wrapper is parked, while process
        // noise (finalizers, timer callbacks from earlier tests) may legitimately blow the
        // budget in some windows.
        var quietWindowObserved = false;

        for (var attempt = 0; attempt < 4 && !quietWindowObserved; attempt++)
        {
            var before = Process.GetCurrentProcess().TotalProcessorTime;
            await Task.Delay(500);
            var consumed = Process.GetCurrentProcess().TotalProcessorTime - before;

            quietWindowObserved = consumed < TimeSpan.FromMilliseconds(250);
        }

        // Assert
        quietWindowObserved.ShouldBeTrue();

        await service.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
    }

    private static IHost CreateRecordingHost(string name, List<string> transitions)
    {
        var options = new TestHostOptions();
        options.HostedServices.Add(new DelegateHostService(
            _ =>
            {
                transitions.Add($"{name}:start");
                return Task.CompletedTask;
            },
            _ =>
            {
                transitions.Add($"{name}:stop");
                return Task.CompletedTask;
            }));
        return new TestHost(options);
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

    private sealed class NeverReadyHost : IHost
    {
        private readonly TaskCompletionSource _startSource =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal NeverReadyHost()
        {
            Context = new TestHostContext([]);
        }

        public HostId Id => Context.HostId;

        public TestHostContext Context { get; }

        IHostContext IHost.Context => Context;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            Context.SetState(HostState.Starting);
            return _startSource.Task;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
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

    private sealed class DelayedStartingHost : IHost
    {
        private readonly bool _faultAfterRelease;
        private int _hasSettledStart;

        internal DelayedStartingHost(bool faultAfterRelease = false)
        {
            _faultAfterRelease = faultAfterRelease;
            Context = new TestHostContext([]);
        }

        public HostId Id => Context.HostId;

        public TestHostContext Context { get; }

        IHostContext IHost.Context => Context;

        internal TaskCompletionSource StartEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseStart { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Stopped { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int StopCount { get; private set; }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            Context.SetState(HostState.Starting);
            StartEntered.TrySetResult();
            await ReleaseStart.Task;
            Volatile.Write(ref _hasSettledStart, 1);

            if (_faultAfterRelease)
            {
                throw new InvalidOperationException("late raw child start failure");
            }

            Context.SetState(HostState.Started);
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;

            HostState state = Context.State;
            if (state is not HostState.Started &&
                (state is not HostState.Starting || Volatile.Read(ref _hasSettledStart) == 0))
            {
                return Task.CompletedTask;
            }

            StopCount++;
            Context.SetState(HostState.Stopping);
            Context.SetState(HostState.Stopped);
            Stopped.TrySetResult();
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

    private sealed class FaultingStartHost : IHost
    {
        internal FaultingStartHost()
        {
            Context = new TestHostContext([]);
        }

        public HostId Id => Context.HostId;

        public TestHostContext Context { get; }

        IHostContext IHost.Context => Context;

        internal int StopCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            Context.SetState(HostState.Starting);
            return Task.FromException(new InvalidOperationException("raw child start failure"));
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
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

    private sealed class CountingRunner : IHostRunner
    {
        internal int InvocationCount { get; private set; }

        public Task RunAsync(IHostRun run, CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            return run.RunAsync(observer: null, cancellationToken);
        }
    }
}
