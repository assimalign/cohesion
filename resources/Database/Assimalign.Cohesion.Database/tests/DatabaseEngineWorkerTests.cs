using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Tests;

/// <summary>
/// The guided worker base's pump (#1268): a failed pass — one that threw, or one that reported a
/// failure it moved past — is recorded and backed off, and the loop keeps running; a pass that
/// completes its work clears the record. Before #1268 the first exception escaped the loop and
/// ended the worker for good.
/// </summary>
public class DatabaseEngineWorkerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Database] - Worker: a pass that throws is recorded, never thrown, and the next clean pass clears it")]
    public void RunIteration_PassThrows_ShouldRecordTheFailureUntilAPassCompletes()
    {
        // Arrange
        var failure = new InvalidOperationException("pass failed");
        var worker = new ScriptedWorker((_, pass) => pass == 1 ? throw failure : true);

        // Act
        bool first = worker.RunIteration(CancellationToken.None);
        var faultAfterTheFailure = worker.Fault;
        int consecutiveAfterTheFailure = worker.ConsecutiveFailures;
        bool second = worker.RunIteration(CancellationToken.None);

        // Assert
        first.ShouldBeFalse();
        faultAfterTheFailure.ShouldBeSameAs(failure);
        consecutiveAfterTheFailure.ShouldBe(1);
        second.ShouldBeTrue();
        worker.Fault.ShouldBeNull();
        worker.ConsecutiveFailures.ShouldBe(0);
        worker.FailureCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Worker: a failure a pass reports and moves past fails the pass without stopping its work")]
    public void RunIteration_ReportedFailure_ShouldFailThePassAndFinishItsWork()
    {
        // Arrange: a pass over three databases, the second of which fails.
        var failure = new InvalidOperationException("database 2 failed");
        var visited = new List<int>();
        var worker = new ScriptedWorker((self, _) =>
        {
            for (int database = 1; database <= 3; database++)
            {
                try
                {
                    if (database == 2)
                    {
                        throw failure;
                    }

                    visited.Add(database);
                }
                catch (InvalidOperationException exception)
                {
                    self.Report(exception);
                }
            }

            return true;
        });

        // Act
        bool succeeded = worker.RunIteration(CancellationToken.None);

        // Assert
        succeeded.ShouldBeFalse();
        visited.ShouldBe([1, 3]);
        worker.Fault.ShouldBeSameAs(failure);
        worker.ConsecutiveFailures.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Worker: a pass that leaves work for later keeps an earlier failure until a pass completes")]
    public void RunIteration_IncompletePass_ShouldKeepTheFaultUntilAPassCompletes()
    {
        // Arrange: a failure, then a pass that leaves its work unfinished, then a complete pass.
        var failure = new InvalidOperationException("checkpoint failed");
        var worker = new ScriptedWorker((_, pass) => pass switch
        {
            1 => throw failure,
            2 => false,
            _ => true,
        });

        // Act
        worker.RunIteration(CancellationToken.None);
        bool incomplete = worker.RunIteration(CancellationToken.None);
        var faultAfterTheIncompletePass = worker.Fault;
        int consecutiveAfterTheIncompletePass = worker.ConsecutiveFailures;
        bool complete = worker.RunIteration(CancellationToken.None);

        // Assert: the unfinished pass is no failure, and no proof of health either.
        incomplete.ShouldBeTrue();
        faultAfterTheIncompletePass.ShouldBeSameAs(failure);
        consecutiveAfterTheIncompletePass.ShouldBe(1);
        complete.ShouldBeTrue();
        worker.Fault.ShouldBeNull();
        worker.ConsecutiveFailures.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Worker: cancellation is thrown and recorded as nothing; a cancellation nobody asked for is a failure")]
    public void RunIteration_Cancellation_ShouldThrowOnlyWhenTheTokenAskedForIt()
    {
        // Arrange
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var cancelled = new ScriptedWorker((_, _) => throw new OperationCanceledException(stop.Token));
        var stray = new ScriptedWorker((_, _) => throw new OperationCanceledException("a timeout of the pass's own"));

        // Act
        Should.Throw<OperationCanceledException>(() => cancelled.RunIteration(stop.Token));
        bool strayResult = stray.RunIteration(CancellationToken.None);

        // Assert
        cancelled.Fault.ShouldBeNull();
        cancelled.FailureCount.ShouldBe(0);
        strayResult.ShouldBeFalse();
        stray.Fault.ShouldBeOfType<OperationCanceledException>();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Worker: an out-of-memory failure escapes the pass")]
    public void RunIteration_OutOfMemory_ShouldEscape()
    {
        // Arrange
        var worker = new ScriptedWorker((_, _) => throw new OutOfMemoryException());

        // Act & Assert
        Should.Throw<OutOfMemoryException>(() => worker.RunIteration(CancellationToken.None));
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Worker: a failure that never clears keeps one record, and retries are paced by the backoff")]
    public void Run_PersistentFailure_ShouldKeepRunningAtTheBackoffPace()
    {
        // Arrange: a worker whose trigger fires every millisecond and whose every pass fails.
        var worker = new ScriptedWorker((_, pass) => throw new InvalidOperationException($"pass {pass} failed"), TimeSpan.FromMilliseconds(1));
        using var stop = new CancellationTokenSource();
        var thread = StartPump(worker, stop.Token);
        var watch = Stopwatch.StartNew();

        // Act: let it fail for about three backoffs.
        bool retried = SpinUntil(() => worker.ConsecutiveFailures >= 3);
        stop.Cancel();
        bool stopped = thread.Join(Timeout);
        var elapsed = watch.Elapsed;

        // Assert: still retrying, one failure held (the last), and no faster than the backoff: a
        // trigger of one millisecond would otherwise have run thousands of passes.
        retried.ShouldBeTrue();
        stopped.ShouldBeTrue();
        worker.FailureCount.ShouldBe(worker.Passes);
        worker.ConsecutiveFailures.ShouldBe(worker.Passes);
        worker.Fault.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe($"pass {worker.Passes} failed");
        ((double)worker.FailureCount).ShouldBeLessThanOrEqualTo(elapsed / DatabaseEngineWorker.FailureBackoff + 1);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Worker: a transient failure leaves the worker running and healthy once it clears")]
    public void Run_TransientFailure_ShouldRecover()
    {
        // Arrange: the first two passes fail, every later one completes.
        var worker = new ScriptedWorker((_, pass) => pass <= 2 ? throw new InvalidOperationException("transient") : true, TimeSpan.FromMilliseconds(1));
        using var stop = new CancellationTokenSource();
        var thread = StartPump(worker, stop.Token);

        // Act
        bool recovered = SpinUntil(() => worker.Passes > 3 && worker.Fault is null);
        stop.Cancel();
        thread.Join(Timeout).ShouldBeTrue();

        // Assert
        recovered.ShouldBeTrue();
        worker.ConsecutiveFailures.ShouldBe(0);
        worker.FailureCount.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Worker: a trigger wait that throws is a failed pass, and the loop goes on")]
    public void Run_TriggerThrows_ShouldRecordItAndKeepRunning()
    {
        // Arrange
        var worker = new ScriptedWorker((_, _) => true, TimeSpan.FromMilliseconds(1)) { FailTriggers = 1 };
        using var stop = new CancellationTokenSource();
        var thread = StartPump(worker, stop.Token);

        // Act
        bool ran = SpinUntil(() => worker.Passes >= 2 && worker.Fault is null);
        stop.Cancel();
        thread.Join(Timeout).ShouldBeTrue();

        // Assert
        ran.ShouldBeTrue();
        worker.FailureCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Worker: the pump stops at once when cancelled during its backoff")]
    public void Run_CancelledDuringTheBackoff_ShouldStopPromptly()
    {
        // Arrange
        var worker = new ScriptedWorker((_, _) => throw new InvalidOperationException("failed"), TimeSpan.FromMilliseconds(1));
        using var stop = new CancellationTokenSource();
        var thread = StartPump(worker, stop.Token);
        SpinUntil(() => worker.FailureCount >= 1).ShouldBeTrue();

        // Act
        var watch = Stopwatch.StartNew();
        stop.Cancel();
        bool stopped = thread.Join(Timeout);

        // Assert
        stopped.ShouldBeTrue();
        (watch.Elapsed / DatabaseEngineWorker.FailureBackoff).ShouldBeLessThan(0.5);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Worker: reporting a null failure is refused")]
    public void ReportFailure_Null_ShouldThrow()
    {
        // Arrange
        var worker = new ScriptedWorker((self, _) =>
        {
            self.Report(null!);
            return true;
        });

        // Act
        bool succeeded = worker.RunIteration(CancellationToken.None);

        // Assert: the refusal failed the pass like any other exception.
        succeeded.ShouldBeFalse();
        worker.Fault.ShouldBeOfType<ArgumentNullException>();
    }

    private static Thread StartPump(DatabaseEngineWorker worker, CancellationToken cancellationToken)
    {
        var thread = new Thread(() => worker.Run(cancellationToken)) { IsBackground = true, Name = worker.Name };
        thread.Start();
        return thread;
    }

    private static bool SpinUntil(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > Timeout)
            {
                return false;
            }

            Thread.Sleep(5);
        }

        return true;
    }

    /// <summary>A worker whose passes run a script given the pass number.</summary>
    private sealed class ScriptedWorker : DatabaseEngineWorker
    {
        private readonly Func<ScriptedWorker, int, bool> _pass;
        private readonly TimeSpan _interval;
        private int _passes;
        private int _failTriggers;

        public ScriptedWorker(Func<ScriptedWorker, int, bool> pass, TimeSpan? interval = null)
        {
            _pass = pass;
            _interval = interval ?? TimeSpan.FromHours(1);
        }

        /// <summary>Gets or sets the number of upcoming trigger waits that throw.</summary>
        public int FailTriggers
        {
            get => Volatile.Read(ref _failTriggers);
            init => _failTriggers = value;
        }

        public int Passes => Volatile.Read(ref _passes);

        public override string Name => "scripted";

        public override DatabaseEngineWorkerKind Kind => DatabaseEngineWorkerKind.Checkpoint;

        public override TimeSpan Interval => _interval;

        public void Report(Exception exception) => ReportFailure(exception);

        protected override bool RunIterationCore(CancellationToken cancellationToken)
            => _pass(this, Interlocked.Increment(ref _passes));

        protected override void WaitForTrigger(CancellationToken cancellationToken)
        {
            if (Interlocked.Decrement(ref _failTriggers) >= 0)
            {
                throw new InvalidOperationException("trigger failed");
            }

            base.WaitForTrigger(cancellationToken);
        }
    }
}
