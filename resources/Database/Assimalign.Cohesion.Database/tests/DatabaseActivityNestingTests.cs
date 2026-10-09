using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Tests;

/// <summary>
/// The Database sources read as one trace (the event-sources plan, section 8): with the runtime's
/// activity tracking on, the kernel's events a statement causes are written inside the root's
/// <c>StatementStart</c>/<c>StatementStop</c> pair and carry the statement's activity id, across the
/// root, Transactions and Storage sources.
/// </summary>
/// <remarks>
/// It runs in the root source's non-parallel collection: the sources and the runtime's activity
/// tracking are process-wide. Every assertion reads only this test's database, so another test's
/// events cannot satisfy or break it.
/// </remarks>
[Collection(nameof(DatabaseEventSourceCollection))]
public sealed class DatabaseActivityNestingTests
{
    private readonly ITestOutputHelper _output;

    public DatabaseActivityNestingTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Event sources: An in-process Sql INSERT writes the kernel's transaction and storage events inside its statement's activity")]
    public async Task ExecuteAsync_SqlInsertUnderActivityTracking_ShouldNestKernelEventsUnderTheStatement()
    {
        // Arrange: a file-backed engine whose commits wait for a grouped flush with a one-tick window
        // (the engine refuses zero), so the commit the INSERT runs writes the Storage source's
        // GroupCommitWindowMissed on the statement's own flow. The flush worker can still win the
        // race: a committer descheduled between registering its LSN and checking it finds the
        // worker's flush already durable and writes nothing, so the INSERT is retried until one
        // commit misses its window, and every attempt's kernel events are checked.
        const int MaxAttempts = 5;
        string name = "nesting" + Guid.NewGuid().ToString("N");
        string root = Path.Combine(Path.GetTempPath(), "wf-trace-int-nesting", Guid.NewGuid().ToString("N"));
        try
        {
            await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions
            {
                EngineName = name,
                RootPath = root,
                Durability = StorageCommitDurability.Grouped,
                GroupCommitWindow = TimeSpan.FromTicks(1),
            });
            var database = await engine.CreateDatabaseAsync(name, CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            (await session.ExecuteAsync("CREATE TABLE items (id INT NOT NULL)")).Status.ShouldBe(QueryResultStatus.Success);
            using var recorder = new ActivityRecorder();
            bool missedWindow = false;

            for (int attempt = 1; attempt <= MaxAttempts && !missedWindow; attempt++)
            {
                int before = recorder.Count;

                // Act
                var inserted = await session.ExecuteAsync($"INSERT INTO items (id) VALUES ({attempt})");

                // Assert
                inserted.Status.ShouldBe(QueryResultStatus.Success);
                EventWrittenEventArgs[] events = recorder.Events.Skip(before).Where(e => IsFor(e, name)).ToArray();
                foreach (var written in events)
                {
                    _output.WriteLine($"attempt {attempt}: {written.EventSource.Name}/{written.EventName} activity={written.ActivityId} related={written.RelatedActivityId}");
                }

                events.ShouldNotContain(e => e.EventId == 0, "An event source reported an instrumentation error.");
                int start = Array.FindIndex(events, e => e.EventSource.Name == ActivityRecorder.Root && e.EventName == "StatementStart");
                int stop = Array.FindLastIndex(events, e => e.EventSource.Name == ActivityRecorder.Root && e.EventName == "StatementStop");
                start.ShouldBeGreaterThanOrEqualTo(0, "The root wrote no StatementStart for the INSERT.");
                stop.ShouldBeGreaterThan(start, "The root wrote no StatementStop after the INSERT's start.");

                Guid statement = events[start].ActivityId;
                statement.ShouldNotBe(Guid.Empty, "Activity tracking gave the statement no activity id.");
                events[stop].ActivityId.ShouldBe(statement, "The stop closes the statement's own activity.");

                EventWrittenEventArgs[] inside = events[(start + 1)..stop];
                var begun = inside.Where(e => e.EventSource.Name == ActivityRecorder.Transactions && e.EventName == "TransactionBegun").ShouldHaveSingleItem();
                var committed = inside.Where(e => e.EventSource.Name == ActivityRecorder.Transactions && e.EventName == "TransactionCommitted").ShouldHaveSingleItem();
                begun.ActivityId.ShouldBe(statement, "The kernel transaction's begin is not nested under the statement.");
                committed.ActivityId.ShouldBe(statement, "The kernel transaction's commit is not nested under the statement.");

                // At most one missed window per commit, and it is the statement's own.
                var flush = inside.Where(e => e.EventSource.Name == ActivityRecorder.Storage && e.EventName == "GroupCommitWindowMissed").ToArray();
                flush.Length.ShouldBeLessThanOrEqualTo(1);
                flush.ShouldAllBe(e => e.ActivityId == statement, "The commit's storage event is not nested under the statement.");
                missedWindow = flush.Length == 1;

                // A flush worker's group flush may fall inside the pair; it runs on the worker's flow, so
                // it never carries the statement's activity.
                inside.Where(e => e.EventName is "PendingCommitsFlushed" or "WorkerPassStart" or "WorkerPassStop")
                    .ShouldAllBe(e => e.ActivityId != statement);
            }

            missedWindow.ShouldBeTrue($"No commit of {MaxAttempts} INSERTs missed its group-commit window, so no Storage event was written on the statement's flow.");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // A file a background worker still held is left for the temp folder's own cleanup.
            }
        }
    }

    // The events of this test's engine and database, which share the test's unique name: the root's
    // statement events and the kernel's events name the database first (the kernel's by its
    // storage's name), the engine's worker passes their worker, named for the engine.
    private static bool IsFor(EventWrittenEventArgs written, string database)
        => written.Payload is { Count: > 0 } payload
            && payload[0] is string first
            && first.Contains(database, StringComparison.Ordinal);

    /// <summary>
    /// Enables the runtime's task-flow activity ids and the root, Transactions and Storage sources by
    /// name, and records what the three write.
    /// </summary>
    private sealed class ActivityRecorder : EventListener
    {
        public const string Root = "Assimalign.Cohesion.Database";
        public const string Transactions = "Assimalign.Cohesion.Database.Transactions";
        public const string Storage = "Assimalign.Cohesion.Database.Storage";
        private const string TaskSource = "System.Threading.Tasks.TplEventSource";

        // TplEventSource's TasksFlowActivityIds keyword: it turns on EventSource's activity tracking.
        private const EventKeywords TasksFlowActivityIds = (EventKeywords)0x80;

        // Initialized before the base constructor calls OnEventSourceCreated for existing sources.
        private readonly ConcurrentQueue<EventWrittenEventArgs> _events = new();

        public IReadOnlyList<EventWrittenEventArgs> Events => _events.ToArray();

        public int Count => _events.Count;

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == TaskSource)
            {
                EnableEvents(eventSource, EventLevel.Informational, TasksFlowActivityIds);
            }
            else if (eventSource.Name is Root or Transactions or Storage)
            {
                EnableEvents(eventSource, EventLevel.Verbose, EventKeywords.All);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventSource.Name is Root or Transactions or Storage)
            {
                // Read on the writing thread: an event that is not a Start or Stop takes its activity
                // id from the current thread when the property is first read, and caches it.
                _ = eventData.ActivityId;
                _events.Enqueue(eventData);
            }
        }
    }
}
