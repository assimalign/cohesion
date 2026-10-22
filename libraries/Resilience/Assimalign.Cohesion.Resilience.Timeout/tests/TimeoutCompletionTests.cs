using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Resilience.Timeout.Tests.TestObjects;

namespace Assimalign.Cohesion.Resilience.Timeout.Tests;

public class TimeoutCompletionTests
{
    private static readonly TimeSpan _budget = TimeSpan.FromHours(1);

    [Fact(DisplayName = "Cohesion Test [Resilience.Timeout] - Execute: Synchronous results do not create a race timer")]
    public async Task ExecuteAsync_SynchronousResult_ShouldAvoidRaceTimer()
    {
        // Arrange
        TrackingTimeProvider timeProvider = new();
        IResiliencePipeline<int> pipeline = CreatePipeline(timeProvider);

        // Act
        int result = await pipeline.ExecuteAsync<int, object?>(static (_, _) => ValueTask.FromResult(42));

        // Assert
        result.ShouldBe(42);
        timeProvider.CreatedTimerCount.ShouldBe(1); // Only the reusable cancellation-source timer.
        timeProvider.OutstandingTimerCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Resilience.Timeout] - Execute: Synchronous void callbacks do not create a race timer")]
    public async Task ExecuteAsync_SynchronousVoidCallback_ShouldAvoidRaceTimer()
    {
        // Arrange
        TrackingTimeProvider timeProvider = new();
        IResiliencePipeline pipeline = new ResiliencePipelineBuilder()
            .UseTimeout(options => { options.Timeout = _budget; options.TimeProvider = timeProvider; })
            .Build();

        // Act
        await pipeline.ExecuteAsync<object?>(static (_, _) => ValueTask.CompletedTask);

        // Assert
        timeProvider.CreatedTimerCount.ShouldBe(1);
        timeProvider.OutstandingTimerCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Resilience.Timeout] - Execute: Async success releases its timer before the deadline")]
    public async Task ExecuteAsync_AsynchronousSuccess_ShouldReleaseRaceTimer()
    {
        // Arrange
        TrackingTimeProvider timeProvider = new();
        IResiliencePipeline<int> pipeline = CreatePipeline(timeProvider);
        TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> execution = pipeline.ExecuteAsync<int, object?>((_, _) => new ValueTask<int>(completion.Task)).AsTask();
        timeProvider.OutstandingTimerCount.ShouldBe(2); // Cancellation source and task race.

        // Act
        completion.SetResult(42);
        int result = await execution.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

        // Assert
        result.ShouldBe(42);
        timeProvider.OutstandingTimerCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Resilience.Timeout] - Execute: Async failures release the timer and preserve the failure")]
    public async Task ExecuteAsync_AsynchronousFailure_ShouldReleaseRaceTimerAndPreserveFailure()
    {
        // Arrange
        TrackingTimeProvider timeProvider = new();
        IResiliencePipeline<int> pipeline = CreatePipeline(timeProvider);
        TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        InvalidOperationException failure = new("callback failure");
        Task<int> execution = pipeline.ExecuteAsync<int, object?>((_, _) => new ValueTask<int>(completion.Task)).AsTask();

        // Act
        completion.SetException(failure);
        ResilienceException exception = await Should.ThrowAsync<ResilienceException>(async () =>
            await execution.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None));

        // Assert
        ContainsException(exception, failure).ShouldBeTrue();
        timeProvider.OutstandingTimerCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Resilience.Timeout] - Execute: Callback TimeoutException stays a callback failure")]
    public async Task ExecuteAsync_CallbackTimeoutException_ShouldNotInvokeOnTimeout()
    {
        // Arrange
        TrackingTimeProvider timeProvider = new();
        int timeoutCalls = 0;
        IResiliencePipeline<int> pipeline = CreatePipeline(timeProvider, () => timeoutCalls++);
        TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TimeoutException failure = new("callback timeout");
        Task<int> execution = pipeline.ExecuteAsync<int, object?>((_, _) => new ValueTask<int>(completion.Task)).AsTask();

        // Act
        completion.SetException(failure);
        ResilienceException exception = await Should.ThrowAsync<ResilienceException>(async () =>
            await execution.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None));

        // Assert
        ContainsException(exception, failure).ShouldBeTrue();
        timeoutCalls.ShouldBe(0);
        timeProvider.OutstandingTimerCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Resilience.Timeout] - Execute: Deadline rejects a noncooperative callback and retains its canceled token")]
    public async Task ExecuteAsync_NoncooperativeCallback_ShouldRejectAndKeepTokenCanceledUntilCompletion()
    {
        // Arrange
        TrackingTimeProvider timeProvider = new();
        int timeoutCalls = 0;
        IResiliencePipeline<int> pipeline = CreatePipeline(timeProvider, () => timeoutCalls++);
        TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken callbackToken = default;
        Task<int> execution = pipeline.ExecuteAsync<int, object?>((context, _) =>
        {
            callbackToken = context.CancellationToken;
            return new ValueTask<int>(completion.Task);
        }).AsTask();

        // Act
        timeProvider.FireTimers();
        ResilienceException exception = await Should.ThrowAsync<ResilienceException>(async () =>
            await execution.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None));

        // Assert
        FindException<TimeoutRejectedException>(exception).Timeout.ShouldBe(_budget);
        callbackToken.IsCancellationRequested.ShouldBeTrue();
        completion.Task.IsCompleted.ShouldBeFalse();
        timeoutCalls.ShouldBe(1);
        timeProvider.OutstandingTimerCount.ShouldBe(0);

        // A new execution must not reset the token still owned by the late callback.
        (await pipeline.ExecuteAsync<int, object?>(static (_, _) => ValueTask.FromResult(7))).ShouldBe(7);
        callbackToken.IsCancellationRequested.ShouldBeTrue();
        completion.SetException(new InvalidOperationException("late failure"));
    }

    [Fact(DisplayName = "Cohesion Test [Resilience.Timeout] - Execute: Parent cancellation stays cancellation and releases the timer")]
    public async Task ExecuteAsync_ParentCancellation_ShouldPreserveCancellation()
    {
        // Arrange
        TrackingTimeProvider timeProvider = new();
        int timeoutCalls = 0;
        IResiliencePipeline<int> pipeline = CreatePipeline(timeProvider, () => timeoutCalls++);
        using CancellationTokenSource cancellation = new();
        Task<int> execution = pipeline.ExecuteAsync(static async (context, _) =>
        {
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, context.CancellationToken);
            return 42;
        }, new TestResilienceContext(cancellation.Token), null).AsTask();

        // Act
        cancellation.Cancel();
        ResilienceException exception = await Should.ThrowAsync<ResilienceException>(async () =>
            await execution.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None));

        // Assert
        FindException<OperationCanceledException>(exception).ShouldNotBeNull();
        timeoutCalls.ShouldBe(0);
        timeProvider.OutstandingTimerCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Resilience.Timeout] - Execute: Inline timeout cancellation still produces a rejection")]
    public async Task ExecuteAsync_InlineTimeoutCancellation_ShouldRejectOnce()
    {
        // Arrange
        TrackingTimeProvider timeProvider = new();
        int timeoutCalls = 0;
        IResiliencePipeline<int> pipeline = CreatePipeline(timeProvider, () => timeoutCalls++);

        // Act
        ResilienceException exception = await Should.ThrowAsync<ResilienceException>(async () =>
            await pipeline.ExecuteAsync<int, object?>((context, _) =>
            {
                timeProvider.FireTimers();
                return ValueTask.FromException<int>(new OperationCanceledException(context.CancellationToken));
            }));

        // Assert
        FindException<TimeoutRejectedException>(exception).InnerException.ShouldBeOfType<OperationCanceledException>();
        timeoutCalls.ShouldBe(1);
        timeProvider.CreatedTimerCount.ShouldBe(1);
        timeProvider.OutstandingTimerCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Resilience.Timeout] - Execute: Async void callbacks release their timer")]
    public async Task ExecuteAsync_AsynchronousVoidCallback_ShouldReleaseRaceTimer()
    {
        // Arrange
        TrackingTimeProvider timeProvider = new();
        IResiliencePipeline pipeline = new ResiliencePipelineBuilder()
            .UseTimeout(options => { options.Timeout = _budget; options.TimeProvider = timeProvider; })
            .Build();
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task execution = pipeline.ExecuteAsync<object?>((_, _) => new ValueTask(completion.Task)).AsTask();

        // Act
        completion.SetResult();
        await execution.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

        // Assert
        timeProvider.OutstandingTimerCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Resilience.Timeout] - Execute: A generated infinite budget bypasses timeout machinery")]
    public async Task ExecuteAsync_DisabledGeneratedBudget_ShouldUseOriginalContext()
    {
        // Arrange
        TrackingTimeProvider timeProvider = new();
        TestResilienceContext context = new(CancellationToken.None);
        IResiliencePipeline<int> pipeline = new ResiliencePipelineBuilder<int>()
            .UseTimeout(options =>
            {
                options.TimeProvider = timeProvider;
                options.TimeoutGenerator = static async _ =>
                {
                    await Task.Yield();
                    return System.Threading.Timeout.InfiniteTimeSpan;
                };
            })
            .Build();

        // Act
        int result = await pipeline.ExecuteAsync((actualContext, _) =>
        {
            actualContext.ShouldBeSameAs(context);
            return ValueTask.FromResult(42);
        }, context, null);

        // Assert
        result.ShouldBe(42);
        timeProvider.CreatedTimerCount.ShouldBe(0);
    }

    private static IResiliencePipeline<int> CreatePipeline(TrackingTimeProvider timeProvider, Action? onTimeout = null)
    {
        return new ResiliencePipelineBuilder<int>()
            .UseTimeout(options =>
            {
                options.Timeout = _budget;
                options.TimeProvider = timeProvider;
                options.OnTimeout = _ =>
                {
                    onTimeout?.Invoke();
                    return ValueTask.CompletedTask;
                };
            })
            .Build();
    }

    private static bool ContainsException(Exception exception, Exception expected)
    {
        if (ReferenceEquals(exception, expected))
        {
            return true;
        }

        if (exception is AggregateException aggregate)
        {
            foreach (Exception inner in aggregate.InnerExceptions)
            {
                if (ContainsException(inner, expected))
                {
                    return true;
                }
            }
        }

        return exception.InnerException is not null && ContainsException(exception.InnerException, expected);
    }

    private static TException FindException<TException>(Exception exception) where TException : Exception
    {
        Queue<Exception> pending = new();
        pending.Enqueue(exception);
        while (pending.Count > 0)
        {
            Exception current = pending.Dequeue();
            if (current is TException match)
            {
                return match;
            }

            if (current is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.InnerExceptions)
                {
                    pending.Enqueue(inner);
                }
            }

            if (current.InnerException is not null)
            {
                pending.Enqueue(current.InnerException);
            }
        }

        throw new InvalidOperationException("Expected exception was not found.");
    }
}
