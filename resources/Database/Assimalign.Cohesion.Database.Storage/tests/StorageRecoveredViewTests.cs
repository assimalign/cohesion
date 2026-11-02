using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Internal;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Storage.Tests;

public sealed class StorageRecoveredViewTests
{
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
