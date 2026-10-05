using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Tests;

/// <summary>
/// The guided worker base's pump and failure record (#1268): a database whose work failed is
/// recorded, skipped until its backoff passed, and cleared once a pass finishes its work, while
/// the worker's other databases keep its full pace; a pass that fails as a whole is recorded and
/// backed off; the loop keeps running through both. Before #1268 the first exception escaped the
/// loop and ended the worker for good, and before its review one database's failure slowed every
/// other database's work to one pass a second.
/// </summary>
public class DatabaseEngineWorkerTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Database] - Worker: a pass that throws is recorded, never thrown, and the next pass that runs to its end clears it")]
    public void RunIteration_PassThrows_ShouldRecordTheFailureUntilAPassRunsToItsEnd()
    {
        // Arrange
        var failure = new InvalidOperationException("pass failed");
        var worker = new ScriptedWorker((_, pass) =>
        {
            if (pass == 1)
            {
                throw failure;
            }
        });

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

    [Fact(DisplayName = "Cohesion Test [Database] - Worker: one database's failure fails the pass without stopping the other databases' work")]
    public void RunIteration_DatabaseFails_ShouldFailThePassAndFinishTheOthers()
    {
        // Arrange: a pass over three databases, the second of which fails.
        var failure = new InvalidOperationException("database b failed");
        var visited = new List<string>();
        var worker = new ScriptedWorker((self, _) =>
        {
            foreach (string database in new[] { "a", "b", "c" })
            {
                if (!self.Begin(database))
                {
                    continue;
                }

                if (database == "b")
                {
                    self.Fail(database, failure);
                    continue;
                }

                visited.Add(database);
            }
        });

        // Act
        bool succeeded = worker.RunIteration(CancellationToken.None);

        // Assert
        succeeded.ShouldBeFalse();
        visited.ShouldBe(["a", "c"]);
        worker.Fault.ShouldBeSameAs(failure);
        worker.ConsecutiveFailures.ShouldBe(1);
        worker.FailureCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Worker: a database whose failure is backing off is skipped, and tried again once its backoff passed")]
    public void RunIteration_DatabaseBackingOff_ShouldBeSkippedUntilItsBackoffPassed()
    {
        // Arrange: database a fails once with a short backoff; database b is healthy.
        var backoff = TimeSpan.FromMilliseconds(200);
        var failure = new InvalidOperationException("database a failed");
        var visits = new List<string>();
        var worker = new ScriptedWorker((self, pass) =>
        {
            foreach (string database in new[] { "a", "b" })
            {
                if (!self.Begin(database))
                {
                    continue;
                }

                lock (visits)
                {
                    visits.Add($"{pass}:{database}");
                }

                if (database == "a" && pass == 1)
                {
                    self.Fail(database, failure, backoff);
                }
            }
        });

        // Act: the failing pass, an immediate pass, then passes until database a is tried again.
        var watch = Stopwatch.StartNew();
        bool failed = worker.RunIteration(CancellationToken.None);
        bool skipped = worker.RunIteration(CancellationToken.None);
        var faultWhileBackingOff = worker.Fault;
        bool retried = SpinUntil(() =>
        {
            worker.RunIteration(CancellationToken.None);
            lock (visits)
            {
                return visits.Count(visit => visit.EndsWith(":a", StringComparison.Ordinal)) >= 2;
            }
        });
        var retriedAfter = watch.Elapsed;

        // Assert: the immediate pass skipped a, visited b, and reported nothing new; the retry
        // waited out the backoff and, completing a's work, cleared the failure.
        failed.ShouldBeFalse();
        skipped.ShouldBeTrue();
        visits.ShouldContain("2:b");
        visits.ShouldNotContain("2:a");
        faultWhileBackingOff.ShouldBeSameAs(failure);
        retried.ShouldBeTrue();
        (retriedAfter / backoff).ShouldBeGreaterThanOrEqualTo(1.0);
        worker.Fault.ShouldBeNull();
        worker.ConsecutiveFailures.ShouldBe(0);
        worker.FailureCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Worker: a database's work left unfinished keeps its failure until a pass finishes it")]
    public void RunIteration_UnfinishedDatabase_ShouldKeepItsFailureUntilItsWorkIsDone()
    {
        // Arrange: a fails, then its work is left for later once, then completes.
        var failure = new InvalidOperationException("checkpoint failed");
        var worker = new ScriptedWorker((self, pass) =>
        {
            self.Begin("a").ShouldBeTrue();
            switch (pass)
            {
                case 1:
                    self.Fail("a", failure, TimeSpan.Zero);
                    break;
                case 2:
                    self.Unfinished("a");
                    break;
            }
        });

        // Act
        worker.RunIteration(CancellationToken.None);
        bool unfinished = worker.RunIteration(CancellationToken.None);
        var faultAfterTheUnfinishedPass = worker.Fault;
        int consecutiveAfterTheUnfinishedPass = worker.ConsecutiveFailures;
        bool complete = worker.RunIteration(CancellationToken.None);

        // Assert: the unfinished pass is no failure, and no proof of health either.
        unfinished.ShouldBeTrue();
        faultAfterTheUnfinishedPass.ShouldBeSameAs(failure);
        consecutiveAfterTheUnfinishedPass.ShouldBe(1);
        complete.ShouldBeTrue();
        worker.Fault.ShouldBeNull();
        worker.ConsecutiveFailures.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Worker: another database's unfinished work does not keep a recovered database's failure")]
    public void RunIteration_OtherDatabaseUnfinished_ShouldNotKeepARecoveredFailure()
    {
        // Arrange: a fails once; b's work is left for later on every pass (a busy database).
        var failure = new InvalidOperationException("database a failed");
        var worker = new ScriptedWorker((self, pass) =>
        {
            self.Begin("a").ShouldBeTrue();
            if (pass == 1)
            {
                self.Fail("a", failure, TimeSpan.Zero);
            }

            self.Begin("b").ShouldBeTrue();
            self.Unfinished("b");
        });

        // Act
        worker.RunIteration(CancellationToken.None);
        var faultAfterTheFailure = worker.Fault;
        bool recovered = worker.RunIteration(CancellationToken.None);

        // Assert: b never failed, so its unfinished work holds nothing.
        faultAfterTheFailure.ShouldBeSameAs(failure);
        recovered.ShouldBeTrue();
        worker.Fault.ShouldBeNull();
        worker.ConsecutiveFailures.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Worker: a database a pass no longer begins (dropped, closed or offline) has its failure forgotten")]
    public void RunIteration_DatabaseNotBegun_ShouldForgetItsFailure()
    {
        // Arrange
        var failure = new InvalidOperationException("database a failed");
        var worker = new ScriptedWorker((self, pass) =>
        {
            if (pass == 1)
            {
                self.Begin("a").ShouldBeTrue();
                self.Fail("a", failure);
            }

            self.Begin("b").ShouldBeTrue();
        });

        // Act
        worker.RunIteration(CancellationToken.None);
        var faultAfterTheFailure = worker.Fault;
        worker.RunIteration(CancellationToken.None);

        // Assert
        faultAfterTheFailure.ShouldBeSameAs(failure);
        worker.Fault.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Worker: the fault is the newest failure still held, and follows the databases that recover")]
    public void Fault_SeveralDatabasesFail_ShouldBeTheNewestFailureStillHeld()
    {
        // Arrange: a fails on pass 1 and stays failed; b fails on pass 2 and recovers on pass 3.
        var failureA = new InvalidOperationException("database a failed");
        var failureB = new InvalidOperationException("database b failed");
        var worker = new ScriptedWorker((self, pass) =>
        {
            self.Begin("a").ShouldBeTrue();
            self.Fail("a", failureA, TimeSpan.Zero);
            self.Begin("b").ShouldBeTrue();
            if (pass == 2)
            {
                self.Fail("b", failureB, TimeSpan.Zero);
            }
        });

        // Act
        worker.RunIteration(CancellationToken.None);
        var afterA = worker.Fault;
        worker.RunIteration(CancellationToken.None);
        var afterB = worker.Fault;
        worker.RunIteration(CancellationToken.None);

        // Assert: a's failure is reported again on pass 3, after b's recovery.
        afterA.ShouldBeSameAs(failureA);
        afterB.ShouldBeSameAs(failureB);
        worker.Fault.ShouldBeSameAs(failureA);
        worker.ConsecutiveFailures.ShouldBe(3);
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

    [Fact(DisplayName = "Cohesion Test [Database] - Worker: passes never overlap, even when a caller runs one beside the worker's thread")]
    public async Task RunIteration_CalledConcurrently_ShouldRunOnePassAtATime()
    {
        // Arrange
        int running = 0;
        int overlapped = 0;
        var worker = new ScriptedWorker((self, _) =>
        {
            if (Interlocked.Increment(ref running) > 1)
            {
                Interlocked.Increment(ref overlapped);
            }

            self.Begin("a");
            Thread.Sleep(1);
            Interlocked.Decrement(ref running);
        });

        // Act
        var callers = new Task[4];
        for (int index = 0; index < callers.Length; index++)
        {
            callers[index] = Task.Run(() =>
            {
                for (int pass = 0; pass < 25; pass++)
                {
                    worker.RunIteration(CancellationToken.None);
                }
            });
        }

        await Task.WhenAll(callers);

        // Assert
        overlapped.ShouldBe(0);
        worker.Passes.ShouldBe(100);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Worker: a database that keeps failing is retried at the backoff pace while the others keep the worker's full pace")]
    public void Run_DatabaseKeepsFailing_ShouldBackOffThatDatabaseOnly()
    {
        // Arrange: a trigger of one millisecond; database a fails on every attempt.
        int attemptsOnA = 0;
        int passesOverB = 0;
        var worker = new ScriptedWorker((self, _) =>
        {
            if (self.Begin("a"))
            {
                int attempt = Interlocked.Increment(ref attemptsOnA);
                self.Fail("a", new InvalidOperationException($"attempt {attempt} failed"));
            }

            if (self.Begin("b"))
            {
                Interlocked.Increment(ref passesOverB);
            }
        }, TimeSpan.FromMilliseconds(1));
        using var stop = new CancellationTokenSource();
        var thread = StartPump(worker, stop.Token);
        var watch = Stopwatch.StartNew();

        // Act: let a fail for about two backoffs.
        bool retried = SpinUntil(() => Volatile.Read(ref attemptsOnA) >= 3);
        stop.Cancel();
        bool stopped = thread.Join(_timeout);
        var elapsed = watch.Elapsed;
        int attempts = Volatile.Read(ref attemptsOnA);
        int passes = Volatile.Read(ref passesOverB);

        // Assert: a was retried no faster than the backoff, and b ran many passes per retry of a
        // (a one-millisecond trigger gives hundreds a second; a worker-wide backoff gave one).
        retried.ShouldBeTrue();
        stopped.ShouldBeTrue();
        ((double)attempts).ShouldBeLessThanOrEqualTo(elapsed / DatabaseEngineWorker.FailureBackoff + 1);
        worker.FailureCount.ShouldBe(attempts);
        worker.Fault.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe($"attempt {attempts} failed");
        ((double)passes / attempts).ShouldBeGreaterThan(10, $"{passes} passes over b, {attempts} attempts on a in {elapsed}");
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Worker: a pass that keeps throwing keeps one record, and retries are paced by the backoff")]
    public void Run_PassKeepsThrowing_ShouldKeepRunningAtTheBackoffPace()
    {
        // Arrange: a worker whose trigger fires every millisecond and whose every pass throws.
        var worker = new ScriptedWorker((_, pass) => throw new InvalidOperationException($"pass {pass} failed"), TimeSpan.FromMilliseconds(1));
        using var stop = new CancellationTokenSource();
        var thread = StartPump(worker, stop.Token);
        var watch = Stopwatch.StartNew();

        // Act: let it fail for about three backoffs.
        bool retried = SpinUntil(() => worker.ConsecutiveFailures >= 3);
        stop.Cancel();
        bool stopped = thread.Join(_timeout);
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
        // Arrange: the first two passes throw, every later one runs to its end.
        var worker = new ScriptedWorker((_, pass) =>
        {
            if (pass <= 2)
            {
                throw new InvalidOperationException("transient");
            }
        }, TimeSpan.FromMilliseconds(1));
        using var stop = new CancellationTokenSource();
        var thread = StartPump(worker, stop.Token);

        // Act
        bool recovered = SpinUntil(() => worker.Passes > 3 && worker.Fault is null);
        stop.Cancel();
        thread.Join(_timeout).ShouldBeTrue();

        // Assert
        recovered.ShouldBeTrue();
        worker.ConsecutiveFailures.ShouldBe(0);
        worker.FailureCount.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Worker: a trigger wait that throws is a failure of the worker, and the loop goes on")]
    public void Run_TriggerThrows_ShouldRecordItAndKeepRunning()
    {
        // Arrange
        var worker = new ScriptedWorker((_, _) => { }, TimeSpan.FromMilliseconds(1)) { FailTriggers = 1 };
        using var stop = new CancellationTokenSource();
        var thread = StartPump(worker, stop.Token);

        // Act
        bool ran = SpinUntil(() => worker.Passes >= 2 && worker.Fault is null);
        stop.Cancel();
        thread.Join(_timeout).ShouldBeTrue();

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
        bool stopped = thread.Join(_timeout);

        // Assert
        stopped.ShouldBeTrue();
        (watch.Elapsed / DatabaseEngineWorker.FailureBackoff).ShouldBeLessThan(0.5);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Worker: reporting a null failure, or a negative backoff, fails the pass as a whole")]
    public void ReportFailure_InvalidArguments_ShouldFailThePass()
    {
        // Arrange
        var nullFailure = new ScriptedWorker((self, _) => self.Fail("a", null!));
        var negativeBackoff = new ScriptedWorker((self, _) => self.Fail("a", new InvalidOperationException("failed"), TimeSpan.FromSeconds(-1)));

        // Act
        bool nullResult = nullFailure.RunIteration(CancellationToken.None);
        bool negativeResult = negativeBackoff.RunIteration(CancellationToken.None);

        // Assert: the refusals failed the passes like any other exception.
        nullResult.ShouldBeFalse();
        nullFailure.Fault.ShouldBeOfType<ArgumentNullException>();
        negativeResult.ShouldBeFalse();
        negativeBackoff.Fault.ShouldBeOfType<ArgumentOutOfRangeException>();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Worker: reporting on a database outside a pass is refused")]
    public void ReportFailure_OutsideAPass_ShouldThrow()
    {
        // Arrange
        var worker = new ScriptedWorker((_, _) => { });

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => worker.Begin("a"));
        Should.Throw<InvalidOperationException>(() => worker.Fail("a", new InvalidOperationException("failed")));
        Should.Throw<InvalidOperationException>(() => worker.Unfinished("a"));
        worker.Fault.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Worker: the name, kind and cadence are the constructor's, through the interface too")]
    public void Constructor_Identity_ShouldBeFixed()
    {
        // Arrange / Act
        var worker = new ScriptedWorker((_, _) => { }, TimeSpan.FromSeconds(5), "sql-engine/version-purge", DatabaseEngineWorkerKind.VersionPurge);
        IDatabaseEngineWorker bridged = worker;

        // Assert
        worker.Name.ShouldBe("sql-engine/version-purge");
        worker.Kind.ShouldBe(DatabaseEngineWorkerKind.VersionPurge);
        worker.Interval.ShouldBe(TimeSpan.FromSeconds(5));
        bridged.Name.ShouldBe(worker.Name);
        bridged.Kind.ShouldBe(worker.Kind);
        bridged.Interval.ShouldBe(worker.Interval);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Worker: a worker without a diagnostic name is refused")]
    public void Constructor_BlankName_ShouldThrow()
    {
        // Act & Assert
        Should.Throw<ArgumentException>(() => new ScriptedWorker((_, _) => { }, name: " "));
        Should.Throw<ArgumentNullException>(() => new ScriptedWorker((_, _) => { }, name: null!));
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
            if (watch.Elapsed > _timeout)
            {
                return false;
            }

            Thread.Sleep(5);
        }

        return true;
    }
}
