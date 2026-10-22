using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(nameof(SqlWorkerResilienceTests))]
public sealed class ProvisionalFlushLaneTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Provisional flush: collection rechecks commits and publication preserves file-set lane ownership")]
    public async Task Run_CommitAfterDetachedProvisionalFlush_ShouldRecheckWithoutOverlappingPublication()
    {
        await VerifyTransitionAsync(publish: false);
        await VerifyTransitionAsync(publish: true);
    }

    private static async Task VerifyTransitionAsync(bool publish)
    {
        // Arrange: hold publication after construction's normal commits. The real WAL pump
        // still sees the provisional storage snapshot while the engine registry lock is held.
        var strategy = new FaultInjectingJournalSqlStorageStrategy(durable: true);
        await using var engine = SqlDatabaseEngine.Create("provisional-lane", new SqlDatabaseEngineOptions
        {
            StorageStrategy = strategy, Durability = StorageCommitDurability.Grouped,
            GroupCommitWindow = TimeSpan.FromSeconds(5), CheckpointInterval = TimeSpan.FromHours(1),
            PageWriteBackInterval = TimeSpan.FromHours(1), MaintenanceInterval = TimeSpan.FromHours(1),
        });
        using var publicationEntered = new ManualResetEventSlim();
        using var publishRelease = new ManualResetEventSlim();
        using var flushEntered = new ManualResetEventSlim();
        using var flushRelease = new ManualResetEventSlim();
        using var registered = new ManualResetEventSlim();
        using var commits = new GroupCommitRecorder();
        strategy.BeforeCreationComplete = () => { publicationEntered.Set(); publishRelease.Wait(); };
        var worker = engine.Workers.OfType<SqlWriteAheadFlushWorker>().Single();
        var passes = new ConcurrentQueue<DatabaseWorkerPass>();
        worker.PassCompleted = passes.Enqueue;
        var create = DedicatedTestTask.Run(async () => await engine.CreateDatabaseAsync("constructing"));
        Task first = Task.CompletedTask;
        Task second = Task.CompletedTask;
        int publishedAttempts = 0;
        try
        {
            publicationEntered.Wait(TimeSpan.FromSeconds(30)).ShouldBeTrue();
            var storage = engine.GetStorageSnapshot().Single(current => current.Name.ToString() == "constructing");
            int held = 0;
            commits.Flushed = name =>
            {
                if (name == "constructing" && Interlocked.CompareExchange(ref held, 1, 0) == 0)
                {
                    flushEntered.Set(); flushRelease.Wait();
                }
            };
            void CommitEmpty()
            {
                long sequence = storage.ReserveTransactionSequence();
                storage.WriteAheadJournal.AppendBegin(sequence);
                long lsn = storage.WriteAheadJournal.AppendCommit(sequence);
                storage.EnsureCommitDurable(lsn);
            }

            // Act: first flush acknowledged its commit, but its method has not returned.
            first = DedicatedTestTask.Run(CommitEmpty);
            flushEntered.Wait(TimeSpan.FromSeconds(30)).ShouldBeTrue();
            await first.WaitAsync(TimeSpan.FromSeconds(30));
            if (publish)
            {
                publishRelease.Set();
                await create.WaitAsync(TimeSpan.FromSeconds(30));
                worker.BeforeDatabase = _ => Interlocked.Increment(ref publishedAttempts);
            }
            var pending = storage.OnCommitPending;
            storage.OnCommitPending = () => { pending?.Invoke(); registered.Set(); };
            int before = passes.Count;
            second = DedicatedTestTask.Run(CommitEmpty);
            registered.Wait(TimeSpan.FromSeconds(30)).ShouldBeTrue();
            string expectedVisit = publish ? "constructing" : "constructing (provisional)";
            var duration = Stopwatch.StartNew();
            while (!passes.Skip(before).Any(pass => pass.Visits.Any(visit =>
                visit.Database == expectedVisit && !visit.Attempted
                && visit.Reason == DatabaseWorkerPass.VisitReason.Running))
                && duration.Elapsed < TimeSpan.FromSeconds(2)) { await Task.Delay(5); }

            // Assert: publication cannot start a second flusher for the same file set.
            publishedAttempts.ShouldBe(0);
            passes.Skip(before).Any(pass => pass.Visits.Any(visit => visit.Database == expectedVisit
                && !visit.Attempted && visit.Reason == DatabaseWorkerPass.VisitReason.Running)).ShouldBeTrue();
            second.IsCompleted.ShouldBeFalse();
            flushRelease.Set();
            await second.WaitAsync(TimeSpan.FromSeconds(2));
            commits.MissedCount("constructing").ShouldBe(0);
        }
        finally
        {
            flushRelease.Set(); publishRelease.Set();
            worker.BeforeDatabase = null; worker.PassCompleted = null; commits.Flushed = null;
            await Task.WhenAll(first, second, create).WaitAsync(TimeSpan.FromSeconds(30));
        }
    }
}
