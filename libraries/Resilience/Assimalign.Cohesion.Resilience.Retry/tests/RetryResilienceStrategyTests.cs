using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Resilience;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Resilience.Retry.Tests;

public class RetryResilienceStrategyTests
{
    [Fact(DisplayName = "Cohesion Test [Resilience.Retry] - Strategy: Canceled execution does not invoke the callback")]
    public async Task Strategy_ExecuteAsync_ShouldNotInvokeCallbackWhenCanceled()
    {
        bool executed = false;

        IResiliencePipeline<object> pipeline = new ResiliencePipelineBuilder<object>()
            .UseRetry(_ => { })
            .Build();

        TestContext context = new()
        {
            CancellationToken = new CancellationToken(canceled: true),
        };

        ResilienceException exception = await Should.ThrowAsync<ResilienceException>(async () =>
        {
            await pipeline.ExecuteAsync(async (_, _) =>
            {
                executed = true;
                await Task.Yield();
                return new object();
            }, context, null).AsTask();
        });

        ExtractException<OperationCanceledException>(exception).ShouldNotBeNull();
        executed.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Resilience.Retry] - Strategy: Retries until the callback succeeds")]
    public async Task Strategy_ExecuteAsync_ShouldRetryUntilSuccess()
    {
        TestClient client = new(failuresBeforeSuccess: 2);

        IResiliencePipeline<bool> pipeline = new ResiliencePipelineBuilder<bool>()
            .UseRetry(options =>
            {
                options.MaxRetryAttempts = 5;
                options.Delay = TimeSpan.Zero;
                options.Retry = static args => ValueTask.FromResult(!args.Outcome.IsSuccess(out _));
            })
            .Build();

        bool result = await ResilienceExtensions.ExecuteAsync<bool, TestClient>(
            pipeline,
            static async (_, currentClient) => await currentClient!.SendAsync(),
            client);

        result.ShouldBeTrue();
        client.Attempts.ShouldBe(3);
    }

    [Fact(DisplayName = "Cohesion Test [Resilience.Retry] - Strategy: Exhausted retries preserve the original failure")]
    public async Task Strategy_ExecuteAsync_ShouldWrapTheFinalFailure()
    {
        TestClient client = new(failuresBeforeSuccess: int.MaxValue);

        IResiliencePipeline<bool> pipeline = new ResiliencePipelineBuilder<bool>()
            .UseRetry(options =>
            {
                options.MaxRetryAttempts = 1;
                options.Delay = TimeSpan.Zero;
                options.Retry = static args => ValueTask.FromResult(!args.Outcome.IsSuccess(out _));
            })
            .Build();

        ResilienceException exception = await Should.ThrowAsync<ResilienceException>(async () =>
        {
            await ResilienceExtensions.ExecuteAsync<bool, TestClient>(
                pipeline,
                static async (_, currentClient) => await currentClient!.SendAsync(),
                client);
        });

        ExtractException<TestException>(exception).ShouldNotBeNull();
        client.Attempts.ShouldBe(2);
    }

    private sealed class TestContext : IResilienceContext
    {
        public OperationKey OperationKey { get; init; }

        public CancellationToken CancellationToken { get; init; }

        public bool ContinueOnCapturedContext { get; init; }
    }

    private sealed class TestClient
    {
        private readonly int _failuresBeforeSuccess;

        public TestClient(int failuresBeforeSuccess)
        {
            _failuresBeforeSuccess = failuresBeforeSuccess;
        }

        public int Attempts { get; private set; }

        public async Task<bool> SendAsync()
        {
            await Task.Yield();

            Attempts++;

            if (Attempts <= _failuresBeforeSuccess)
            {
                throw new TestException();
            }

            return true;
        }
    }

    private sealed class TestException : Exception;

    private static TException ExtractException<TException>(Exception exception)
        where TException : Exception
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

        throw new InvalidOperationException($"Unable to locate an exception of type '{typeof(TException).FullName}'.");
    }
}
