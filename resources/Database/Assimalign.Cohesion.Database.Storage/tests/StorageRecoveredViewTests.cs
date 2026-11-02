using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Threading;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Testing;

using Assimalign.Cohesion.Database.Storage.Internal;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Storage.Tests;

[Collection(nameof(StorageEventSourceCollection))]
public sealed class StorageRecoveredViewTests
{
    private readonly ITestOutputHelper _output;
    public StorageRecoveredViewTests(ITestOutputHelper output) => _output = output;
    [Theory(DisplayName = "Cohesion Test [Storage] - Probe: unchanged owner headers do not read an unavailable body (#1744)")]
    [InlineData(false)]
    [InlineData(true)]
    public void Open_UnreadBodyFailure_ShouldPreserveHeaderOnlyAvailability(bool readOnly)
    {
        // Arrange: normal open needs only this unchanged page's valid header.
        using var seed = TornStorage.Create();
        var image = seed.CaptureDurable();
        using var data = new BodyFaultStream(image.Data, writable: !readOnly);

        // Act.
        using var opened = ProbeStorage.Open(data, image.Journal);

        // Assert: both normal recovery and the probe open without touching the body.
        data.BodyReadAttempted.ShouldBeFalse();
        Should.Throw<IOException>(() => opened.Data.ReadPage((PageId)1L, new byte[Page.Size]));
        data.BodyReadAttempted.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Probe: a large unread journal retains identities and one scratch page (#1744)")]
    public void Probe_LargeJournalWithOneReadPage_ShouldRetainNoUnreadPageBytes()
    {
        // Arrange: 32 MiB of images, almost all for pages no gate reads.
        using var medium = new MemoryStream();
        using (var journal = StorageJournal.Create(medium, leaveOpen: true))
        {
            var image = new byte[Page.Size];
            new Random(1744).NextBytes(image);
            for (int page = 1; page <= 4096; page++)
            {
                journal.AppendPageImage(1, (PageId)page, JournalRecordType.FullPageImage, image);
            }
            journal.AppendCommit(1);
        }
        byte[] bytes = medium.ToArray();
        bytes.LongLength.ShouldBeGreaterThan(32L * 1024 * 1024);
        using var data = new StorageStream(new MemoryStream(new byte[Page.Size], writable: false));
        using var log = new StorageStream(new MemoryStream(bytes, writable: false));
        using var reader = StorageJournal.OpenReadOnly(log);
        using var probe = new StorageRecoveredStream(data);

        // Act.
        probe.Initialize(reader, new HashSet<long>(), CancellationToken.None);
        probe.Position = Page.Size;
        probe.ReadExactly(new byte[Page.Size]);

        // Assert: growing the journal adds references, never retained page buffers.
        probe.IndexedRecordCount.ShouldBe(4096);
        (probe.RetainedIndexBytes + probe.RetainedPageBytes).ShouldBeLessThanOrEqualTo(512L * 4096 + Page.Size + 128);
        probe.RetainedPageBytes.ShouldBe(Page.Size);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Probe: every recovered page equals physical replay across mixed histories (#1744)")]
    public void Probe_MixedCommittedAndUncommittedHistories_ShouldEqualPhysicalReplay()
    {
        // Arrange: include repeated images, deltas, committed images, reused sequences,
        // backwards LSNs, a protected anchor and pages whose on-disk bytes are torn.
        for (int seed = 0; seed < 32; seed++)
        {
            byte[] journalBytes = RecoveryJournalScenario.Create(seed);
            byte[] dataBytes = Enumerable.Repeat((byte)0x5a, 10 * Page.Size).ToArray();
            var anchors = new HashSet<long> { 9 };
            using var source = new StorageStream(new MemoryStream(dataBytes, writable: false));
            using var probeJournalStream = new StorageStream(new MemoryStream(journalBytes, writable: false));
            using var probeJournal = StorageJournal.OpenReadOnly(probeJournalStream);
            using var probe = new StorageRecoveredStream(source);
            probe.Initialize(probeJournal, anchors, CancellationToken.None);
            using var real = new StorageStream(new MemoryStream());
            real.Write(dataBytes);
            using var realJournal = StorageJournal.Create(new MemoryStream(journalBytes));

            // Act.
            StorageRecovery.Run(real, realJournal, forceDurable: false, anchors, cacheCapacity: 2);

            // Assert: compare every page a gate could read, including unchanged pages.
            probe.Length.ShouldBe(real.Length);
            var expected = new byte[Page.Size];
            var actual = new byte[Page.Size];
            for (long page = 0; page < real.Length / Page.Size; page++)
            {
                real.ReadPage((PageId)page, expected);
                probe.Position = page * Page.Size;
                probe.ReadExactly(actual);
                actual.ShouldBe(expected, $"seed {seed}, page {page}");
            }
            probe.RetainedPageBytes.ShouldBe(Page.Size);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Probe: torn current pages open, reject writes and retain one scratch page (#1744)")]
    public void Open_TornCurrentPageAndLargeJournal_ShouldRecoverWithoutWriting()
    {
        // Arrange: abandon a live storage with committed and stolen uncommitted bytes.
        using var original = TornStorage.Create();
        var location = original.Insert("committed");
        using var uncommitted = original.BeginTransaction();
        original.Update(uncommitted, location.PageId, location.SlotIndex, "abandoned");
        original.PageManager.FlushAll();
        var image = original.CaptureDurable();
        Array.Fill(image.Data, (byte)0x5a, checked((int)(long)location.PageId * Page.Size + 512), 512);
        byte[] beforeData = (byte[])image.Data.Clone();
        byte[] beforeJournal = (byte[])image.Journal.Clone();

        // Act.
        using (var probe = ProbeStorage.Open(image))
        using (var real = TornStorage.Open(image))
        {
            var actual = new byte[Page.Size];
            var expected = new byte[Page.Size];
            for (long page = 0; page < probe.Data.Length / Page.Size; page++)
            {
                probe.Data.ReadPage((PageId)page, actual);
                real.Data.ReadPage((PageId)page, expected);
                actual.ShouldBe(expected);
            }
            // Assert.
            probe.ProbeRetainedPageBytes.ShouldBe(Page.Size);
            probe.ProbeIndexedRecordCount.ShouldBeGreaterThan(0);
            Should.Throw<NotSupportedException>(() => probe.BeginTransaction());
            Should.Throw<NotSupportedException>(() => probe.BeginTransaction(42));
            Should.Throw<NotSupportedException>(() => probe.Log.AppendBegin(42));
            probe.Data.CanWrite.ShouldBeFalse();
            using (var handle = probe.PageManager.GetPage(location.PageId))
            {
                Should.Throw<NotSupportedException>(() => handle.MarkDirty());
                probe.DirtyPageCount.ShouldBe(0);
            }
            probe.Dispose();
            probe.Data.CanRead.ShouldBeFalse();
        }
        image.Data.ShouldBe(beforeData);
        image.Journal.ShouldBe(beforeJournal);
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Probe: small records retain eight bytes each plus bounded page metadata (#1744)")]
    [InlineData(1, 20000)]
    [InlineData(257, 80)]
    public void Probe_ManySmallRecords_ShouldBoundRetainedIndexBytes(int pageCount, int deltasPerPage)
    {
        // Arrange.
        byte[] bytes = SmallRecords(pageCount, deltasPerPage);
        using var source = new StorageStream(new MemoryStream(new byte[Page.Size], writable: false));
        using var journal = StorageJournal.OpenReadOnly(new StorageStream(new MemoryStream(bytes, writable: false)));
        using var probe = new StorageRecoveredStream(source);

        // Act.
        probe.Initialize(journal, new HashSet<long>(), CancellationToken.None);

        // Assert: charge allocated capacity, including slack, not just used entries.
        long records = (long)pageCount * (deltasPerPage + 1);
        _output.WriteLine($"{records} records, {pageCount} pages, {probe.RetainedIndexBytes} retained index bytes.");
        probe.IndexedRecordCount.ShouldBe(records);
        probe.RetainedIndexBytes.ShouldBeLessThanOrEqualTo(8 * records + 56L * pageCount + 256);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Probe: page replay reads sequential chunks and reuses record buffers (#1744)")]
    public void Probe_ManySmallRecords_ShouldBoundJournalReadCalls()
    {
        // Arrange: reset after indexing so this measures only lazy replay.
        byte[] bytes = SmallRecords(1, 20000);
        using var medium = new ReadCountingStream(bytes);
        using var journal = StorageJournal.OpenReadOnly(new StorageStream(medium));
        using var probe = new StorageRecoveredStream(new StorageStream(new MemoryStream(new byte[Page.Size], writable: false)));
        probe.Initialize(journal, new HashSet<long>(), CancellationToken.None);
        medium.ReadCalls = 0;
        medium.LengthReads = 0;

        // Act.
        probe.Position = Page.Size;
        probe.ReadExactly(new byte[Page.Size]);

        // Assert: a generous chunk ceiling, independent of machine speed or allocation counters.
        _output.WriteLine($"{probe.IndexedRecordCount} records, {medium.ReadCalls} journal reads, {medium.LengthReads} length queries, {bytes.Length} journal bytes.");
        medium.ReadCalls.ShouldBeLessThanOrEqualTo(2 + bytes.Length / (128 * 1024));
        medium.LengthReads.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Probe: preflight is absent from lifecycle telemetry and gauges (#1744)")]
    public void Open_ProbeThenPhysicalRecovery_ShouldReportOnlyOneRecovery()
    {
        // Arrange.
        (byte[] Data, byte[] Journal) image;
        using (var seed = TornStorage.Create()) { seed.Insert("committed"); image = seed.CaptureDurable(); }
        using var recorder = new EventSourceRecorder(StorageEventSource.Log, EventLevel.Verbose);
        long gauge = StorageEventSource.Log.CurrentStorages;

        // Act and assert: a refusal after this preflight would have no Storage lifecycle events.
        using (var probe = ProbeStorage.Open(image))
        {
            StorageEventSource.Log.CurrentStorages.ShouldBe(gauge);
            recorder.Events.ShouldBeEmpty();
        }
        recorder.Events.ShouldBeEmpty();
        using (var real = TornStorage.Open(image)) { StorageEventSource.Log.CurrentStorages.ShouldBe(gauge + 1); }

        // Assert.
        recorder.Events.Count(e => e.EventName == "RecoveryStart").ShouldBe(1);
        recorder.Events.Count(e => e.EventName == "RecoveryStop").ShouldBe(1);
        recorder.Events.Count(e => e.EventName == "StorageClosed").ShouldBe(1);
        StorageEventSource.Log.CurrentStorages.ShouldBe(gauge);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Recovery: missing image fails before a cache eviction writes another page (#1744)")]
    public void Run_MissingImageWithFullCache_ShouldThrowBeforeWriting()
    {
        // Arrange: page 1 remains live in the one-page cache when corrupt page 2 arrives.
        using var medium = new MemoryStream();
        using (var writer = StorageJournal.Create(medium, leaveOpen: true))
        {
            writer.AppendPageImage(1, (PageId)1L, JournalRecordType.FullPageImage, new byte[Page.Size]);
            writer.AppendPageRecord(1, (PageId)2L, JournalRecordType.PageDelta, new byte[8], out _);
            writer.AppendPageRecord(1, (PageId)1L, JournalRecordType.PageDelta, new byte[8], out _);
            writer.AppendCommit(1);
        }
        using var data = new MemoryStream();
        using var stream = new StorageStream(data);
        using var journal = StorageJournal.Create(new MemoryStream(medium.ToArray()));

        // Act.
        Should.Throw<StorageCorruptionException>(() => StorageRecovery.Run(stream, journal, false, cacheCapacity: 1));

        // Assert.
        data.Length.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Probe: retained-byte inspection does not allocate (#1744)")]
    public void RetainedIndexBytes_EmptyProbe_ShouldNotAllocate()
    {
        // Arrange.
        using var probe = new StorageRecoveredStream(new StorageStream(new MemoryStream(new byte[Page.Size], writable: false)));
        _ = probe.RetainedIndexBytes;
        using var fresh = new StorageRecoveredStream(new StorageStream(new MemoryStream(new byte[Page.Size], writable: false)));

        // Act.
        long before = GC.GetAllocatedBytesForCurrentThread();
        _ = fresh.RetainedIndexBytes;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert.
        allocated.ShouldBe(0);
    }

    private static byte[] SmallRecords(int pages, int deltas)
    {
        using var medium = new MemoryStream();
        using (var journal = StorageJournal.Create(medium, leaveOpen: true))
        {
            byte[] previous = new byte[Page.Size];
            byte[] next = new byte[Page.Size];
            byte[] payload = new byte[PageImageCodec.MaximumPayloadLength];
            for (int page = 1; page <= pages; page++)
            {
                Array.Clear(previous); Array.Clear(next);
                long lsn = journal.AppendPageImage(1, (PageId)page, JournalRecordType.FullPageImage, previous);
                for (int i = 0; i < deltas; i++)
                {
                    next[512] = (byte)(i + 1);
                    BinaryPrimitives.WriteInt64LittleEndian(payload, lsn);
                    int length = 8 + PageImageCodec.EncodeRuns(previous, next, payload.AsSpan(8));
                    lsn = journal.AppendPageRecord(1, (PageId)page, JournalRecordType.PageDelta, payload.AsSpan(0, length), out _);
                    next.CopyTo(previous, 0);
                }
            }
            journal.AppendCommit(1);
        }
        return medium.ToArray();
    }

    private sealed class ReadCountingStream : MemoryStream
    {
        internal ReadCountingStream(byte[] bytes) : base(bytes, writable: false) { }
        internal int ReadCalls { get; set; }
        internal int LengthReads { get; set; }
        public override long Length { get { LengthReads++; return base.Length; } }
        public override int Read(Span<byte> buffer) { ReadCalls++; return base.Read(buffer); }
    }

    private sealed class ProbeStorage : Storage
    {
        internal StorageJournal Log => WriteAheadLog;

        private ProbeStorage(byte[] data, byte[] journal)
            : base(StorageModel.Custom, new StorageStream(new MemoryStream(data, writable: false)),
                new StorageStream(new MemoryStream(journal, writable: false)),
                new StorageStream(new MemoryStream([], writable: false))) { }

        private ProbeStorage(Stream data, byte[] journal)
            : base(StorageModel.Custom, new StorageStream(data), new StorageStream(new MemoryStream(journal)),
                new StorageStream(new MemoryStream())) { }

        internal static ProbeStorage Open(Stream data, byte[] journal)
        {
            var storage = new ProbeStorage(data, journal);
            storage.OpenExisting(checkpointOnOpen: false);
            return storage;
        }

        internal static ProbeStorage Open((byte[] Data, byte[] Journal) image)
        {
            var storage = new ProbeStorage(image.Data, image.Journal);
            storage.OpenExisting();
            return storage;
        }
    }

    private sealed class BodyFaultStream : MemoryStream
    {
        internal BodyFaultStream(byte[] bytes, bool writable) : base(bytes, writable) { }
        internal bool BodyReadAttempted { get; private set; }
        public override int Read(Span<byte> buffer)
        {
            if (Position >= Page.Size && Position + buffer.Length > Page.Size + 512)
            {
                BodyReadAttempted = true;
                throw new IOException("The unrelated page body is unavailable.");
            }
            return base.Read(buffer);
        }
    }
}
