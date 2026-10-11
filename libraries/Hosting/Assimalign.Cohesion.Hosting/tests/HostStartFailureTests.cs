using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Hosting.Tests;

public class HostStartFailureTests
{
    private const string displayPrefix = "Cohesion Test [Hosting] - Start failure: ";

    [Theory(DisplayName = displayPrefix + "Every startup phase reports once after compensation")]
    [InlineData("OnStarting", "OnStarting,OnStartFailed")]
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
            token.ShouldBe(CancellationToken.None);
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

    [Fact(DisplayName = displayPrefix + "Rollback and the failure hook are both awaited")]
    public async Task StartAsync_WhenRollbackAndReportingYield_ShouldAwaitBothBeforePropagating()
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
        releaseHook.SetResult();
        InvalidOperationException failure = await Should.ThrowAsync<InvalidOperationException>(
            () => start.WaitAsync(deadline.Token));

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
        var options = new TestHostOptions();
        options.HostedServices.Add(new DelegateHostService(_ => throw expectedFailure));
        await using IHost host = new HookRecordingHost(
            options,
            _ => { },
            (_, _) => throw new InvalidOperationException("reporting failed"));

        // Act
        UnwritableDataException failure = await Should.ThrowAsync<UnwritableDataException>(
            () => host.StartAsync(CancellationToken.None));

        // Assert
        failure.ShouldBeSameAs(expectedFailure);
        host.Context.State.ShouldBe(HostState.Failed);
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
            token.ShouldBe(CancellationToken.None);
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
}
