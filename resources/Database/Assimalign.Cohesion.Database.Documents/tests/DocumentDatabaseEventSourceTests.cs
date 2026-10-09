using System;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Documents.Tests;

/// <summary>
/// Serializes the tests that observe the document model's event source: the source is
/// process-wide.
/// </summary>
[CollectionDefinition(nameof(DocumentDatabaseEventSourceCollection), DisableParallelization = true)]
public class DocumentDatabaseEventSourceCollection
{
}

/// <summary>
/// The document model's event source against the repository's EventSource convention, and the
/// index recovery a reopened database reports (database event-sources plan, section 4.10). The
/// source declares no counters.
/// </summary>
[Collection(nameof(DocumentDatabaseEventSourceCollection))]
public sealed class DocumentDatabaseEventSourceTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - DocumentDatabaseEventSource: Should be named for its assembly")]
    public void GetName_DocumentDatabaseEventSource_ShouldEqualAssemblyName()
    {
        // Act
        string name = EventSource.GetName(typeof(DocumentDatabaseEventSource));

        // Assert
        name.ShouldBe(typeof(DocumentDatabaseEventSource).Assembly.GetName().Name);
        name.ShouldBe("Assimalign.Cohesion.Database.Documents");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Documents] - DocumentDatabaseEventSource: Should generate a manifest in strict mode")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111:DynamicallyAccessedMembers", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    public void GenerateManifest_StrictMode_ShouldSucceed()
    {
        // Act
        string? manifest = EventSource.GenerateManifest(typeof(DocumentDatabaseEventSource), assemblyPathToIncludeInManifest: null, EventManifestOptions.Strict);

        // Assert
        manifest.ShouldNotBeNull();
        manifest.ShouldContain("Assimalign.Cohesion.Database.Documents", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Documents] - DocumentDatabaseEventSource: Should report the index recovery of a database reopened with an aborted writer once")]
    public async Task IndexRecovery_ReopenWithAbortedWriter_ShouldReportStartAndStopOnce()
    {
        // Arrange: quiet workers, and a transaction whose write reached the journal (a page
        // write-back pass flushes the journal ahead of the pages) when the database went offline
        // under it, so its close writes nothing and the reopen recovers it.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        string name = "recovery" + Guid.NewGuid().ToString("N");
        await using var engine = DocumentDatabaseEngine.Create(new DocumentDatabaseEngineOptions
        {
            EngineName = "documents-events-" + Guid.NewGuid().ToString("N"),
            CheckpointInterval = TimeSpan.FromHours(1),
            PageWriteBackInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
        });
        var database = await engine.CreateDatabaseAsync(name, token);
        var session = await database.CreateSessionAsync(token);
        var items = await session.CreateCollectionAsync("items", token);
        await session.BeginTransactionAsync(token);
        await items.PutAsync(session, "aborted", Encoding.UTF8.GetBytes("{\"id\":\"aborted\"}"), cancellationToken: token);
        foreach (var worker in engine.Workers.OfType<DatabaseEngineWorker>().Where(worker => worker.Kind == DatabaseEngineWorkerKind.PageWriteBack))
        {
            worker.RunIteration(CancellationToken.None).ShouldBeTrue(worker.Fault?.ToString());
        }

        database.DataStorage.TakeOffline(StorageOfflineCause.CheckpointFailures, "the test took the storage offline", new IOException("Injected device failure")).ShouldBeTrue();
        await session.DisposeAsync();
        using var recorder = new EventSourceRecorder(DocumentDatabaseEventSource.Log, EventLevel.Informational);

        // Act
        var reopened = await engine.OpenDatabaseAsync(name, token);

        // Assert
        reopened.ShouldNotBeSameAs(database);
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        var start = recorder.Events.Where(e => e.EventId == 1 && Equals(e.Payload![0], name)).ShouldHaveSingleItem();
        start.EventName.ShouldBe("IndexRecoveryStart");
        start.Level.ShouldBe(EventLevel.Informational);
        start.PayloadNames.ShouldBe(["database", "abortedWriters"]);
        ((int)start.Payload![1]!).ShouldBeGreaterThanOrEqualTo(1);
        var stop = recorder.Events.Where(e => e.EventId == 2 && Equals(e.Payload![0], name)).ShouldHaveSingleItem();
        stop.EventName.ShouldBe("IndexRecoveryStop");
        stop.Level.ShouldBe(EventLevel.Informational);
        stop.PayloadNames.ShouldBe(["database", "status", "durationMilliseconds"]);
        stop.Payload![1].ShouldBe("Success");
        ((double)stop.Payload![2]!).ShouldBeGreaterThan(0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Documents] - DocumentDatabaseEventSource: Should not report a database it creates, which has nothing to recover")]
    public async Task IndexRecovery_CreatedDatabase_ShouldNotBeReported()
    {
        // Arrange
        using var recorder = new EventSourceRecorder(DocumentDatabaseEventSource.Log, EventLevel.Verbose);
        await using var engine = DocumentDatabaseEngine.Create(new DocumentDatabaseEngineOptions { EngineName = "documents-events-" + Guid.NewGuid().ToString("N") });
        string name = "created" + Guid.NewGuid().ToString("N");

        // Act
        await engine.CreateDatabaseAsync(name, CancellationToken.None);

        // Assert
        recorder.Events.ShouldNotContain(e => Equals(e.Payload![0], name));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Documents] - DocumentDatabaseEventSource: Should allocate nothing and read no timestamp while nobody listens")]
    public void Writes_NoListener_ShouldAllocateNothing()
    {
        // Arrange: a disabled source. Disposing the last listener already disables it; the explicit
        // disable below is redundant but harmless.
        var database = new DatabaseName("documents");
        using (var listener = new EventSourceRecorder(DocumentDatabaseEventSource.Log, EventLevel.Verbose))
        {
            listener.DisableEvents(DocumentDatabaseEventSource.Log);
        }

        DocumentDatabaseEventSource.Log.IsEnabled().ShouldBeFalse();
        DocumentDatabaseEventSource.Log.IndexRecoveryStop(database, true, DocumentDatabaseEventSource.Log.IndexRecoveryStart(database, 1));

        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        long started = DocumentDatabaseEventSource.Log.IndexRecoveryStart(database, 1);
        DocumentDatabaseEventSource.Log.IndexRecoveryStop(database, true, started);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        allocated.ShouldBe(0);
        started.ShouldBe(0);
    }
}
