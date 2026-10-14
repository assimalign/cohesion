using System;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks.Sources;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections.Tcp.Tests.TestObjects;

namespace Assimalign.Cohesion.Connections.Tcp.Tests;

/// <summary>
/// The socket operation's value-task source (#1093): an awaiter's continuation must run exactly once,
/// with the state it registered, however its registration interleaves with the socket completing.
/// A continuation that ran with another state crashed the process: the runtime's async-method
/// continuation rejects any state that is not its state machine.
/// </summary>
public class SocketPipeAsyncArgsTests
{
    private static readonly TimeSpan _waitBudget = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "Cohesion Test [Connections.Tcp] - Socket args: A continuation registered after completion runs with its state")]
    public void OnCompleted_AfterOperationCompleted_ShouldRunContinuationWithItsState()
    {
        // Arrange
        TestSocketPipeAsyncArgs args = new(PipeScheduler.Inline);
        object state = new();
        object? observed = null;
        using ManualResetEventSlim ran = new();
        args.CompleteOperation();

        // Act
        args.OnCompleted(s => { observed = s; ran.Set(); }, state, 0, ValueTaskSourceOnCompletedFlags.None);

        // Assert
        ran.Wait(_waitBudget).ShouldBeTrue();
        observed.ShouldBeSameAs(state);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Tcp] - Socket args: A continuation registered before completion runs with its state")]
    public void OnCompleted_BeforeOperationCompleted_ShouldRunContinuationWithItsState()
    {
        // Arrange
        TestSocketPipeAsyncArgs args = new(PipeScheduler.Inline);
        object state = new();
        object? observed = null;
        args.OnCompleted(s => observed = s, state, 0, ValueTaskSourceOnCompletedFlags.None);

        // Act
        args.CompleteOperation();

        // Assert
        observed.ShouldBeSameAs(state);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Tcp] - Socket args: Registration racing completion never pairs a continuation with another state")]
    public void OnCompleted_RacingOperationCompletion_ShouldAlwaysRunContinuationOnceWithItsState()
    {
        // Arrange — a worker registers each iteration's continuation while this thread completes the
        // operation, both released by the same signal, so the two paths overlap as closely as the
        // hardware allows. Each iteration waits for its continuation before the next one starts.
        const int iterations = 50_000;

        TestSocketPipeAsyncArgs args = new(PipeScheduler.Inline);
        object[] states = new object[iterations];
        object?[] observed = new object?[iterations];
        int[] runs = new int[iterations];
        int iteration = -1;
        int registered = -1;
        Exception? workerFailure = null;

        Action<object?> continuation = state =>
        {
            int index = Volatile.Read(ref iteration);
            observed[index] = state;
            Interlocked.Increment(ref runs[index]);
        };

        Thread worker = new(() =>
        {
            try
            {
                for (int i = 0; i < iterations; i++)
                {
                    SpinUntil(() => Volatile.Read(ref iteration) >= i);
                    args.OnCompleted(continuation, states[i], 0, ValueTaskSourceOnCompletedFlags.None);
                    Volatile.Write(ref registered, i);
                }
            }
            catch (Exception exception)
            {
                workerFailure = exception;
            }
        })
        {
            IsBackground = true,
        };

        // Act
        worker.Start();

        for (int i = 0; i < iterations; i++)
        {
            states[i] = new object();
            Volatile.Write(ref iteration, i);
            args.CompleteOperation();

            int current = i;
            SpinUntil(() => Volatile.Read(ref registered) >= current && Volatile.Read(ref runs[current]) > 0);
            args.GetResult(0);
        }

        worker.Join(_waitBudget).ShouldBeTrue();

        // Assert
        workerFailure.ShouldBeNull();

        int mismatches = 0;
        int duplicates = 0;
        for (int i = 0; i < iterations; i++)
        {
            if (!ReferenceEquals(observed[i], states[i]))
            {
                mismatches++;
            }

            if (runs[i] != 1)
            {
                duplicates++;
            }
        }

        mismatches.ShouldBe(0, "a continuation ran with a state it did not register");
        duplicates.ShouldBe(0, "a continuation ran more than once");
    }

    private static void SpinUntil(Func<bool> condition)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        SpinWait spin = default;

        while (!condition())
        {
            if (elapsed.Elapsed > _waitBudget)
            {
                throw new TimeoutException("A continuation never ran.");
            }

            spin.SpinOnce(sleep1Threshold: -1);
        }
    }
}
