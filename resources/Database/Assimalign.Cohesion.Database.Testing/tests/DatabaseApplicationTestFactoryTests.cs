using System;
using System.Diagnostics.Tracing;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Testing.TestHost;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Database.Testing.Tests;

/// <summary>
/// Entry-point and lifecycle coverage for <see cref="DatabaseApplicationTestFactory"/>.
/// </summary>
public class DatabaseApplicationTestFactoryTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Database.Testing] - FromProgram: runs the real entry point until its scope's shutdown stops its host")]
    public async Task FromProgram_StartedAndStopped_ShouldRunEntryPointUntilScopeShutdown()
    {
        // Arrange: the program's host reports its transitions on the Database hosting event source.
        using var cancellation = new CancellationTokenSource(_testTimeout);
        using var lifecycle = new HostLifecycleListener();
        await using DatabaseApplicationTestFactory factory =
            DatabaseApplicationTestFactory.FromProgram<Program>();

        // Act
        await factory.StartAsync(cancellation.Token);
        bool startedAfterStart = factory.IsStarted;
        await lifecycle.Started.WaitAsync(cancellation.Token);
        await factory.StopAsync(cancellation.Token);

        // Assert: the host stopped on the scope's shutdown, before the entry point returned.
        startedAfterStart.ShouldBeTrue();
        lifecycle.Stopped.IsCompletedSuccessfully.ShouldBeTrue();
        factory.IsStarted.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Testing] - FromProgram: rejects a marker that is not its assembly entry-point type")]
    public void FromProgram_WithNonEntryPointMarker_ShouldRejectMarker()
    {
        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => DatabaseApplicationTestFactory.FromProgram<DatabaseApplicationTestFactoryTests>());

        // Assert
        exception.Message.ShouldContain("not the entry-point Program", Case.Insensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Testing] - FromProgram: pre-cancelled startup does not launch the resource program")]
    public async Task StartAsync_WithPreCancelledToken_ShouldNotLaunchProgram()
    {
        await using DatabaseApplicationTestFactory factory =
            DatabaseApplicationTestFactory.FromProgram<Program>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => factory.StartAsync(cancellation.Token));

        factory.IsStarted.ShouldBeFalse();
        await factory.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// Observes the Database hosting event source's application transitions: the program's host
    /// started, and it stopped.
    /// </summary>
    private sealed class HostLifecycleListener : EventListener
    {
        private const string sourceName = "Assimalign.Cohesion.Database.Hosting";

        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public Task Stopped => _stopped.Task;

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == sourceName)
            {
                EnableEvents(eventSource, EventLevel.Informational);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventSource.Name != sourceName)
            {
                return;
            }

            switch (eventData.EventName)
            {
                case "ApplicationStarted":
                    _started.TrySetResult();
                    break;
                case "ApplicationStopped":
                    _stopped.TrySetResult();
                    break;
            }
        }
    }
}
