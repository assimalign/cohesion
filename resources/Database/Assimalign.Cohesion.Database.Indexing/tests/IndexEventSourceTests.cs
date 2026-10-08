using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Indexing.Internal;
using Assimalign.Cohesion.Database.Indexing.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Indexing.Tests;

/// <summary>
/// Serializes the tests that observe the Indexing event source: the source is process-wide.
/// </summary>
[CollectionDefinition(nameof(IndexEventSourceCollection), DisableParallelization = true)]
public class IndexEventSourceCollection
{
}

/// <summary>
/// The B+Tree index's event source against the repository's EventSource convention
/// (<c>.claude/rules/event-source.md</c>) and the plan of record's catalog
/// (<c>docs/programs/DATABASE_EVENT_SOURCES_PLAN.md</c>, §4.4): each event is raised once by a real
/// operation on a real tree over a real storage, with its declared payload.
/// </summary>
/// <remarks>
/// Every harness storage here has its own name, and the assertions read only that database's
/// events. The source declares no counters, so there is no counter test.
/// </remarks>
[Collection(nameof(IndexEventSourceCollection))]
public sealed class IndexEventSourceTests
{
    private const ulong ObjectId = 9;

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - IndexEventSource: Should be named for its assembly")]
    public void GetName_IndexEventSource_ShouldEqualAssemblyName()
    {
        // Act
        string name = EventSource.GetName(typeof(IndexEventSource));

        // Assert
        name.ShouldBe(typeof(IndexEventSource).Assembly.GetName().Name);
        name.ShouldBe("Assimalign.Cohesion.Database.Indexing");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - IndexEventSource: Should generate a manifest in strict mode")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111:DynamicallyAccessedMembers", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    public void GenerateManifest_StrictMode_ShouldSucceed()
    {
        // Act
        string? manifest = EventSource.GenerateManifest(typeof(IndexEventSource), assemblyPathToIncludeInManifest: null, EventManifestOptions.Strict);

        // Assert
        manifest.ShouldNotBeNull();
        manifest.ShouldContain("Assimalign.Cohesion.Database.Indexing", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - IndexEventSource: Should report an index created and dropped once each")]
    public async Task CreateAndDropIndex_UnderListener_ShouldReportEachOnce()
    {
        // Arrange
        string name = UniqueName();
        await using var harness = new IndexTestHarness(name: name);
        using var recorder = new IndexEventRecorder(EventLevel.Verbose);

        // Act
        var transaction = await harness.BeginAsync();
        await harness.IndexManager.CreateIndexAsync(transaction, ObjectId, new IndexDefinition("ix_lifecycle"));
        await harness.IndexManager.DropIndexAsync(transaction, ObjectId, "ix_lifecycle");
        await harness.CommitAsync(transaction);

        // Assert
        recorder.ShouldHaveNoInstrumentationError();
        var events = recorder.For(name);
        events.Select(e => e.EventName).ShouldBe(["IndexCreated", "IndexDropped"]);

        events[0].EventId.ShouldBe(1);
        events[0].Level.ShouldBe(EventLevel.Verbose);
        events[0].PayloadNames.ShouldBe(["database", "objectId", "index", "kind"]);
        events[0].Payload.ShouldBe([name, ObjectId, "ix_lifecycle", nameof(IndexKind.BTree)]);

        events[1].EventId.ShouldBe(2);
        events[1].Level.ShouldBe(EventLevel.Verbose);
        events[1].PayloadNames.ShouldBe(["database", "objectId", "index"]);
        events[1].Payload.ShouldBe([name, ObjectId, "ix_lifecycle"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - IndexEventSource: Should report the first leaf split of a root and the root's growth once each")]
    public async Task InsertAsync_RootLeafFills_ShouldReportOneSplitAndOneRootGrowth()
    {
        // Arrange: a single-leaf tree of wide keys, about fifteen to a leaf.
        string name = UniqueName();
        var (harness, index) = await CreateIndexAsync(name, "ix_split");
        await using var harnessLifetime = harness;
        long root = harness.IndexManager.ExportRegistrations().Single().RootPageId;
        using var recorder = new IndexEventRecorder(EventLevel.Verbose);

        // Act: insert until the root leaf splits.
        var writer = await harness.BeginAsync();
        for (long i = 0; i < 1_000 && !recorder.For(name).Any(e => e.EventName == "PageSplit"); i++)
        {
            await index.InsertAsync(writer, WideKey(i), (ulong)i);
        }

        await harness.CommitAsync(writer);

        // Assert: the root's leaf split, then the tree grew a level with its root in place.
        recorder.ShouldHaveNoInstrumentationError();
        var events = recorder.For(name);
        events.Select(e => e.EventName).ShouldBe(["PageSplit", "RootGrown"]);

        var split = events[0];
        split.EventId.ShouldBe(6);
        split.Level.ShouldBe(EventLevel.Verbose);
        Declared(split.Keywords).ShouldBe(IndexEventSource.Keywords.Splits);
        split.PayloadNames.ShouldBe(["database", "index", "pageId", "leaf", "entries"]);
        split.Payload!.Take(4).ShouldBe([name, "ix_split", root, true]);
        ((int)split.Payload![4]!).ShouldBeGreaterThan(1);

        var grown = events[1];
        grown.EventId.ShouldBe(7);
        grown.Level.ShouldBe(EventLevel.Verbose);
        Declared(grown.Keywords).ShouldBe(IndexEventSource.Keywords.Splits);
        grown.PayloadNames.ShouldBe(["database", "index", "rootPageId"]);
        grown.Payload.ShouldBe([name, "ix_split", root]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - IndexEventSource: Should report an internal node's split as a split of a non-leaf page")]
    public async Task InsertAsync_RootInternalNodeFills_ShouldReportAnInternalSplit()
    {
        // Arrange: wide keys keep about fifteen separators to an internal node, so a few hundred
        // inserts fill the internal root (BTreeIndexTests.WideKey).
        string name = UniqueName();
        var (harness, index) = await CreateIndexAsync(name, "ix_internal");
        await using var harnessLifetime = harness;
        long root = harness.IndexManager.ExportRegistrations().Single().RootPageId;
        using var recorder = new IndexEventRecorder(EventLevel.Verbose);

        // Act: insert until an internal node splits.
        var writer = await harness.BeginAsync();
        for (long i = 0; i < 5_000 && !recorder.For(name).Any(IsInternalSplit); i++)
        {
            await index.InsertAsync(writer, WideKey(i), (ulong)i);
        }

        await harness.CommitAsync(writer);

        // Assert: the first internal split is the root's, and it grew the tree a second level.
        recorder.ShouldHaveNoInstrumentationError();
        var events = recorder.For(name);
        var internalSplit = events.Where(IsInternalSplit).ShouldHaveSingleItem();
        internalSplit.Payload!.Take(4).ShouldBe([name, "ix_internal", root, false]);
        events.Count(e => e.EventName == "RootGrown").ShouldBe(2);
        events.Last().EventName.ShouldBe("RootGrown");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - IndexEventSource: Should report a damaged node an operation reaches, once per operation")]
    public async Task OpenCursor_DamagedLeaf_ShouldReportTheCorruptionOnce()
    {
        // Arrange: a tree with an internal root, one of whose leaves loses its format stamp.
        string name = UniqueName();
        await using var harness = new IndexTestHarness(name: name);
        var setup = await harness.BeginAsync();
        var index = await harness.IndexManager.CreateIndexAsync(setup, ObjectId, new IndexDefinition("ix_damaged"));
        for (long i = 0; i < 2_000; i++)
        {
            await index.InsertAsync(setup, IndexKey.FromInt64(i), (ulong)i);
        }

        await harness.CommitAsync(setup);
        long leaf = LeftmostLeaf(harness.Storage, harness.IndexManager.ExportRegistrations().Single().RootPageId);
        RewritePage(harness.Storage, leaf, LegacyBTreePages.DowngradeToFormat1);
        using var recorder = new IndexEventRecorder(EventLevel.Error);

        // Act
        var reader = await harness.BeginAsync();
        Should.Throw<IndexCorruptionException>(() => index.OpenCursor(reader, IndexKeyRange.All));

        // Assert
        recorder.ShouldHaveNoInstrumentationError();
        var corruption = recorder.For(name).ShouldHaveSingleItem();
        corruption.EventName.ShouldBe("IndexCorruptionDetected");
        corruption.EventId.ShouldBe(4);
        corruption.Level.ShouldBe(EventLevel.Error);
        corruption.PayloadNames.ShouldBe(["database", "index", "pageId", "formatVersion"]);
        corruption.Payload.ShouldBe([name, "ix_damaged", leaf, 1]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - IndexEventSource: Should report a tree refused at attach for its page format, once")]
    public async Task Create_ExistingTreeInFormat1_ShouldReportTheRefusalOnce()
    {
        // Arrange: a committed tree whose root page is rewritten into the format-1 layout.
        string name = UniqueName();
        var (harness, _) = await CreateIndexAsync(name, "ix_legacy", entries: 10);
        await using var harnessLifetime = harness;
        var registrations = harness.IndexManager.ExportRegistrations();
        long root = registrations.Single().RootPageId;
        RewritePage(harness.Storage, root, LegacyBTreePages.DowngradeToFormat1);
        using var recorder = new IndexEventRecorder(EventLevel.Error);

        // Act
        Should.Throw<IndexFormatException>(() => BTreeIndexManager.Create(new BTreeIndexManagerOptions
        {
            Storage = harness.Storage,
            TransactionSource = harness.GetStorageTransaction,
            LockManager = harness.LockManager,
            ExistingIndexes = registrations,
        }));

        // Assert
        recorder.ShouldHaveNoInstrumentationError();
        var refused = recorder.For(name).ShouldHaveSingleItem();
        refused.EventName.ShouldBe("IndexFormatRefused");
        refused.EventId.ShouldBe(3);
        refused.Level.ShouldBe(EventLevel.Error);
        refused.PayloadNames.ShouldBe(["database", "objectId", "index", "rootPageId", "foundFormat"]);
        refused.Payload.ShouldBe([name, ObjectId, "ix_legacy", root, 1]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - IndexEventSource: Should report a split whose separator would misorder its parent, once")]
    public async Task InsertAsync_SeparatorWouldMisorderParent_ShouldReportTheInvariantOnce()
    {
        // Arrange: twenty wide keys split the root once; its only separator is then overwritten
        // to sort above every key of the right child, so that child's next split promotes a
        // separator the parent cannot take (BTreeIndexTests, Split_SeparatorMisorderingParent_*).
        string name = UniqueName();
        var (harness, index) = await CreateIndexAsync(name, "ix_misorder");
        await using var harnessLifetime = harness;
        var setup = await harness.BeginAsync();
        for (long i = 0; i < 20; i++)
        {
            await index.InsertAsync(setup, WideKey(i * 100), (ulong)i);
        }

        await harness.CommitAsync(setup);
        long root = harness.IndexManager.ExportRegistrations().Single().RootPageId;
        OverwriteOnlySeparator(harness.Storage, root, WideKey(5_000));
        using var recorder = new IndexEventRecorder(EventLevel.Error);

        // Act
        var writer = await harness.BeginAsync();
        var failure = await Should.ThrowAsync<IndexException>(async () =>
        {
            for (long value = 5_001; value < 5_100; value++)
            {
                await index.InsertAsync(writer, WideKey(value), (ulong)value);
            }
        });
        await harness.RollbackAsync(writer);

        // Assert: the event carries the failure's message, which names pages, not keys.
        recorder.ShouldHaveNoInstrumentationError();
        var violated = recorder.For(name).ShouldHaveSingleItem();
        violated.EventName.ShouldBe("IndexInvariantViolated");
        violated.EventId.ShouldBe(5);
        violated.Level.ShouldBe(EventLevel.Error);
        violated.PayloadNames.ShouldBe(["database", "index", "pageId", "detail"]);
        violated.Payload.ShouldBe([name, "ix_misorder", root, failure.Message]);
        failure.Message.ShouldContain("misorder");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - IndexEventSource: Should report a purge of unproven writers once, and nothing for an empty one")]
    public async Task PurgeWritersAsync_UnprovenWriter_ShouldReportThePurgeOnce()
    {
        // Arrange: a writer's entries reach the pages through a committed bracket, as a crash
        // leaves an unproven writer's stolen pages behind.
        string name = UniqueName();
        var (harness, index) = await CreateIndexAsync(name, "ix_purge");
        await using var harnessLifetime = harness;
        var writer = await harness.BeginAsync();
        for (long i = 0; i < 5; i++)
        {
            await index.InsertAsync(writer, IndexKey.FromInt64(i), (ulong)i);
        }

        harness.GetStorageTransaction(writer).Commit();
        using var recorder = new IndexEventRecorder(EventLevel.Verbose);

        // Act
        long purged;
        using (var bracket = harness.Storage.BeginTransaction())
        {
            purged = await harness.IndexManager.PurgeWritersAsync(bracket, new HashSet<TransactionSequence> { writer.Sequence });
            await harness.IndexManager.PurgeWritersAsync(bracket, new HashSet<TransactionSequence>());
            bracket.Commit();
        }

        await harness.Manager.RollbackAsync(writer);

        // Assert
        recorder.ShouldHaveNoInstrumentationError();
        purged.ShouldBe(5);
        var event_ = recorder.For(name).ShouldHaveSingleItem();
        event_.EventName.ShouldBe("WritersPurged");
        event_.EventId.ShouldBe(8);
        event_.Level.ShouldBe(EventLevel.Verbose);
        event_.PayloadNames.ShouldBe(["database", "writers", "entriesRemoved", "durationMilliseconds"]);
        event_.Payload!.Take(3).ShouldBe([name, 1, purged]);
        ((double)event_.Payload![3]!).ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - IndexEventSource: Should write nothing below its enabled level")]
    public async Task CreateIndexAsync_ErrorListener_ShouldNotWriteVerboseEvents()
    {
        // Arrange
        string name = UniqueName();
        using var recorder = new IndexEventRecorder(EventLevel.Error);

        // Act
        var (harness, _) = await CreateIndexAsync(name, "ix_quiet", entries: 100);
        await using var harnessLifetime = harness;

        // Assert
        recorder.For(name).ShouldBeEmpty();
    }

    private static string UniqueName() => "ix-events-" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// The keywords an event declares: a listener's events also carry the runtime's reserved
    /// session bits (0xF000_0000_0000 and above), which no source declares.
    /// </summary>
    private static EventKeywords Declared(EventKeywords keywords) => (EventKeywords)((long)keywords & 0x0000_0FFF_FFFF_FFFF);

    private static bool IsInternalSplit(EventWrittenEventArgs e) => e.EventName == "PageSplit" && Equals(e.Payload?[3], false);

    private static async Task<(IndexTestHarness Harness, BTreeIndex Index)> CreateIndexAsync(string name, string index, int entries = 0)
    {
        var harness = new IndexTestHarness(name: name);
        var setup = await harness.BeginAsync();
        var created = await harness.IndexManager.CreateIndexAsync(setup, ObjectId, new IndexDefinition(index));
        for (long i = 0; i < entries; i++)
        {
            await created.InsertAsync(setup, IndexKey.FromInt64(i), (ulong)i);
        }

        await harness.CommitAsync(setup);
        return (harness, created);
    }

    /// <summary>A 508-byte key whose order is its value's: about fifteen fit on a node (as <c>BTreeIndexTests.WideKey</c>).</summary>
    private static IndexKey WideKey(long value)
    {
        var bytes = new byte[508];
        bytes.AsSpan(0, 500).Fill(0x2E);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(500), (ulong)value ^ 0x8000_0000_0000_0000UL);
        return new IndexKey(bytes);
    }

    private static void RewritePage(Storage.Storage storage, long pageId, LegacyBTreePages.BodyRewriter rewrite)
    {
        using var bracket = storage.BeginTransaction();
        using (var handle = storage.OpenPageForWrite(bracket, pageId))
        {
            rewrite(handle.Page.AsBodySpan());
            handle.MarkDirty();
        }

        bracket.Commit();
    }

    // The node layout the two helpers below read (BTreeNode): kind at 3, entry count at 4, the
    // leftmost child at 24, the directory at 32.
    private const int NodeKindOffset = 3;
    private const int NodeCountOffset = 4;
    private const int NodeLeftmostChildOffset = 24;
    private const int NodeDirectoryOffset = 32;
    private const byte LeafNodeKind = 1;
    private const byte InternalNodeKind = 2;

    private static long LeftmostLeaf(Storage.Storage storage, long root)
    {
        long current = root;
        while (true)
        {
            using var handle = storage.PageManager.GetPage(current);
            var body = handle.Page.AsBodySpan();
            if (body[NodeKindOffset] == LeafNodeKind)
            {
                return current;
            }

            current = BinaryPrimitives.ReadInt64LittleEndian(body[NodeLeftmostChildOffset..]);
        }
    }

    /// <summary>
    /// Overwrites the key bytes of the only separator of the internal node on
    /// <paramref name="pageId"/> with the leading bytes of <paramref name="replacement"/>, in a
    /// committed bracket of its own (as <c>BTreeIndexTests.OverwriteOnlySeparator</c>).
    /// </summary>
    private static void OverwriteOnlySeparator(Storage.Storage storage, long pageId, IndexKey replacement)
    {
        using var bracket = storage.BeginTransaction();
        using (var handle = storage.OpenPageForWrite(bracket, pageId))
        {
            var body = handle.Page.AsBodySpan();
            body[NodeKindOffset].ShouldBe(InternalNodeKind);
            BinaryPrimitives.ReadUInt16LittleEndian(body[NodeCountOffset..]).ShouldBe((ushort)1);

            int entry = BinaryPrimitives.ReadUInt16LittleEndian(body[NodeDirectoryOffset..]);
            int field = BinaryPrimitives.ReadUInt16LittleEndian(body[entry..]);
            (field >> 14).ShouldBe(0, "the separator keeps no tiebreaker");
            int length = field & 0x3FFF;
            length.ShouldBeLessThanOrEqualTo(replacement.Length);
            replacement.Encoded.Span[..length].CopyTo(body[(entry + 2)..]);
            handle.MarkDirty();
        }

        bracket.Commit();
    }

    /// <summary>Records the events the Indexing event source writes.</summary>
    private sealed class IndexEventRecorder : EventListener
    {
        private readonly ConcurrentQueue<EventWrittenEventArgs> _events = new();

        public IndexEventRecorder(EventLevel level)
        {
            EnableEvents(IndexEventSource.Log, level, EventKeywords.All);
        }

        public IReadOnlyList<EventWrittenEventArgs> Events => _events.ToArray();

        /// <summary>The events whose <c>database</c> payload is <paramref name="database"/>, in the order written.</summary>
        public EventWrittenEventArgs[] For(string database)
            => Events.Where(e => e.PayloadNames is { Count: > 0 } names && names[0] == "database" && Equals(e.Payload?[0], database)).ToArray();

        public void ShouldHaveNoInstrumentationError()
            => Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (ReferenceEquals(eventData.EventSource, IndexEventSource.Log))
            {
                _events.Enqueue(eventData);
            }
        }
    }
}
