using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Threading;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Storage.Internal;
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Storage.Tests;

[Collection(nameof(StorageEventSourceCollection))]
public sealed class StorageProbeSecondPassTests
{
    private readonly ITestOutputHelper _output;

    public StorageProbeSecondPassTests(ITestOutputHelper output) => _output = output;

    [Theory(DisplayName = "Cohesion Test [Storage] - Probe: scattered and contiguous replay bound bytes and calls (#1744)")]
    [InlineData(4096, 8)]
    [InlineData(1, 20000)]
    public void Read_RoundRobinRecords_ShouldBoundBytesAndCalls(int pages, int deltas)
    {
        // Arrange: every round visits all pages before returning to the first one.
        byte[] bytes = Journal(pages, deltas);
        var frames = Frames(bytes).Where(frame => frame.Page > 0).GroupBy(frame => frame.Page)
            .ToDictionary(group => group.Key, group => group.ToArray());
        using var medium = new CountingStream(bytes);
        using var journal = StorageJournal.OpenReadOnly(new StorageStream(medium));
        using var probe = new StorageRecoveredStream(new StorageStream(new MemoryStream(new byte[Page.Size], writable: false)));
        probe.Initialize(journal, new HashSet<long>(), CancellationToken.None);
        var page = new byte[Page.Size];
        long totalBytes = 0, totalCalls = 0, maximumBytes = 0;
        bool bounded = true;

        // Act: measure only lazy replay, independently for every page.
        for (int id = 1; id <= pages; id++)
        {
            medium.Reset();
            probe.Position = (long)id * Page.Size;
            probe.ReadExactly(page);
            totalBytes += medium.BytesRead;
            totalCalls += medium.ReadCalls;
            maximumBytes = Math.Max(maximumBytes, medium.BytesRead);
            long frameBytes = frames[id].Sum(frame => (long)frame.Length);
            bounded &= medium.BytesRead <= frameBytes + 4096L * (frames[id].Length - 1);
            bounded &= medium.ReadCalls <= frames[id].Length;
            medium.LengthReads.ShouldBe(0);
        }

        // Assert: clustered frames also retain the existing chunked-call ceiling.
        _output.WriteLine($"{pages} pages x {deltas} deltas: {probe.IndexedRecordCount} records; {bytes.Length} journal bytes; {frames.Values.Sum(pageFrames => pageFrames.Sum(frame => (long)frame.Length))} page-frame bytes; {totalBytes} replay bytes; {totalCalls} replay calls; {maximumBytes} maximum bytes per page; {probe.RetainedIndexBytes} retained index bytes.");
        bounded.ShouldBeTrue("each page must stay within its own frame bytes plus at most 4 KiB per gap, and one read per record");
        probe.RetainedIndexBytes.ShouldBeLessThanOrEqualTo(8 * probe.IndexedRecordCount + 56L * pages + 256);
        if (pages == 1) { totalCalls.ShouldBeLessThanOrEqualTo(2 + bytes.Length / (128 * 1024)); }
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Pre-scan: an unclean journal stops at its first in-file full image (#1744)")]
    public void RequiresRecovery_UncleanJournal_ShouldReadOnlyFirstImage()
    {
        // Arrange: count distinct frame starts actually read, including buffered read-ahead.
        byte[] bytes = Journal(4096, 8);
        using var medium = new CountingStream(bytes);
        using var stream = new StorageStream(medium);

        // Act.
        bool required = StorageRecoveryJournal.RequiresRecovery(stream, 4097L * Page.Size);

        // Assert.
        _output.WriteLine($"Pre-scan: {medium.FramesRead} frames, {medium.BytesRead} bytes, {medium.ReadCalls} reads of {Frames(bytes).Count} frames.");
        required.ShouldBeTrue();
        medium.FramesRead.ShouldBeLessThanOrEqualTo(1);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Pre-scan: canceled empty journals honor cancellation (#1744)")]
    public void RequiresRecovery_CanceledEmptyJournal_ShouldThrowCancellation()
    {
        // Arrange.
        using var stream = new StorageStream(new MemoryStream());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Act and assert.
        Should.Throw<OperationCanceledException>(() => StorageRecoveryJournal.RequiresRecovery(stream, Page.Size, cancellation.Token));
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Probe: nearby frames coalesce without reading beyond the last frame (#1744)")]
    public void Read_NearbyPageFrames_ShouldReadOnlyTheirSpan()
    {
        // Arrange.
        byte[] bytes = Journal(2, 1);
        var frames = Frames(bytes).Where(frame => frame.Page == 1).ToArray();
        long span = frames[^1].Offset + frames[^1].Length - frames[0].Offset;
        using var medium = new CountingStream(bytes);
        using var journal = StorageJournal.OpenReadOnly(new StorageStream(medium));
        using var probe = new StorageRecoveredStream(new StorageStream(new MemoryStream(new byte[Page.Size], writable: false)));
        probe.Initialize(journal, new HashSet<long>(), CancellationToken.None);
        medium.Reset();

        // Act.
        probe.Position = Page.Size;
        probe.ReadExactly(new byte[Page.Size]);

        // Assert.
        medium.ReadCalls.ShouldBe(1);
        medium.BytesRead.ShouldBe(span);
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Probe: fill rejects both overflowing and incomplete page ranges (#1744)")]
    [InlineData(true)]
    [InlineData(false)]
    public void Initialize_ChangedRecordCount_ShouldRefuseCorruption(bool extra)
    {
        // Arrange: change only the second scan, simulating a violated immutable-source contract.
        byte[] original = Images(1, 2);
        byte[] changed = extra ? Images(1, 1, 2) : Images(2);
        using var medium = new SwapOnFillStream(original, changed);
        using var journal = StorageJournal.OpenReadOnly(new StorageStream(medium));
        using var probe = new StorageRecoveredStream(new StorageStream(new MemoryStream(new byte[3 * Page.Size], writable: false)));

        // Act.
        var failure = Should.Throw<StorageCorruptionException>(() => probe.Initialize(journal, new HashSet<long>(), CancellationToken.None));

        // Assert.
        failure.Code.ShouldBe("CK06003");
        failure.PageId.ShouldBe((PageId)1L);
        medium.Swapped.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Probe: replay validates frame prefix and requested page identity (#1744)")]
    [InlineData("magic")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("page")]
    public void Read_ChangedIndexedFrame_ShouldRefuseCorruption(string damage)
    {
        // Arrange: alter a frame after the two verified index scans.
        byte[] bytes = Images(1, 2);
        int offset = Frames(bytes).Single(frame => frame.Page == 2).Offset;
        using var journal = StorageJournal.OpenReadOnly(new StorageStream(new MemoryStream(bytes, writable: false)));
        using var probe = new StorageRecoveredStream(new StorageStream(new MemoryStream(new byte[3 * Page.Size], writable: false)));
        probe.Initialize(journal, new HashSet<long>(), CancellationToken.None);
        if (damage == "magic") { BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 4), 0); }
        else if (damage == "short") { BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), 1); }
        else if (damage == "long") { BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), int.MaxValue); }
        else { BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset + 8 + 18), 1); }

        // Act.
        probe.Position = 2 * Page.Size;
        var failure = Should.Throw<StorageCorruptionException>(() => probe.ReadExactly(new byte[Page.Size]));

        // Assert.
        failure.Code.ShouldBe("CK06003");
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Probe: version refusal reports the page and absolute frame offset (#1744)")]
    public void Read_ChangedIndexedVersion_ShouldNamePageFrame()
    {
        // Arrange: page two starts in the second slot of the flat index.
        byte[] bytes = Images(1, 2);
        int offset = Frames(bytes).Single(frame => frame.Page == 2).Offset;
        using var journal = StorageJournal.OpenReadOnly(new StorageStream(new MemoryStream(bytes, writable: false)));
        using var probe = new StorageRecoveredStream(new StorageStream(new MemoryStream(new byte[3 * Page.Size], writable: false)));
        probe.Initialize(journal, new HashSet<long>(), CancellationToken.None);
        bytes[offset + 8] = 0;

        // Act.
        probe.Position = 2 * Page.Size;
        var failure = Should.Throw<StorageFormatException>(() => probe.ReadExactly(new byte[Page.Size]));

        // Assert.
        failure.Message.ShouldContain("Page 2", Case.Sensitive);
        failure.Message.ShouldContain($"byte offset {offset}", Case.Sensitive);
        failure.FoundVersion.ShouldBe(0);
        failure.SupportedVersion.ShouldBe(StorageRecoveryJournal.currentVersion);
        failure.Message.ShouldNotContain("newer", Case.Insensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Probe: oversized page frames cannot enter the packed index (#1744)")]
    public void Initialize_OversizedPageFrame_ShouldRefuseCorruption()
    {
        // Arrange: the append API can carry an invalid image payload with a valid frame CRC.
        using var medium = new MemoryStream();
        using (var writer = StorageJournal.Create(medium, leaveOpen: true))
        {
            writer.AppendPageRecord(1, (PageId)1L, JournalRecordType.FullPageImage, new byte[65536], out _);
        }
        using var journal = StorageJournal.OpenReadOnly(new StorageStream(new MemoryStream(medium.ToArray(), writable: false)));
        using var probe = new StorageRecoveredStream(new StorageStream(new MemoryStream(new byte[2 * Page.Size], writable: false)));

        // Act.
        var failure = Should.Throw<StorageCorruptionException>(() => probe.Initialize(journal, new HashSet<long>(), CancellationToken.None));

        // Assert.
        failure.PageId.ShouldBe((PageId)1L);
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Probe pool: page reads stay suppressed and checksum failures remain visible (#1744)")]
    [InlineData(false)]
    [InlineData(true)]
    public void Pin_ReadOnlyPool_ShouldNotReportPageReads(bool corrupt)
    {
        // Arrange.
        string name = "probe-pool-" + Guid.NewGuid().ToString("N");
        var bytes = new byte[Page.Size];
        PageChecksum.Stamp(bytes);
        if (corrupt) { bytes[512] ^= 1; }
        using var stream = new StorageStream(new MemoryStream(bytes, writable: false));
        using var pool = new StorageBufferPool(1) { IsReadOnly = true, StorageName = name };
        using var recorder = new EventSourceRecorder(StorageEventSource.Log, EventLevel.Verbose);
        long before = StorageEventSource.Log.PageReads;

        // Act.
        if (corrupt) { Should.Throw<StorageCorruptionException>(() => pool.Pin((PageId)0L, stream)); }
        else { using var handle = pool.Pin((PageId)0L, stream); }

        // Assert.
        (StorageEventSource.Log.PageReads - before).ShouldBe(0);
        var events = recorder.Events.Where(e => e.PayloadNames?.IndexOf("database") is >= 0
            && Equals(e.Payload![e.PayloadNames.IndexOf("database")], name)).ToArray();
        if (corrupt)
        {
            var checksum = events.ShouldHaveSingleItem();
            checksum.EventName.ShouldBe("PageChecksumFailed");
            checksum.Level.ShouldBe(EventLevel.Error);
        }
        else { events.ShouldBeEmpty(); }
    }

    private static byte[] Journal(int pages, int deltas)
    {
        using var medium = new MemoryStream();
        using (var writer = StorageJournal.Create(medium, leaveOpen: true))
        {
            var previous = new byte[Page.Size];
            var next = new byte[Page.Size];
            var payload = new byte[PageImageCodec.MaximumPayloadLength];
            var lsns = new long[pages];
            for (int page = 0; page < pages; page++)
            {
                lsns[page] = writer.AppendPageImage(1, (PageId)(page + 1L), JournalRecordType.FullPageImage, previous);
            }
            for (int delta = 0; delta < deltas; delta++)
            {
                next[512] = (byte)(delta + 1);
                int length = 8 + PageImageCodec.EncodeRuns(previous, next, payload.AsSpan(8));
                for (int page = 0; page < pages; page++)
                {
                    BinaryPrimitives.WriteInt64LittleEndian(payload, lsns[page]);
                    lsns[page] = writer.AppendPageRecord(1, (PageId)(page + 1L), JournalRecordType.PageDelta, payload.AsSpan(0, length), out _);
                }
                next.CopyTo(previous, 0);
            }
            writer.AppendCommit(1);
        }
        return medium.ToArray();
    }

    private static byte[] Images(params int[] pages)
    {
        using var medium = new MemoryStream();
        using (var writer = StorageJournal.Create(medium, leaveOpen: true))
        {
            foreach (int page in pages) { writer.AppendPageImage(1, (PageId)(long)page, JournalRecordType.FullPageImage, new byte[Page.Size]); }
            writer.AppendCommit(1);
        }
        return medium.ToArray();
    }

    private static List<(int Offset, int Length, long Page)> Frames(byte[] bytes)
    {
        var frames = new List<(int, int, long)>();
        for (int offset = 0; offset < bytes.Length;)
        {
            int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset)) + 12;
            frames.Add((offset, length, BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset + 8 + 18))));
            offset += length;
        }
        return frames;
    }

    private sealed class CountingStream : MemoryStream
    {
        private readonly List<(int Offset, int Length, long Page)> _frames;
        private readonly HashSet<int> _framesRead = [];
        internal CountingStream(byte[] bytes) : base(bytes, writable: false) => _frames = Frames(bytes);
        internal long BytesRead { get; private set; }
        internal int ReadCalls { get; private set; }
        internal int LengthReads { get; private set; }
        internal int FramesRead => _framesRead.Count;
        internal void Reset() { BytesRead = 0; ReadCalls = 0; LengthReads = 0; _framesRead.Clear(); }
        public override long Length { get { LengthReads++; return base.Length; } }
        public override int Read(Span<byte> buffer)
        {
            long start = Position;
            int read = base.Read(buffer);
            BytesRead += read;
            ReadCalls++;
            // Only a scan from zero needs frame-work accounting; replay measures bytes/calls.
            if (start == 0)
            {
                foreach (var frame in _frames.Where(frame => frame.Offset < start + read)) { _framesRead.Add(frame.Offset); }
            }
            else if (_framesRead.Count > 0)
            {
                foreach (var frame in _frames.Where(frame => frame.Offset >= start && frame.Offset < start + read)) { _framesRead.Add(frame.Offset); }
            }
            return read;
        }
    }

    private sealed class SwapOnFillStream : MemoryStream
    {
        private readonly byte[] _changed;
        private int _scans;
        internal bool Swapped { get; private set; }
        internal SwapOnFillStream(byte[] original, byte[] changed)
            : base(Math.Max(original.Length, changed.Length))
        {
            Write(original);
            Position = 0;
            _changed = changed;
        }
        public override long Length
        {
            get
            {
                if (++_scans == 2)
                {
                    base.SetLength(0);
                    base.Position = 0;
                    base.Write(_changed);
                    base.Position = 0;
                    Swapped = true;
                }
                return base.Length;
            }
        }
    }
}
