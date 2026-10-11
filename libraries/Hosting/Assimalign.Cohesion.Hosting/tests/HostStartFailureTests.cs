using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Hosting.Tests;

[Collection(nameof(SerialCollection))]
public class HostStartFailureTests
{
    private const string displayPrefix = "Cohesion Test [Hosting] - Start failure: ";

    [Theory(DisplayName = displayPrefix + "Every startup phase reports once after compensation")]
    [InlineData("Starting", "OnStarting,Starting,Stop,OnStartFailed")]
    [InlineData("Start", "OnStarting,Starting,Start,Stop,OnStartFailed")]
    [InlineData("Started", "OnStarting,Starting,Start,Started,Stop,OnStartFailed")]
    [InlineData("OnStarted", "OnStarting,Starting,Start,Started,OnStarted,Stop,OnStartFailed")]
    public async Task StartAsync_WhenStartupPhaseFails_ShouldReportOnceAfterRollback(
        string failingPhase,
        string expectedOrder)
    {
        // Arrange
        var events = new List<string>();
        var expectedFailure = new InvalidOperationException("startup failed");
        var options = new TestHostOptions();
        options.HostedServices.Add(new TestLifecycleService(phase => Record(phase.ToString())));
        int reports = 0;
        HookRecordingHost? host = null;
        host = new HookRecordingHost(options, Record, (failure, token) =>
        {
            reports++;
            failure.ShouldBeSameAs(expectedFailure);
            token.CanBeCanceled.ShouldBeTrue();
            host!.Context.State.ShouldBe(HostState.Failed);
            return Task.CompletedTask;
        });
        await using IHost lifetime = host;

        // Act
        InvalidOperationException failure = await Should.ThrowAsync<InvalidOperationException>(
            () => lifetime.StartAsync(CancellationToken.None));
        await lifetime.StopAsync(CancellationToken.None);

        // Assert
        failure.ShouldBeSameAs(expectedFailure);
        failure.Data.Count.ShouldBe(0);
        failure.StackTrace.ShouldNotBeNull().ShouldContain(nameof(Record));
        events.ShouldBe(expectedOrder.Split(','));
        reports.ShouldBe(1);

        void Record(string phase)
        {
            events.Add(phase);
            if (phase == failingPhase)
            {
                throw expectedFailure;
            }
        }
    }

    [Fact(DisplayName = displayPrefix + "Rollback is awaited before bounded reporting")]
    public async Task StartAsync_WhenRollbackAndReportingYield_ShouldBoundReportingAfterRollback()
    {
        // Arrange
        var rollbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRollback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hookEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHook = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expectedFailure = new InvalidOperationException("startup failed");
        var options = new TestHostOptions();
        options.HostedServices.Add(new DelegateHostService(
            _ => Task.FromException(expectedFailure),
            async token =>
            {
                rollbackEntered.SetResult();
                await releaseRollback.Task.WaitAsync(token);
            }));
        int reports = 0;
        HookRecordingHost? host = null;
        host = new HookRecordingHost(options, _ => { }, async (failure, token) =>
        {
            reports++;
            failure.ShouldBeSameAs(expectedFailure);
            host!.Context.State.ShouldBe(HostState.Failed);
            hookEntered.SetResult();
            await releaseHook.Task.WaitAsync(token);
        });
        await using IHost lifetime = host;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act
        Task start = lifetime.StartAsync(CancellationToken.None);
        await rollbackEntered.Task.WaitAsync(deadline.Token);
        hookEntered.Task.IsCompleted.ShouldBeFalse();
        releaseRollback.SetResult();
        await hookEntered.Task.WaitAsync(deadline.Token);
        start.IsCompleted.ShouldBeFalse();
        InvalidOperationException failure;
        try
        {
            failure = await Should.ThrowAsync<InvalidOperationException>(
                () => start.WaitAsync(deadline.Token));
        }
        finally
        {
            releaseHook.TrySetResult();
        }

        // Assert
        failure.ShouldBeSameAs(expectedFailure);
        reports.ShouldBe(1);
        host.Context.State.ShouldBe(HostState.Failed);
    }

    [Theory(DisplayName = displayPrefix + "A hook failure is attached without replacing startup failure")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartAsync_WhenFailureHookThrows_ShouldAttachFailureAndPreserveStartupException(bool asynchronous)
    {
        // Arrange
        var expectedFailure = new InvalidOperationException("startup failed");
        var hookFailure = new ArgumentException("reporting failed");
        var options = new TestHostOptions();
        options.HostedServices.Add(new DelegateHostService(_ => throw expectedFailure));
        int reports = 0;
        await using IHost host = new HookRecordingHost(options, _ => { }, (_, _) =>
        {
            reports++;
            if (asynchronous)
            {
                return Task.FromException(hookFailure);
            }

            throw hookFailure;
        });

        // Act
        InvalidOperationException failure = await Should.ThrowAsync<InvalidOperationException>(
            () => host.StartAsync(CancellationToken.None));

        // Assert
        failure.ShouldBeSameAs(expectedFailure);
        failure.Data["Assimalign.Cohesion.Hosting.StartFailureHookException"].ShouldBeSameAs(hookFailure);
        reports.ShouldBe(1);
        host.Context.State.ShouldBe(HostState.Failed);
    }

    [Fact(DisplayName = displayPrefix + "A custom exception rejecting diagnostic attachment still propagates")]
    public async Task StartAsync_WhenExceptionRejectsHookFailureAttachment_ShouldPreserveStartupException()
    {
        // Arrange
        var expectedFailure = new UnwritableDataException();
        var hookFailure = new InvalidOperationException("reporting failed");
        var options = new TestHostOptions();
        bool fail = true;
        options.HostedServices.Add(new DelegateHostService(_ => fail ? throw expectedFailure : Task.CompletedTask));
        var host = new HookRecordingHost(
            options,
            _ => { },
            (_, _) => throw hookFailure);
        await using IHost lifetime = host;

        // Act
        UnwritableDataException failure = await Should.ThrowAsync<UnwritableDataException>(
            () => lifetime.StartAsync(CancellationToken.None));

        // Assert
        failure.ShouldBeSameAs(expectedFailure);
        host.Context.State.ShouldBe(HostState.Failed);
        host.StartFailureHookException.ShouldBeSameAs(hookFailure);

        // A fresh accepted start resets the prior attempt's diagnostic record.
        fail = false;
        await lifetime.StartAsync(CancellationToken.None);
        host.StartFailureHookException.ShouldBeNull();
        await lifetime.StopAsync(CancellationToken.None);
    }

    [Fact(DisplayName = displayPrefix + "Timeout reporting receives the same translated failure as the caller")]
    public async Task StartAsync_WhenStartupTimesOut_ShouldReportThePropagatedStartupFailure()
    {
        // Arrange
        var options = new TestHostOptions { StartupTimeout = TimeSpan.FromMilliseconds(50) };
        options.HostedServices.Add(new DelegateHostService(token => Task.Delay(Timeout.InfiniteTimeSpan, token)));
        Exception? reportedFailure = null;
        int reports = 0;
        await using IHost host = new HookRecordingHost(options, _ => { }, (failure, token) =>
        {
            reports++;
            reportedFailure = failure;
            token.CanBeCanceled.ShouldBeTrue();
            return Task.CompletedTask;
        });

        // Act
        HostStartupException failure = await Should.ThrowAsync<HostStartupException>(
            () => host.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));

        // Assert
        failure.ShouldBeSameAs(reportedFailure);
        failure.Data.Count.ShouldBe(0);
        failure.InnerException.ShouldBeAssignableTo<OperationCanceledException>();
        reports.ShouldBe(1);
        host.Context.State.ShouldBe(HostState.Failed);
    }

    [Fact(DisplayName = displayPrefix + "Each failed retry reports once and successful retries do not report")]
    public async Task StartAsync_WhenRetriedAfterFailure_ShouldReportOnlyFailedAttempts()
    {
        // Arrange
        var options = new TestHostOptions();
        int attempts = 0;
        options.HostedServices.Add(new DelegateHostService(_ => ++attempts < 3
            ? Task.FromException(new InvalidOperationException("startup failed"))
            : Task.CompletedTask));
        int reports = 0;
        await using IHost host = new HookRecordingHost(options, _ => { }, (_, _) =>
        {
            reports++;
            return Task.CompletedTask;
        });

        // Act
        await Should.ThrowAsync<InvalidOperationException>(() => host.StartAsync(CancellationToken.None));
        await Should.ThrowAsync<InvalidOperationException>(() => host.StartAsync(CancellationToken.None));
        await host.StartAsync(CancellationToken.None);
        await host.StopAsync(CancellationToken.None);

        // Assert
        attempts.ShouldBe(3);
        reports.ShouldBe(2);
        host.Context.State.ShouldBe(HostState.Stopped);
    }

    [Fact(DisplayName = displayPrefix + "A pending report cannot leave the startup timer armed")]
    public async Task StartAsync_WhenReportingOutlastsStartupDeadline_ShouldDisarmStartupCancellation()
    {
        // Arrange
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expectedFailure = new InvalidOperationException("startup failed immediately");
        var options = new TestHostOptions { StartupTimeout = TimeSpan.FromMilliseconds(200) };
        options.HostedServices.Add(new DelegateHostService(_ => throw expectedFailure));
        int reports = 0;
        await using IHost host = new HookRecordingHost(options, _ => { }, (_, _) =>
        {
            Interlocked.Increment(ref reports);
            entered.SetResult();
            return release.Task;
        });

        // Act: hold reporting beyond the real startup deadline after observing entry.
        Task start = host.StartAsync(CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(TimeSpan.FromMilliseconds(600), CancellationToken.None);
            release.Task.IsCompleted.ShouldBeFalse();
            host.Context.State.ShouldBe(HostState.Failed);
        }
        finally
        {
            release.TrySetResult();
        }
        var failure = await Should.ThrowAsync<InvalidOperationException>(() => start.WaitAsync(TimeSpan.FromSeconds(5)));

        // Assert
        failure.ShouldBeSameAs(expectedFailure);
        reports.ShouldBe(1);
    }

    [Theory(DisplayName = displayPrefix + "An uncooperative report cannot hold the original failure")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartAsync_WhenReportingNeverCompletes_ShouldBoundAsynchronousAndSynchronousHooks(bool synchronous)
    {
        // Arrange
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expectedFailure = new InvalidOperationException("startup failed");
        var options = new TestHostOptions { ShutdownTimeout = Timeout.InfiniteTimeSpan };
        options.HostedServices.Add(new DelegateHostService(_ => throw expectedFailure));
        CancellationToken reportingToken = default;
        int reports = 0;
        await using IHost host = new HookRecordingHost(options, _ => { }, async (_, token) =>
        {
            reportingToken = token;
            Interlocked.Increment(ref reports);
            entered.SetResult();
            try
            {
                if (synchronous) { release.Task.GetAwaiter().GetResult(); }
                else { await release.Task; }
            }
            finally { exited.SetResult(); }
        });

        // Act: isolate the test driver too, so the old synchronously blocking code can fail.
        Task start = Task.Run(() => host.StartAsync(CancellationToken.None));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var failure = await Should.ThrowAsync<InvalidOperationException>(
                () => start.WaitAsync(TimeSpan.FromSeconds(3)));

            // Assert
            failure.ShouldBeSameAs(expectedFailure);
            reportingToken.IsCancellationRequested.ShouldBeTrue();
            exited.Task.IsCompleted.ShouldBeFalse();
            reports.ShouldBe(1);
            host.Context.State.ShouldBe(HostState.Failed);
        }
        finally
        {
            release.TrySetResult();
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try { await start; } catch (InvalidOperationException) { }
        }
    }

    [Theory(DisplayName = displayPrefix + "Caller cancellation is not reported as a failed start")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartAsync_WhenCallerCancels_ShouldSkipReportingAndAllowRetry(bool beforeEntry)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var options = new TestHostOptions();
        int starts = 0;
        options.HostedServices.Add(new DelegateHostService(token =>
        {
            if (++starts == 1 && !beforeEntry) { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            return Task.CompletedTask;
        }));
        int reports = 0;
        await using IHost host = new HookRecordingHost(options, _ => { }, (_, _) =>
        {
            reports++;
            return Task.CompletedTask;
        });
        if (beforeEntry) { cancellation.Cancel(); }

        // Act
        await Should.ThrowAsync<OperationCanceledException>(() => host.StartAsync(cancellation.Token));

        // Assert
        reports.ShouldBe(0);
        starts.ShouldBe(beforeEntry ? 0 : 1);
        await host.StartAsync(CancellationToken.None);
        await host.StopAsync(CancellationToken.None);
        starts.ShouldBe(beforeEntry ? 1 : 2);
        reports.ShouldBe(0);
    }

    [Fact(DisplayName = displayPrefix + "A rejected second lifecycle is not reported or compensated again")]
    public async Task StartAsync_WhenOnStartingRejectsRestart_ShouldSkipFailureReporting()
    {
        // Arrange
        int attempts = 0;
        int starts = 0;
        int stops = 0;
        int reports = 0;
        var options = new TestHostOptions();
        options.HostedServices.Add(new DelegateHostService(
            _ => { starts++; return Task.CompletedTask; },
            _ => { stops++; return Task.CompletedTask; }));
        await using IHost host = new HookRecordingHost(options, phase =>
        {
            if (phase == "OnStarting" && ++attempts > 1) { throw new InvalidOperationException("one lifecycle"); }
        }, (_, _) => { reports++; return Task.CompletedTask; });
        await host.StartAsync(CancellationToken.None);
        await host.StopAsync(CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<InvalidOperationException>(() => host.StartAsync(CancellationToken.None));

        // Assert
        failure.Message.ShouldBe("one lifecycle");
        reports.ShouldBe(0);
        starts.ShouldBe(1);
        stops.ShouldBe(1);
    }

    [Fact(DisplayName = displayPrefix + "A late hook fault is observed and cannot overwrite a successful retry")]
    public async Task StartAsync_WhenAbandonedHookFaultsAfterRetry_ShouldObserveFaultWithoutChangingDiagnostics()
    {
        // Arrange
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateFault = new WeakReference(null);
        string lateMessage = Guid.NewGuid().ToString();
        int unobserved = 0;
        int reports = 0;
        int attempts = 0;
        var options = new TestHostOptions();
        var expectedFailure = new InvalidOperationException("startup failed");
        options.HostedServices.Add(new DelegateHostService(_ => ++attempts == 1
            ? Task.FromException(expectedFailure) : Task.CompletedTask));
        var host = new HookRecordingHost(options, _ => { }, async (_, _) =>
        {
            Interlocked.Increment(ref reports);
            await release.Task;
            try
            {
                var failure = new InvalidOperationException(lateMessage);
                lateFault.Target = failure;
                throw failure;
            }
            finally { exited.SetResult(); }
        });
        await using IHost lifetime = host;
        EventHandler<UnobservedTaskExceptionEventArgs> onUnobserved = (_, args) =>
        {
            if (args.Exception.GetBaseException().Message == lateMessage)
            {
                Interlocked.Increment(ref unobserved);
                args.SetObserved();
            }
        };
        TaskScheduler.UnobservedTaskException += onUnobserved;
        try
        {
            // Act
            var failure = await Should.ThrowAsync<InvalidOperationException>(
                () => lifetime.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
            failure.ShouldBeSameAs(expectedFailure);
            object? diagnostic = failure.Data["Assimalign.Cohesion.Hosting.StartFailureHookException"];
            diagnostic.ShouldBeOfType<TimeoutException>();
            await lifetime.StartAsync(CancellationToken.None);
            release.SetResult();
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Force finalization until the late exception is no longer held by any task,
            // with a minimum observation period and a ceiling, not a fixed race window.
            var watch = Stopwatch.StartNew();
            int collections = 0;
            do
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                collections++;
                await Task.Delay(10, CancellationToken.None);
                watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
            } while (watch.Elapsed < TimeSpan.FromMilliseconds(200) || lateFault.IsAlive);

            // Assert
            collections.ShouldBeGreaterThan(0);
            reports.ShouldBe(1);
            unobserved.ShouldBe(0);
            host.StartFailureHookException.ShouldBeNull();
            failure.Data["Assimalign.Cohesion.Hosting.StartFailureHookException"].ShouldBeSameAs(diagnostic);
            await lifetime.StopAsync(CancellationToken.None);
        }
        finally
        {
            release.TrySetResult();
            TaskScheduler.UnobservedTaskException -= onUnobserved;
        }
    }
}
